using System.Numerics;
using ImGuiNET;

namespace SteamWorkshopManager;

public sealed partial class SteamWorkshopManagerPlugin
{
    // Echo Replay Mobile's light blue section headings throughout the manager.
    // Push and pop these colors with the window so the host retains its own theme.
    private static readonly (ImGuiCol Slot, Vector4 Color)[] ManagerPalette =
    [
        (ImGuiCol.Text, new(.90f, .93f, .97f, 1)),
        (ImGuiCol.TextDisabled, new(.56f, .64f, .73f, 1)),
        (ImGuiCol.WindowBg, new(.07f, .085f, .11f, .98f)),
        (ImGuiCol.ChildBg, new(.085f, .105f, .135f, 1)),
        (ImGuiCol.PopupBg, new(.09f, .11f, .15f, .98f)),
        (ImGuiCol.Border, new(.20f, .27f, .34f, .90f)),
        (ImGuiCol.FrameBg, new(.12f, .16f, .21f, 1)),
        (ImGuiCol.FrameBgHovered, new(.17f, .24f, .32f, 1)),
        (ImGuiCol.FrameBgActive, new(.20f, .31f, .42f, 1)),
        (ImGuiCol.TitleBg, new(.08f, .12f, .17f, 1)),
        (ImGuiCol.TitleBgActive, new(.10f, .19f, .27f, 1)),
        (ImGuiCol.TitleBgCollapsed, new(.08f, .12f, .17f, 1)),
        (ImGuiCol.Button, new(.14f, .26f, .36f, 1)),
        (ImGuiCol.ButtonHovered, new(.20f, .39f, .53f, 1)),
        (ImGuiCol.ButtonActive, new(.24f, .48f, .65f, 1)),
        (ImGuiCol.Header, new(.16f, .31f, .43f, 1)),
        (ImGuiCol.HeaderHovered, new(.20f, .40f, .55f, 1)),
        (ImGuiCol.HeaderActive, new(.23f, .47f, .64f, 1)),
        (ImGuiCol.Tab, new(.11f, .18f, .25f, 1)),
        (ImGuiCol.TabSelected, new(.18f, .34f, .46f, 1)),
        (ImGuiCol.TabHovered, new(.21f, .41f, .56f, 1)),
        (ImGuiCol.TabDimmed, new(.08f, .13f, .18f, 1)),
        (ImGuiCol.TabDimmedSelected, new(.13f, .25f, .34f, 1)),
        (ImGuiCol.TabSelectedOverline, new(.45f, .78f, .96f, 1)),
        (ImGuiCol.TabDimmedSelectedOverline, new(.30f, .53f, .67f, 1)),
        (ImGuiCol.CheckMark, new(.45f, .78f, .96f, 1)),
        (ImGuiCol.SliderGrab, new(.30f, .58f, .75f, 1)),
        (ImGuiCol.SliderGrabActive, new(.45f, .78f, .96f, 1)),
        (ImGuiCol.Separator, new(.18f, .26f, .34f, 1)),
        (ImGuiCol.SeparatorHovered, new(.30f, .58f, .75f, 1)),
        (ImGuiCol.SeparatorActive, new(.45f, .78f, .96f, 1)),
        (ImGuiCol.ScrollbarBg, new(.05f, .07f, .09f, .90f)),
        (ImGuiCol.ScrollbarGrab, new(.22f, .32f, .41f, 1)),
        (ImGuiCol.ScrollbarGrabHovered, new(.30f, .46f, .58f, 1)),
        (ImGuiCol.ScrollbarGrabActive, new(.38f, .60f, .76f, 1)),
        (ImGuiCol.TextSelectedBg, new(.22f, .47f, .65f, .65f)),
        (ImGuiCol.PlotHistogram, new(.45f, .78f, .96f, 1)),
        (ImGuiCol.NavCursor, new(.45f, .78f, .96f, 1)),
    ];
}
