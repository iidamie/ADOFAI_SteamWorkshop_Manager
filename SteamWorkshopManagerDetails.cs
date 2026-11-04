using System.Numerics;
using ImGuiNET;

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private InstalledWorkshopLevel? _localDetails;
    private WorkshopItem? _workshopDetails;
    private bool _detailsFromSubscription;
    private bool _confirmLocalDelete;

    private readonly record struct ManagerAction(string Label, Action Invoke, bool Enabled = true);

    private static float ManagerButtonHeight() => Math.Max(48, ImGui.GetFrameHeight() * 1.12f);

    private static void ManagerToolbar(params ManagerAction[] actions)
    {
        if (actions.Length == 0) return;
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail().X;
        var required = actions.Length * actions.Max(action => Math.Max(112,
            ImGui.CalcTextSize(action.Label.Split("##", StringSplitOptions.None)[0]).X + style.FramePadding.X * 2 + 20));
        var horizontal = available >= required + style.ItemSpacing.X * (actions.Length - 1);
        var width = horizontal ? (available - style.ItemSpacing.X * (actions.Length - 1)) / actions.Length : -1;
        foreach (var (action, index) in actions.Select((action, index) => (action, index)))
        {
            if (horizontal && index > 0) ImGui.SameLine();
            ImGui.BeginDisabled(!action.Enabled);
            var clicked = ImGui.Button(action.Label, new Vector2(width, ManagerButtonHeight()));
            ImGui.EndDisabled();
            if (clicked) action.Invoke();
        }
    }

    private static void ManagerSectionTitle(string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.45f, .78f, .96f, 1));
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static bool ManagerSearch(string title, string id, ref string text, uint limit = 256,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ManagerSectionTitle(title);
        ImGui.SetNextItemWidth(-1);
        return ImGui.InputText(id, ref text, limit, flags);
    }

    private static string ManagerEllipsis(string text, float width)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        if (ImGui.CalcTextSize(text).X <= width) return text;
        var suffixWidth = ImGui.CalcTextSize("…").X;
        int low = 0, high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle]).X + suffixWidth <= width) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        return text[..low] + "…";
    }

    private static bool ManagerProjectRow(string title, string metadata)
    {
        var padding = ImGui.GetStyle().FramePadding;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var height = Math.Max(ManagerButtonHeight(), ImGui.GetTextLineHeight() * 2 + padding.Y * 2);
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Selectable("##project-details", false, ImGuiSelectableFlags.None, new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        var textWidth = Math.Max(1, width - padding.X * 2);
        draw.AddText(start + padding, ImGui.GetColorU32(ImGuiCol.Text), ManagerEllipsis(title, textWidth));
        draw.AddText(start + padding + new Vector2(0, ImGui.GetTextLineHeight()),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), ManagerEllipsis(metadata, textWidth));
        return clicked;
    }

    private void DrawLocalDetails()
    {
        var level = _localDetails!;
        ManagerToolbar(new ManagerAction("返回本地列表", () => { _localDetails = null; _confirmLocalDelete = false; }),
            new ManagerAction("检查更新 / 重新下载", () => EnqueueDownload(level.PublishedFileId, level.KeepWithoutSubscription)));
        ImGui.BeginChild("##local-project-details", ManagerListSize(), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        ManagerSectionTitle(string.IsNullOrWhiteSpace(level.Title) ? level.Folder : level.Title);
        ManagerWrapped($"工坊 ID：{level.PublishedFileId}");
        string directory;
        lock (_stateLock) directory = _settings.LevelsDirectory;
        ManagerWrapped($"目录：{Path.Combine(directory, level.Folder)}");
        ManagerMuted(level.KeepWithoutSubscription ? "主动下载 · 独立保留" : "订阅下载 · 随订阅同步");
        ManagerSectionTitle("项目内谱面");
        foreach (var chart in level.ChartFiles ?? [level.ChartFileName]) ManagerWrapped(chart);
        ImGui.Separator();
        if (!_confirmLocalDelete)
            ManagerToolbar(new ManagerAction("删除本地项目", () => _confirmLocalDelete = true));
        else
        {
            ManagerWrapped("确认删除整个项目的已下载谱面与资源？用户额外放入的文件会保留。仍订阅的项目可能在后续同步中重新下载。");
            ManagerToolbar(new ManagerAction("确认删除", () =>
            {
                _ = Task.Run(() => DeleteSelectedLevelsAsync(new HashSet<string>(StringComparer.Ordinal) { level.Folder }));
                _localDetails = null;
                _confirmLocalDelete = false;
            }), new ManagerAction("保留项目", () => _confirmLocalDelete = false));
        }
        ImGui.EndChild();
    }

    private void DrawWorkshopDetails()
    {
        var item = EnsureWorkshopPresentation(_workshopDetails!);
        ManagerToolbar(new ManagerAction("返回列表", CloseWorkshopDetails),
            new ManagerAction("加入下载队列", () => EnqueueDownload(item.PublishedFileId, !_detailsFromSubscription, item)));
        if (_workshopDetails == null) return;
        ImGui.BeginChild("##workshop-project-details", ManagerListSize(), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        try { DrawWorkshopInformation(item); }
        finally { ImGui.EndChild(); }
    }
}
