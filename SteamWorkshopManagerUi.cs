using System.Numerics;
using ImGuiNET;

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private string _localQuery = string.Empty;
    private string _levelsDirectoryEdit = WorkshopLevelInstaller.DefaultLevelsDirectory;
    private string _subscriptionQuery = string.Empty;
    private string _workshopQuery = string.Empty;
    private string _workshopLink = string.Empty;
    private string _lastQueuedLink = string.Empty;
    private string _taskQuery = string.Empty;
    private int _localPage = 1;
    private int _subscriptionPage = 1;
    private int _taskPage = 1;
    private int _operationPage = 1;
    private int _taskFilter;
    private bool _autoDownloadLink = true;
    private DateTimeOffset _linkEditedAt;

    private void DrawManagerContents()
    {
        EnsureLocalLibraryRefresh();
        string status;
        bool connected;
        bool needsGuard;
        lock (_stateLock)
        {
            status = _status;
            connected = _connection is { IsConnected: true };
            needsGuard = _authPrompt != null;
        }
        if (DrawManagerHeader(status, connected, needsGuard))
        {
            CloseManagerWindow();
            return;
        }
        ImGui.Separator();
        if (_localDetails != null) { DrawLocalDetails(); return; }
        if (_workshopDetails != null) { DrawWorkshopDetails(); return; }
        if (!ImGui.BeginTabBar("##workshop-manager-tabs", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        if (ImGui.BeginTabItem("本地谱面"))
        {
            DrawInstalledLevels();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("创意工坊"))
        {
            DrawWorkshopBrowser(connected);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("已订阅"))
        {
            DrawSubscriptions();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("下载管理"))
        {
            DrawTasks();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("账号与设置"))
        {
            DrawLoginSection();
            ImGui.Separator();
            DrawSyncSettings();
            _updateService?.DrawGui();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private bool DrawManagerHeader(string status, bool connected, bool needsGuard)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var buttonWidth = Math.Min(Math.Max(96, ImGui.CalcTextSize("关闭").X
            + ImGui.GetStyle().FramePadding.X * 2), Math.Max(1, available * .4f));
        var rightInset = Math.Min(24, Math.Max(0, available * .06f));
        var close = false;
        if (ImGui.BeginTable("##manager-header", 2,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.NoPadOuterX))
        {
            ImGui.TableSetupColumn("##manager-status", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##manager-close", ImGuiTableColumnFlags.WidthFixed, buttonWidth + rightInset);
            ImGui.TableNextColumn();
            ManagerMuted($"{(connected ? "Steam 已连接" : "Steam 未登录 · 本地管理可用")} · {Version}");
            ManagerWrapped(status);
            if (needsGuard) ManagerWrapped("需要 Steam Guard 验证，请打开“账号与设置”完成登录。");
            ImGui.TableNextColumn();
            close = ImGui.Button("关闭##manager-header", new Vector2(buttonWidth, ManagerButtonHeight()));
            ImGui.EndTable();
        }
        return close;
    }

    private static void DrawPager(string id, ref int page, int total)
    {
        page = ManagerPaging.Clamp(page, total);
        ImGui.PushID(id);
        var current = page;
        ManagerToolbar(new ManagerAction("上一页", () => current--, page > 1),
            new ManagerAction("下一页", () => current++, page < ManagerPaging.Count(total)));
        page = current;
        ManagerMuted($"{page} / {ManagerPaging.Count(total)} 页 · 共 {total} 项");
        ImGui.PopID();
    }

    private void DrawInstalledLevels()
    {
        InstalledWorkshopLevel[] levels;
        bool busy;
        string error;
        lock (_stateLock)
        {
            levels = _localLibrary.ToArray();
            busy = _libraryBusy;
            error = _libraryError;
        }
        if (ManagerSearch(busy ? "查找本地谱面 · 正在读取…" : $"查找本地谱面 · {levels.Length} 个项目",
            "##local-query", ref _localQuery)) _localPage = 1;
        if (!string.IsNullOrWhiteSpace(error)) ManagerWrapped(error);
        var filtered = levels.Where(level => ManagerPaging.Matches(_localQuery, level.Title, level.PublishedFileId,
            level.Folder, string.Join(" ", level.ChartFiles ?? [level.ChartFileName]))).ToArray();
        DrawPager("local-pages", ref _localPage, filtered.Length);
        var visible = filtered.Skip((_localPage - 1) * ManagerPaging.PageSize).Take(ManagerPaging.PageSize).ToArray();
        int selectedCount;
        lock (_stateLock) selectedCount = _selectedDeleteFolders.Count;
        ManagerToolbar(new ManagerAction("勾选本页##local", () =>
        {
            lock (_stateLock) _selectedDeleteFolders.UnionWith(visible.Select(level => level.Folder));
        }), new ManagerAction("清空选择##local", () =>
        {
            lock (_stateLock) _selectedDeleteFolders.Clear();
        }), new ManagerAction($"删除所选（{selectedCount}）", StartDeleteSelectedLevels, selectedCount > 0));
        ManagerMuted("点击项目查看详情；勾选后可批量删除。");
        ImGui.BeginChild("##local-levels-page", ManagerListSize(), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        if (filtered.Length == 0) ManagerWrapped("没有匹配的已下载谱面。");
        foreach (var level in visible)
        {
            ImGui.PushID(level.Folder);
            bool selected;
            lock (_stateLock) selected = _selectedDeleteFolders.Contains(level.Folder);
            if (ImGui.Checkbox("##select-local", ref selected))
            {
                lock (_stateLock)
                {
                    if (selected) _selectedDeleteFolders.Add(level.Folder);
                    else _selectedDeleteFolders.Remove(level.Folder);
                }
            }
            ImGui.SameLine();
            if (ManagerProjectRow(string.IsNullOrWhiteSpace(level.Title) ? level.Folder : level.Title,
                $"{level.Folder} · {(level.ChartFiles?.Count ?? 1)} 个谱面 · {(level.KeepWithoutSubscription ? "独立保留" : "随订阅同步" )}"))
            {
                _localDetails = level;
                _confirmLocalDelete = false;
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void DrawSubscriptions()
    {
        WorkshopItem[] subscriptions;
        HashSet<string> selected;
        bool onlySelected;
        lock (_stateLock)
        {
            subscriptions = _subscriptions.ToArray();
            selected = _settings.SelectedPublishedFileIds.ToHashSet(StringComparer.Ordinal);
            onlySelected = _settings.DownloadOnlySelected;
        }
        if (ManagerSearch("查找已订阅谱面", "##subscription-query", ref _subscriptionQuery)) _subscriptionPage = 1;
        ManagerToolbar(new ManagerAction("刷新订阅列表", () => StartManualSync(queueDownloads: false)),
            new ManagerAction("按设置同步", () => StartManualSync()));
        ManagerMuted(onlySelected ? "自动同步只下载已勾选项目。" : "自动同步下载全部订阅，可在账号与设置页切换。");
        var filtered = subscriptions.Where(item => ManagerPaging.Matches(_subscriptionQuery, item.Title,
            item.PublishedFileId, item.FileName)).ToArray();
        DrawPager("subscription-pages", ref _subscriptionPage, filtered.Length);
        var visible = filtered.Skip((_subscriptionPage - 1) * ManagerPaging.PageSize).Take(ManagerPaging.PageSize).ToArray();
        ManagerToolbar(new ManagerAction("勾选本页##subscriptions", () =>
        {
            lock (_stateLock)
                _settings.SelectedPublishedFileIds = selected.Union(visible.Select(item => item.PublishedFileId)).ToList();
            SaveSettings();
            selected.UnionWith(visible.Select(item => item.PublishedFileId));
        }), new ManagerAction("全选订阅", () => { SetAllDownloadSelection(true); selected = subscriptions.Select(item => item.PublishedFileId).ToHashSet(); }),
            new ManagerAction("清空勾选", () => { SetAllDownloadSelection(false); selected.Clear(); }));
        ManagerToolbar(new ManagerAction("下载全部勾选项目", () =>
        {
            foreach (var item in subscriptions.Where(item => selected.Contains(item.PublishedFileId)))
                EnqueueDownload(item.PublishedFileId, keepWithoutSubscription: false, item);
        }));
        ManagerMuted($"已勾选 {subscriptions.Count(item => selected.Contains(item.PublishedFileId))} 项");
        ImGui.BeginChild("##subscriptions-page", ManagerListSize(), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        if (filtered.Length == 0) ManagerWrapped("没有匹配的订阅。登录后可刷新列表。");
        foreach (var item in visible)
        {
            ImGui.PushID(item.PublishedFileId);
            var isSelected = selected.Contains(item.PublishedFileId);
            if (ImGui.Checkbox("##select-subscription", ref isSelected)) SetDownloadSelection(item.PublishedFileId, isSelected);
            ImGui.SameLine();
            if (ManagerProjectRow(string.IsNullOrWhiteSpace(item.Title) ? item.PublishedFileId : item.Title,
                $"ID {item.PublishedFileId} · {FormatSize(item.FileSize)}"))
            {
                _workshopDetails = item;
                _detailsFromSubscription = true;
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void DrawWorkshopBrowser(bool connected)
    {
        if (ManagerSearch("工坊链接或项目 ID", "##workshop-link", ref _workshopLink, 2048))
        {
            _linkEditedAt = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(_workshopLink)) _lastQueuedLink = string.Empty;
        }
        var validLink = WorkshopLinkParser.TryParse(_workshopLink, out var linkedId);
        ManagerToolbar(new ManagerAction("下载链接项目", () => QueueLinkDownload(linkedId), validLink));
        ImGui.Checkbox("粘贴有效链接后自动加入下载", ref _autoDownloadLink);
        ManagerMuted(validLink ? $"已识别项目 ID：{linkedId}" : "支持 Steam 工坊链接、steam:// 链接或数字 ID");
        if (_autoDownloadLink && connected && validLink && _workshopLink.Contains("://", StringComparison.Ordinal)
            && _workshopLink.Trim() != _lastQueuedLink && DateTimeOffset.UtcNow - _linkEditedAt > TimeSpan.FromMilliseconds(800))
            QueueLinkDownload(linkedId);
        if (!connected) ManagerWrapped("请在账号与设置页登录 Steam，随后可搜索和下载。下载权限由 Steam 确认。");
        ImGui.Separator();
        var submit = ManagerSearch("查找创意工坊谱面", "##workshop-query", ref _workshopQuery, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        if (submit) StartWorkshopSearch(_workshopQuery);
        ManagerToolbar(new ManagerAction("搜索工坊", () => StartWorkshopSearch(_workshopQuery)),
            new ManagerAction("浏览最新谱面", () => { _workshopQuery = string.Empty; StartWorkshopSearch(string.Empty); }));

        WorkshopSearchPage? results;
        bool searching;
        string error;
        string activeQuery;
        HashSet<string> installedIds;
        lock (_stateLock)
        {
            results = _workshopResults;
            searching = _workshopSearching;
            error = _workshopError;
            activeQuery = _activeWorkshopQuery;
            installedIds = _localLibrary.Select(level => level.PublishedFileId).ToHashSet(StringComparer.Ordinal);
        }
        if (!string.IsNullOrWhiteSpace(error)) ManagerWrapped(error);
        if (searching) ManagerMuted("正在搜索 Steam 创意工坊…");
        if (results == null) return;
        var pages = Math.Max(1L, ((long)results.Total + results.PageSize - 1) / results.PageSize);
        ManagerToolbar(new ManagerAction("上一页##workshop", () => StartWorkshopSearch(activeQuery, results.Page - 1), !searching && results.Page > 1),
            new ManagerAction("下一页##workshop", () => StartWorkshopSearch(activeQuery, results.Page + 1), !searching && results.HasNext));
        ManagerMuted($"第 {results.Page} / {pages} 页 · Steam 返回 {results.Total} 项");
        ImGui.BeginChild("##workshop-search-results", ManagerListSize(), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        if (results.Items.Count == 0) ManagerWrapped("没有找到可下载的 ADOFAI 谱面。");
        foreach (var item in results.Items)
        {
            ImGui.PushID(item.PublishedFileId);
            if (ManagerProjectRow(item.Title,
                $"ID {item.PublishedFileId} · {FormatSize(item.FileSize)}{(installedIds.Contains(item.PublishedFileId) ? " · 已下载" : string.Empty)}"))
            {
                _workshopDetails = item;
                _detailsFromSubscription = false;
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void QueueLinkDownload(ulong id)
    {
        if (EnqueueDownload(id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            _lastQueuedLink = _workshopLink.Trim();
    }

    private void DrawTasks()
    {
        if (_downloads == null) return;
        var tasks = _downloads.GetSnapshots();
        if (ManagerSearch("查找下载任务", "##task-query", ref _taskQuery)) _taskPage = 1;
        string[] filters = ["全部", "进行中", "失败", "已结束"];
        ManagerSectionTitle("显示状态");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##task-filter", ref _taskFilter, filters, filters.Length)) _taskPage = 1;
        ManagerToolbar(new ManagerAction(_downloads.IsPaused ? "继续队列" : "暂停后续任务", () => _downloads.SetPaused(!_downloads.IsPaused)),
            new ManagerAction("取消全部未完成任务", _downloads.CancelAll), new ManagerAction("清除已结束记录", _downloads.ClearFinished));
        ManagerMuted($"等待 {tasks.Count(task => task.State == DownloadState.Queued)} · 下载中 {tasks.Count(task => task.State == DownloadState.Running)} · 失败 {tasks.Count(task => task.State == DownloadState.Failed)}");
        if (_downloads.IsPaused) ManagerWrapped("队列已暂停，当前任务继续完成。点击继续队列恢复等待任务。");
        var filtered = tasks.Where(task => ManagerPaging.Matches(_taskQuery, task.Title, task.PublishedFileId, task.Message)
            && (_taskFilter switch { 1 => !task.IsTerminal, 2 => task.State == DownloadState.Failed, 3 => task.IsTerminal, _ => true }))
            .OrderBy(task => task.State == DownloadState.Running ? 0 : task.State == DownloadState.Queued ? 1 : 2)
            .ThenBy(task => task.IsTerminal ? -task.Id : task.Id).ToArray();
        DrawPager("download-pages", ref _taskPage, filtered.Length);
        ImGui.BeginChild("##download-tasks-page", ManagerListSize(ImGui.GetFrameHeightWithSpacing() * 2), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        if (filtered.Length == 0) ManagerWrapped("没有匹配的下载任务。");
        foreach (var task in filtered.Skip((_taskPage - 1) * ManagerPaging.PageSize).Take(ManagerPaging.PageSize))
        {
            ImGui.PushID($"job-{task.Id}");
            ManagerWrapped($"{task.Title} · {task.StatusText}");
            ManagerMuted($"ID {task.PublishedFileId} · {(task.KeepWithoutSubscription ? "主动下载" : "订阅同步")}");
            if (task.State == DownloadState.Running) ImGui.ProgressBar((float)task.Progress, new Vector2(-1, 0), $"{task.Progress:P0}");
            ManagerWrapped(task.Message);
            if (!task.IsTerminal) ManagerToolbar(new ManagerAction("取消任务", () => _downloads.Cancel(task.Id)));
            if (task.State is DownloadState.Failed or DownloadState.Canceled)
                ManagerToolbar(new ManagerAction("重试下载", () => EnqueueDownload(task.PublishedFileId, task.KeepWithoutSubscription)));
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (ImGui.CollapsingHeader("同步与删除记录"))
        {
            WorkshopTaskSnapshot[] operations;
            lock (_stateLock) operations = _tasks.OrderByDescending(task => task.TaskId).ToArray();
            DrawPager("operation-pages", ref _operationPage, operations.Length);
            foreach (var operation in operations.Skip((_operationPage - 1) * ManagerPaging.PageSize).Take(ManagerPaging.PageSize))
                ManagerWrapped($"{operation.Title} · {operation.Status} · {operation.Message}");
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MiB" : $"{Math.Max(0, bytes) / 1024d:F0} KiB";

    private static void ManagerWrapped(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void ManagerMuted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ManagerWrapped(text);
        ImGui.PopStyleColor();
    }
}
