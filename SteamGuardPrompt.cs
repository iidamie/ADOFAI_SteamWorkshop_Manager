using SteamKit2.Authentication;

namespace SteamWorkshopManager;

internal sealed class SteamGuardPrompt : IAuthenticator
{
    private readonly Func<string, bool, CancellationToken, Task<string>> _requestCode;
    private readonly Action<string> _setStatus;
    private readonly CancellationToken _cancellationToken;

    public SteamGuardPrompt(
        Func<string, bool, CancellationToken, Task<string>> requestCode,
        Action<string> setStatus,
        CancellationToken cancellationToken)
    {
        _requestCode = requestCode;
        _setStatus = setStatus;
        _cancellationToken = cancellationToken;
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        => _requestCode("请输入 Steam 手机令牌中的验证码。", previousCodeWasIncorrect, _cancellationToken);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => _requestCode($"请输入发送到 {email} 的 Steam Guard 邮件验证码。", previousCodeWasIncorrect, _cancellationToken);

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        _setStatus("请在 Steam 手机应用中确认本次登录。");
        return Task.FromResult(true);
    }
}
