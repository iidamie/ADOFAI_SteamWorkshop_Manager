namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private CancellationTokenSource? _loginCancellation;
    private long _loginGeneration;
    private bool _reconnecting;

    private void BeginConnection(Func<CancellationToken, Task<SteamKitConnection>> createConnection, bool automaticLogin)
    {
        CancellationTokenSource loginCancellation;
        long generation;
        lock (_stateLock)
        {
            if (_loginCancellation != null || _connection != null || _unloadCancellation.IsCancellationRequested) return;
            loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_unloadCancellation.Token);
            _loginCancellation = loginCancellation;
            generation = ++_loginGeneration;
            _loginBusy = true;
            _reconnecting = automaticLogin;
            _authPrompt = null;
            _status = automaticLogin ? "正在使用保存的 Steam 会话自动登录…" : "正在连接 Steam…";
        }
        _ = Task.Run(() => RunLoginSupervisorAsync(createConnection, automaticLogin, loginCancellation, generation));
    }

    private async Task RunLoginSupervisorAsync(Func<CancellationToken, Task<SteamKitConnection>> createConnection,
        bool automaticLogin, CancellationTokenSource loginCancellation, long generation)
    {
        var lifetime = loginCancellation.Token;
        var retry = 0;
        var hasLoggedIn = false;
        var reason = string.Empty;
        try
        {
            while (true)
            {
                lifetime.ThrowIfCancellationRequested();
                if (retry > 0)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(30000, 2000 * (1 << Math.Min(retry - 1, 4)))
                        + Random.Shared.Next(150, 650));
                    SetLoginStatus(generation, $"{reason} {delay.TotalSeconds:F1} 秒后自动重连（第 {retry} 次），下载断点已保留。");
                    await Task.Delay(delay, lifetime).ConfigureAwait(false);
                }

                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                var token = sessionCancellation.Token;
                SteamKitConnection? connection = null;
                Task? sessionWork = null;
                var retryable = true;
                try
                {
                    lock (_stateLock)
                    {
                        EnsureLoginGeneration(generation, lifetime);
                        _sessionCancellation = sessionCancellation;
                        _loginBusy = true;
                        _reconnecting = hasLoggedIn || automaticLogin || retry > 0;
                        _status = _reconnecting ? "正在重新连接 Steam…" : "正在登录 Steam…";
                    }
                    connection = await createConnection(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!connection.IsConnected)
                    {
                        if (connection.Disconnected.IsCompleted)
                            throw new SteamSessionDisconnectedException(await connection.Disconnected.ConfigureAwait(false));
                        throw new IOException("Steam 登录完成时连接已中断。");
                    }
                    var savedLogin = connection.SavedLogin;
                    var restored = hasLoggedIn;
                    lock (_stateLock)
                    {
                        EnsureLoginGeneration(generation, lifetime);
                        // Only a refresh token is captured by subsequent attempts; release the password closure.
                        createConnection = cancellation => SteamKitConnection.LoginWithRefreshTokenAsync(savedLogin, cancellation);
                        _connection = connection;
                        _loginBusy = false;
                        _reconnecting = false;
                        _password = string.Empty;
                        PersistSavedLogin(savedLogin);
                        _status = restored ? "Steam 已重新连接，正在恢复订阅和下载…" : "Steam 登录成功，正在读取订阅…";
                    }
                    hasLoggedIn = true;
                    automaticLogin = true;
                    var connectedAt = DateTimeOffset.UtcNow;
                    var active = connection;
                    sessionWork = Task.Run(() => MaintainConnectedSessionAsync(active, restored, token), CancellationToken.None);
                    var disconnected = connection.Disconnected.WaitAsync(token);
                    var finished = await Task.WhenAny(disconnected, sessionWork).ConfigureAwait(false);
                    if (finished == sessionWork)
                    {
                        await sessionWork.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        throw new IOException("Steam 后台会话已停止。");
                    }
                    var loss = await disconnected.ConfigureAwait(false);
                    reason = loss.Message;
                    retryable = loss.Retryable;
                    if (DateTimeOffset.UtcNow - connectedAt >= TimeSpan.FromMinutes(1)) retry = 0;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    retryable = exception switch
                    {
                        SteamLogonRejectedException logon => logon.Retryable,
                        SteamSessionDisconnectedException disconnected => disconnected.Retryable,
                        SteamKit2.Authentication.AuthenticationException authentication => SteamLogonRejectedException.IsTemporary(authentication.Result),
                        _ => DownloadNetworkRetry.IsTransient(exception, lifetime),
                    };
                    reason = exception is SteamLogonRejectedException or SteamSessionDisconnectedException ? exception.Message
                        : retryable ? "Steam 网络连接失败。" : $"Steam 登录失败：{exception.GetBaseException().Message}";
                }
                finally
                {
                    // Detach and suspend only this intent, before its token is canceled.
                    lock (_stateLock)
                    {
                        if (_loginGeneration == generation)
                        {
                            _downloads?.SuspendSession();
                            _connection = null;
                            _sessionCancellation = null;
                            _subscriptions.Clear();
                            _authPrompt?.Completion.TrySetCanceled();
                            _authPrompt = null;
                            _loginBusy = true;
                            _reconnecting = true;
                            CancelWorkshopPresentation();
                            CancelWorkshopSearch();
                        }
                    }
                    TryCancel(sessionCancellation);
                    connection?.Dispose();
                    if (sessionWork != null)
                    {
                        try { await sessionWork.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch { /* The supervisor owns the final status and retry decision. */ }
                    }
                }
                lifetime.ThrowIfCancellationRequested();
                if (!retryable)
                {
                    SetLoginStatus(generation, $"{reason} 自动重连已停止，请重新登录或完成 Steam 验证。");
                    return;
                }
                if (retry < int.MaxValue) retry++;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetLoginStatus(generation, $"Steam 自动重连停止：{exception.GetBaseException().Message}");
        }
        finally
        {
            lock (_stateLock)
            {
                if (_loginGeneration == generation)
                {
                    _loginCancellation = null;
                    _loginBusy = false;
                    _reconnecting = false;
                    _password = string.Empty;
                    _authPrompt = null;
                }
            }
            loginCancellation.Dispose();
        }
    }

    private async Task MaintainConnectedSessionAsync(SteamKitConnection connection, bool restored, CancellationToken token)
    {
        bool queueDownloads;
        lock (_stateLock) queueDownloads = !restored || _settings.AutoSync;
        var subscriptionsReady = await SynchronizeAsync(token, queueDownloads, waitForGate: true).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if (!ReferenceEquals(_connection, connection) || !connection.IsConnected) return;
            _downloads?.ResumeForSession(connection, token, subscribedIds: null);
            if (_authStoreError != null) _status = $"Steam 已登录，自动重连可用，但会话未能安全保存：{_authStoreError}";
        }
        while (!subscriptionsReady)
        {
            SetSessionStatus(token, "Steam 已连接，30 秒后重试读取订阅；主动下载可继续，订阅任务等待完整列表校验。");
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            lock (_stateLock) queueDownloads = !restored || _settings.AutoSync;
            subscriptionsReady = await SynchronizeAsync(token, queueDownloads, waitForGate: true).ConfigureAwait(false);
        }
        await AutoSyncLoopAsync(token).ConfigureAwait(false);
    }

    private void SetLoginStatus(long generation, string status)
    {
        lock (_stateLock) if (_loginGeneration == generation && !_unloadCancellation.IsCancellationRequested) _status = status;
    }

    private void SetSessionStatus(CancellationToken token, string status)
    {
        lock (_stateLock)
            if (!token.IsCancellationRequested && _sessionCancellation != null && _sessionCancellation.Token == token)
                _status = status;
    }

    // Called with _stateLock held; token cancellation can race with UI detachment.
    private void EnsureSessionToken(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_sessionCancellation == null || _sessionCancellation.Token != token)
            throw new OperationCanceledException(token);
    }

    private void EnsureLoginGeneration(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_loginGeneration != generation) throw new OperationCanceledException(token);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
