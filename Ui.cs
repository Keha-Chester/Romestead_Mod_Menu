using ImGuiNET;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace RomesteadCheatMenu;

// ImGui.NET's Text/TextColored/TextWrapped treat the string as a printf format,
// so any '%' in game text would read garbage varargs. Always go through TextUnformatted.
internal static class Ui
{
    public static readonly Vec4 Accent = new Vec4(0.96f, 0.76f, 0.38f, 1f);
    public static readonly Vec4 Muted = new Vec4(0.67f, 0.62f, 0.55f, 1f);
    public static readonly Vec4 Good = new Vec4(0.58f, 0.86f, 0.46f, 1f);
    public static readonly Vec4 Bad = new Vec4(0.96f, 0.47f, 0.36f, 1f);

    public static readonly Vec4 TileBackground = new Vec4(0.19f, 0.16f, 0.13f, 1f);
    public static readonly Vec4 TileHovered = new Vec4(0.30f, 0.23f, 0.15f, 1f);
    public static readonly Vec4 TileActive = new Vec4(0.42f, 0.31f, 0.18f, 1f);
    public static readonly Vec4 TileFlash = new Vec4(0.36f, 0.55f, 0.26f, 1f);
    public static readonly Vec4 TileText = new Vec4(0.94f, 0.90f, 0.83f, 1f);

    private static readonly (ImGuiCol Slot, Vec4 Color)[] Theme =
    {
        (ImGuiCol.Text, new Vec4(0.95f, 0.91f, 0.84f, 1f)),
        (ImGuiCol.TextDisabled, new Vec4(0.62f, 0.57f, 0.50f, 1f)),
        (ImGuiCol.WindowBg, new Vec4(0.10f, 0.09f, 0.08f, 0.97f)),
        (ImGuiCol.ChildBg, new Vec4(0.13f, 0.115f, 0.10f, 1f)),
        (ImGuiCol.PopupBg, new Vec4(0.12f, 0.105f, 0.09f, 0.98f)),
        (ImGuiCol.Border, new Vec4(0.47f, 0.37f, 0.22f, 0.55f)),
        (ImGuiCol.FrameBg, new Vec4(0.21f, 0.18f, 0.15f, 1f)),
        (ImGuiCol.FrameBgHovered, new Vec4(0.29f, 0.24f, 0.18f, 1f)),
        (ImGuiCol.FrameBgActive, new Vec4(0.35f, 0.28f, 0.20f, 1f)),
        (ImGuiCol.TitleBg, new Vec4(0.17f, 0.12f, 0.08f, 1f)),
        (ImGuiCol.TitleBgActive, new Vec4(0.38f, 0.22f, 0.10f, 1f)),
        (ImGuiCol.Button, new Vec4(0.37f, 0.25f, 0.13f, 1f)),
        (ImGuiCol.ButtonHovered, new Vec4(0.53f, 0.35f, 0.17f, 1f)),
        (ImGuiCol.ButtonActive, new Vec4(0.66f, 0.43f, 0.19f, 1f)),
        (ImGuiCol.Header, new Vec4(0.37f, 0.25f, 0.13f, 0.85f)),
        (ImGuiCol.HeaderHovered, new Vec4(0.50f, 0.33f, 0.16f, 0.9f)),
        (ImGuiCol.HeaderActive, new Vec4(0.62f, 0.40f, 0.18f, 1f)),
        (ImGuiCol.Tab, new Vec4(0.25f, 0.18f, 0.11f, 1f)),
        (ImGuiCol.TabHovered, new Vec4(0.53f, 0.35f, 0.17f, 1f)),
        (ImGuiCol.TabActive, new Vec4(0.45f, 0.29f, 0.14f, 1f)),
        (ImGuiCol.CheckMark, new Vec4(0.98f, 0.78f, 0.36f, 1f)),
        (ImGuiCol.SliderGrab, new Vec4(0.80f, 0.56f, 0.24f, 1f)),
        (ImGuiCol.Separator, new Vec4(0.47f, 0.37f, 0.22f, 0.5f)),
        (ImGuiCol.TableHeaderBg, new Vec4(0.24f, 0.17f, 0.10f, 1f)),
        (ImGuiCol.TableRowBgAlt, new Vec4(1f, 1f, 1f, 0.035f)),
        (ImGuiCol.PlotHistogram, new Vec4(0.78f, 0.53f, 0.20f, 1f)),
        (ImGuiCol.ScrollbarGrab, new Vec4(0.42f, 0.33f, 0.22f, 1f)),
        (ImGuiCol.ScrollbarGrabHovered, new Vec4(0.52f, 0.40f, 0.25f, 1f)),
        (ImGuiCol.ModalWindowDimBg, new Vec4(0f, 0f, 0f, 0.55f))
    };

    public static void Text(string text)
    {
        ImGui.TextUnformatted(text ?? string.Empty);
    }

    public static void Colored(Vec4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text ?? string.Empty);
        ImGui.PopStyleColor();
    }

    public static void Disabled(string text)
    {
        Colored(Muted, text);
    }

    public static void Wrapped(string text, Vec4? color = null)
    {
        if (color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        }
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text ?? string.Empty);
        ImGui.PopTextWrapPos();
        if (color.HasValue)
        {
            ImGui.PopStyleColor();
        }
    }

    public static int PushTheme()
    {
        foreach ((ImGuiCol slot, Vec4 color) in Theme)
        {
            ImGui.PushStyleColor(slot, color);
        }
        return Theme.Length;
    }

    public static int PushStyle(float s)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 5f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vec2(12f, 10f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vec2(8f, 4f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vec2(8f, 6f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vec2(6f, 4f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vec2(6f, 4f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 14f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        return 13;
    }
}
