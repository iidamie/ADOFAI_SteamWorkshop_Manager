using System.Numerics;
using ImGuiNET;

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private const string ManagerWindowId = "Steam 工坊管理器###SteamWorkshopManager";
    private bool _managerOpen;
    private bool _managerFocusRequested;

    private void DrawSettingsUi()
    {
        if (ImGui.Button("打开谱面管理器", new Vector2(-1, Math.Max(48, ImGui.GetFrameHeight()))))
            OpenManagerWindow();
        ManagerWrapped("在独立管理窗口中搜索谱面、粘贴工坊链接、管理本地项目和下载任务。");

        if (_downloads != null)
        {
            var tasks = _downloads.GetSnapshots();
            ManagerMuted($"下载中 {tasks.Count(task => task.State == DownloadState.Running)} · 等待 {tasks.Count(task => task.State == DownloadState.Queued)} · 待恢复 {tasks.Count(task => task.State == DownloadState.Interrupted)} · 失败 {tasks.Count(task => task.State == DownloadState.Failed)}");
            if (!string.IsNullOrWhiteSpace(_downloads.PersistenceError)) ManagerWrapped(_downloads.PersistenceError);
            if (ImGui.Button(_downloads.IsPaused ? "继续下载队列" : "暂停后续下载"))
                _downloads.SetPaused(!_downloads.IsPaused);
        }

        ImGui.Separator();
        DrawLoginSection();
        ImGui.Separator();
        DrawSyncSettings();
        _updateService?.DrawGui();
    }

    private void OpenManagerWindow()
    {
        lock (_stateLock)
        {
            _managerOpen = true;
            _managerFocusRequested = true;
        }
        RequestLocalLibraryRefresh();
    }

    private void CloseManagerWindow()
    {
        CancelWorkshopPresentation();
        lock (_stateLock)
        {
            _managerOpen = false;
            _managerFocusRequested = false;
            _localDetails = null;
            _workshopDetails = null;
            _confirmLocalDelete = false;
        }
    }

    private void DrawIndependentManagerWindow()
    {
        lock (_stateLock)
            if (!_managerOpen) return;

        var display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0 || display.Y <= 0) return;

        bool requestFocus;
        lock (_stateLock)
        {
            requestFocus = _managerFocusRequested;
            _managerFocusRequested = false;
        }

        // Replay Mobile uses a foreground, screen-sized ImGui window for its manager.
        // This callback remains active when ModManager stops drawing the settings page.
        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Math.Max(style.WindowPadding.X, 18), Math.Max(style.WindowPadding.Y, 14)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new Vector2(Math.Max(style.FramePadding.X, 14), Math.Max(style.FramePadding.Y, 9)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
            new Vector2(Math.Max(style.ItemSpacing.X, 12), Math.Max(style.ItemSpacing.Y, 10)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Math.Max(style.FrameRounding, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f));
        foreach (var (slot, color) in ManagerPalette)
            ImGui.PushStyleColor(slot, color);
        bool open = true;
        try
        {
            ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowSize(display, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(.98f);
            if (requestFocus) ImGui.SetNextWindowFocus();
            var visible = ImGui.Begin(ManagerWindowId, ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
            try
            {
                if (visible)
                {
                    ImGui.SetWindowFontScale(1.06f);
                    DrawManagerContents();
                }
            }
            finally { ImGui.End(); }
        }
        finally
        {
            ImGui.PopStyleColor(ManagerPalette.Length);
            ImGui.PopStyleVar(5);
        }

        if (!open) CloseManagerWindow();
    }

    private static Vector2 ManagerListSize(float reserve = 0)
        => new(0, Math.Max(ImGui.GetFrameHeightWithSpacing() * 3, ImGui.GetContentRegionAvail().Y - reserve - 4));
}
