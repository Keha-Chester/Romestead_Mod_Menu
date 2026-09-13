using System;
using System.IO;
using System.Runtime.InteropServices;
using Candide;
using Candide.Toolkit;
using ImGuiNET;

namespace RomesteadCheatMenu;

// The game's ImGui only has the tiny built-in ASCII font. Add a scalable TTF with
// Cyrillic glyphs (item descriptions and labels are Russian) sized for the screen.
internal static unsafe class UiFonts
{
    // Latin-1, Cyrillic, general punctuation (dashes, ellipsis) and arrows: game descriptions use them.
    // ImGui keeps a pointer to this table for the lifetime of the font atlas, so it stays pinned.
    private static readonly ushort[] GlyphRanges =
    {
        0x0020, 0x00FF,
        0x0400, 0x052F,
        0x2000, 0x206F,
        0x2190, 0x21FF,
        0x2DE0, 0x2DFF,
        0xA640, 0xA69F,
        0
    };

    private static GCHandle _glyphRangesHandle;
    private static bool _attempted;

    public static ImFontPtr Main;

    public static bool HasMain { get; private set; }

    public static float Scale { get; private set; } = 1f;

    public static void EnsureLoaded(ImGuiRenderer renderer)
    {
        if (_attempted)
        {
            return;
        }
        _attempted = true;

        int height = Globals.GraphicsDevice?.PresentationParameters?.BackBufferHeight ?? 1080;
        Scale = Math.Clamp(height / 1080f, 1f, 3f);
        float size = MathF.Round(17f * Scale);

        string windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        {
            Path.Combine(windowsFonts, "segoeui.ttf"),
            Path.Combine(windowsFonts, "arial.ttf"),
            Path.Combine(windowsFonts, "tahoma.ttf"),
            Path.Combine(AppContext.BaseDirectory, "Content", "fonts", "Arial.ttf")
        };

        ImGuiIOPtr io = ImGui.GetIO();
        if (!_glyphRangesHandle.IsAllocated)
        {
            _glyphRangesHandle = GCHandle.Alloc(GlyphRanges, GCHandleType.Pinned);
        }
        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            ImFontPtr font = io.Fonts.AddFontFromFileTTF(path, size, new ImFontConfigPtr((ImFontConfig*)null), _glyphRangesHandle.AddrOfPinnedObject());
            if (font.NativePtr == null)
            {
                continue;
            }
            Main = font;
            HasMain = true;
            Log.Info($"UI font: {path}, {size}px (scale {Scale:0.00}, back buffer height {height})");
            break;
        }
        renderer.RebuildFontAtlas();
    }
}
