using System.Numerics;
using ImGuiNET;

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    private void DrawWorkshopInformation(WorkshopItem item)
    {
        ManagerSectionTitle(string.IsNullOrWhiteSpace(item.Title) ? item.PublishedFileId : item.Title);
        bool busy;
        string error;
        lock (_stateLock) { busy = _detailBusy; error = _detailError; }
        if (busy) ManagerMuted("正在补充工坊详情和上传者信息…");
        if (!string.IsNullOrWhiteSpace(error))
        {
            ManagerWrapped(error);
            if (ImGui.Button("重新读取工坊信息")) RetryWorkshopPresentation();
        }
        var available = ImGui.GetContentRegionAvail().X;
        var wide = available >= Math.Max(760, ImGui.GetFontSize() * 30);
        if (wide && ImGui.BeginTable("##workshop-information", 2,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.NoPadOuterX))
        {
            try
            {
                ImGui.TableSetupColumn("##preview", ImGuiTableColumnFlags.WidthFixed, Math.Min(640, available * .42f));
                ImGui.TableSetupColumn("##metadata", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextColumn();
                DrawWorkshopPreview(item);
                ImGui.TableNextColumn();
                DrawWorkshopMetadata(item);
            }
            finally { ImGui.EndTable(); }
        }
        else
        {
            DrawWorkshopPreview(item);
            DrawWorkshopMetadata(item);
        }
        if (item.Metadata is { Tags.Count: > 0 } metadata)
        {
            ManagerSectionTitle("工坊标签");
            ManagerWrapped(string.Join(" · ", metadata.Tags));
        }
        if (item.Metadata is { Attributes.Count: > 0 } attributes)
        {
            ManagerSectionTitle("作者标注");
            foreach (var (key, value) in attributes.Attributes) ManagerWrapped($"{key}：{value}");
        }
        ManagerSectionTitle("项目说明");
        ManagerWrapped(string.IsNullOrWhiteSpace(item.Description) ? "此项目没有提供说明。" : item.Description);
    }

    private void DrawWorkshopPreview(WorkshopItem item)
    {
        ManagerSectionTitle("工坊预览");
        var previews = item.Metadata?.PreviewImages ?? [];
        if (previews.Count == 0) { ManagerMuted("暂无图片预览。"); return; }
        if (_previewItemId != item.PublishedFileId)
        {
            _previewItemId = item.PublishedFileId;
            _previewIndex = 0;
        }
        _previewIndex = Math.Clamp(_previewIndex, 0, previews.Count - 1);
        if (previews.Count > 1)
        {
            ManagerToolbar(new ManagerAction("上一张", () => _previewIndex--, _previewIndex > 0),
                new ManagerAction("下一张", () => _previewIndex++, _previewIndex < previews.Count - 1));
            ManagerMuted($"图片 {_previewIndex + 1} / {previews.Count}");
        }
        _workshopPreview ??= new WorkshopPreview();
        _workshopPreview.Draw(previews[_previewIndex], Path.Combine(_pluginDirectory, "data", "workshop-previews"),
            ImGui.GetContentRegionAvail().X, _unloadCancellation.Token);
    }

    private void DrawWorkshopMetadata(WorkshopItem item)
    {
        var metadata = item.Metadata;
        ManagerSectionTitle("谱面信息");
        var creator = metadata?.CreatorSteamId ?? 0;
        ManagerWrapped($"上传者：{(!string.IsNullOrWhiteSpace(metadata?.CreatorName) ? metadata.CreatorName : creator != 0 ? creator.ToString() : "未提供")}");
        if (creator != 0 && !string.IsNullOrWhiteSpace(metadata?.CreatorName)) ManagerMuted($"SteamID：{creator}");
        ManagerWrapped($"难度：{metadata?.Difficulty ?? "未标注"}");
        ManagerWrapped($"发布时间：{FormatWorkshopDate(metadata?.TimeCreated ?? 0)}");
        ManagerWrapped($"更新时间：{FormatWorkshopDate(item.TimeUpdated)}");
        ManagerWrapped($"文件大小：{FormatSize(item.FileSize)}");
        ManagerWrapped($"工坊 ID：{item.PublishedFileId}");
        ManagerWrapped($"安装目录：steam_{item.PublishedFileId}");
        ManagerMuted(_detailsFromSubscription ? "订阅下载 · 随订阅同步" : "主动下载 · 独立保留");
        if (metadata is { FullDetails: true })
        {
            ManagerSectionTitle("社区数据");
            ManagerWrapped($"订阅 {metadata.Subscriptions:N0} · 收藏 {metadata.Favorites:N0} · 浏览 {metadata.Views:N0}");
            if (metadata.VotesUp is { } up && metadata.VotesDown is { } down)
            {
                var total = (long)up + down;
                ManagerWrapped(total > 0 ? $"赞 {up:N0} · 踩 {down:N0} · 好评比例 {up * 100d / total:F0}%" : "暂无评价");
            }
        }
    }

    private static string FormatWorkshopDate(long unixTime)
    {
        if (unixTime <= 0) return "未提供";
        try { return DateTimeOffset.FromUnixTimeSeconds(unixTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        catch (ArgumentOutOfRangeException) { return "未提供"; }
    }
}
