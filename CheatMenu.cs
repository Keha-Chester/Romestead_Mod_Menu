using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Candide;
using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using Shared.Entity;
using Shared.Models.Player;
using Shared.Models.Skill;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace RomesteadCheatMenu;

internal static class CheatMenu
{
    private static readonly int[] SkillSteps = { 1, 5, 10 };
    private static readonly Dictionary<string, string[]> WrappedNames = new Dictionary<string, string[]>();

    // Per-tab state of a spawner grid.
    private sealed class SpawnerView
    {
        public string Id;
        public int[] Presets;
        public int MaxAmount;
        public string Search = string.Empty;
        public int Amount = 1;
        public Category Selected;
    }

    private static readonly SpawnerView ItemsView = new SpawnerView { Id = "items", Presets = new[] { 1, 10, 50, 100, 999 }, MaxAmount = 99999 };
    private static readonly SpawnerView CreaturesView = new SpawnerView { Id = "creatures", Presets = new[] { 1, 3, 5, 10, 25, 50 }, MaxAmount = 100 };

    private static long _lastDrawTicks;
    private static bool _savedDisableMouseDrawing;
    private static bool _creaturesPersistent;
    private static bool _ignoreEscapeUntilReleased;
    private static string _status = string.Empty;
    private static bool _statusIsError;
    private static double _statusTime = -100.0;
    private static bool _focusSearch;
    private static int _favourAmount = 1;
    private static string _flashKey;
    private static double _flashUntil;

    // The part after ### is the ID, so the popup survives a language switch.
    private static string UnlearnPopupId => Loc.T("Confirm", "Подтверждение") + "###confirm_unlearn";

    public static bool IsOpen { get; private set; }

    // True when the game stopped rendering ImGui while the menu is open.
    public static bool DrawIsStale => IsOpen && Environment.TickCount64 - _lastDrawTicks > 2000;

    public static void Open()
    {
        IsOpen = true;
        _lastDrawTicks = Environment.TickCount64;
        _savedDisableMouseDrawing = Globals.DisableMouseDrawing;
        Globals.DisableMouseDrawing = true;
        ItemsView.Amount = Math.Clamp(Settings.Data.Amount, 1, ItemsView.MaxAmount);
        CreaturesView.Amount = Math.Clamp(Settings.Data.CreatureAmount, 1, CreaturesView.MaxAmount);
        _creaturesPersistent = Settings.Data.CreaturesPersistent;
        GamePause.Begin();
        // The Esc that left the terraforming brush must not close the menu right away.
        _ignoreEscapeUntilReleased = Keyboard.GetState().IsKeyDown(Keys.Escape);
        _focusSearch = true;
    }

    public static void Close()
    {
        if (!IsOpen)
        {
            return;
        }
        IsOpen = false;
        GamePause.End();
        Globals.DisableMouseDrawing = _savedDisableMouseDrawing;
        InputBlocker.OnMenuClosed();
        Settings.Data.Amount = ItemsView.Amount;
        Settings.Data.CreatureAmount = CreaturesView.Amount;
        Settings.Data.CreaturesPersistent = _creaturesPersistent;
        Settings.Save();
    }

    public static void AfterEngineUpdate()
    {
        // The game hides the OS cursor and draws its own sprite cursor, which is frozen while input is blocked.
        if (IsOpen && Globals.Game?.Game != null)
        {
            Globals.Game.Game.IsMouseVisible = true;
        }
    }

    public static void Draw()
    {
        if (!IsOpen)
        {
            return;
        }
        _lastDrawTicks = Environment.TickCount64;
        Catalog.EnsureBuilt();

        float s = UiFonts.Scale;
        ImGuiIOPtr io = ImGui.GetIO();
        bool pushedFont = UiFonts.HasMain;
        if (pushedFont)
        {
            ImGui.PushFont(UiFonts.Main);
        }
        int colorCount = Ui.PushTheme();
        int styleCount = Ui.PushStyle(s);

        Vec2 display = io.DisplaySize;
        ImGui.SetNextWindowSize(new Vec2(Math.Min(1220f * s, display.X * 0.94f), Math.Min(820f * s, display.Y * 0.9f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(display * 0.5f, ImGuiCond.FirstUseEver, new Vec2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vec2(Math.Min(680f * s, display.X), Math.Min(440f * s, display.Y)), display);

        bool open = true;
        bool visible = ImGui.Begin("Romestead Cheat Menu      Ctrl+0###RomesteadCheatMenu", ref open, ImGuiWindowFlags.NoCollapse);
        try
        {
            if (visible)
            {
                DrawContents(s);
            }
            if (_ignoreEscapeUntilReleased && !ImGui.IsKeyDown(ImGuiKey.Escape))
            {
                _ignoreEscapeUntilReleased = false;
            }
            // WantTextInput still reflects the previous frame, so Esc first leaves a text field and only a second Esc closes the menu.
            if (!_ignoreEscapeUntilReleased && ImGui.IsKeyPressed(ImGuiKey.Escape) && !io.WantTextInput && !ImGui.IsAnyItemActive() && !ImGui.IsPopupOpen(UnlearnPopupId))
            {
                open = false;
            }
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Menu contents", e);
            SetStatus(Loc.T("Interface error: ", "Ошибка интерфейса: ") + e.Message, true);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(styleCount);
            ImGui.PopStyleColor(colorCount);
            if (pushedFont)
            {
                ImGui.PopFont();
            }
        }
        if (!open)
        {
            Close();
        }
    }

    private static void DrawContents(float s)
    {
        // Tab IDs come after ###, so the open tab stays open when the language changes.
        if (ImGui.BeginTabBar("##cheat_tabs"))
        {
            try
            {
                if (ImGui.BeginTabItem(Loc.T("Items", "Предметы") + "###tab_items"))
                {
                    try
                    {
                        DrawSpawner(Catalog.ItemsTab, Catalog.BuildError, ItemsView, s);
                    }
                    finally
                    {
                        ImGui.EndTabItem();
                    }
                }
                if (ImGui.BeginTabItem(Loc.T("Enemies & Creatures", "Враги и существа") + "###tab_creatures"))
                {
                    try
                    {
                        DrawSpawner(Catalog.CreaturesTab, Catalog.CreaturesError, CreaturesView, s);
                    }
                    finally
                    {
                        ImGui.EndTabItem();
                    }
                }
                if (ImGui.BeginTabItem(Loc.T("Terraform", "Террафоминг") + "###tab_terraform"))
                {
                    try
                    {
                        DrawTerraform(s);
                    }
                    finally
                    {
                        ImGui.EndTabItem();
                    }
                }
                if (ImGui.BeginTabItem(Loc.T("Skills", "Навыки") + "###tab_skills"))
                {
                    try
                    {
                        DrawSkills(s);
                    }
                    finally
                    {
                        ImGui.EndTabItem();
                    }
                }
                if (ImGui.BeginTabItem(Loc.T("Stats", "Характеристики") + "###tab_stats"))
                {
                    try
                    {
                        DrawStats(s);
                    }
                    finally
                    {
                        ImGui.EndTabItem();
                    }
                }
                LanguageButton("EN", Loc.English, "Menu language: English");
                LanguageButton("RU", Loc.Russian, "Язык меню: русский");
            }
            finally
            {
                ImGui.EndTabBar();
            }
        }
        DrawStatusBar();
    }

    // Small buttons in the top-right corner of the tab bar; the current language looks like the open tab.
    private static void LanguageButton(string label, string code, string tooltip)
    {
        bool current = Loc.IsRussian == (code == Loc.Russian);
        if (current)
        {
            ImGui.PushStyleColor(ImGuiCol.Tab, ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive]);
        }
        bool clicked = ImGui.TabItemButton(label + "###language_" + code, ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip);
        if (current)
        {
            ImGui.PopStyleColor();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            Ui.Text(tooltip);
            ImGui.EndTooltip();
        }
        if (clicked)
        {
            Loc.SetLanguage(code);
        }
    }

    private static float FooterHeight(float s)
    {
        return ImGui.GetFrameHeightWithSpacing() + 8f * s;
    }

    private static void Tooltip(string text, float s)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f * s);
        Ui.Text(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // ------------------------------------------------------------------ spawner tabs

    private static void DrawSpawner(CatalogTab tab, string error, SpawnerView view, float s)
    {
        if (tab == null)
        {
            Ui.Colored(Ui.Bad, error ?? Loc.T("The list is not available.", "Список недоступен."));
            return;
        }
        bool creatures = view == CreaturesView;
        if (view.Selected == null && tab.Categories.Count > 0)
        {
            view.Selected = tab.Categories[0];
        }

        ImGui.SetNextItemWidth(330f * s);
        if (_focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            _focusSearch = false;
        }
        ImGui.InputTextWithHint("##search_" + view.Id, Loc.T("Search by English name...", "Поиск по английскому названию..."), ref view.Search, 96);
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Clear", "Очистить") + "###clear_" + view.Id))
        {
            view.Search = string.Empty;
        }
        ImGui.SameLine(0f, 28f * s);
        ImGui.AlignTextToFramePadding();
        Ui.Text(Loc.T("Amount:", "Количество:"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f * s);
        if (ImGui.InputInt("##amount_" + view.Id, ref view.Amount, 1, 10))
        {
            view.Amount = Math.Clamp(view.Amount, 1, view.MaxAmount);
        }
        foreach (int preset in view.Presets)
        {
            ImGui.SameLine();
            if (ImGui.Button(preset.ToString(CultureInfo.InvariantCulture) + "##preset_" + view.Id + preset))
            {
                view.Amount = preset;
            }
        }
        if (creatures)
        {
            ImGui.SameLine(0f, 28f * s);
            ImGui.Checkbox(Loc.T("Don't despawn", "Не исчезают") + "###persistent", ref _creaturesPersistent);
            if (ImGui.IsItemHovered())
            {
                Tooltip(Loc.T(
                    "Off: creatures behave like the game's own spawns and disappear when you walk far away. "
                    + "On: they stay in the world and are saved with it. Citizens always stay.",
                    "Выключено: существа ведут себя как обычные спавны игры и исчезают, когда вы уходите далеко. "
                    + "Включено: остаются в мире и сохраняются вместе с ним. Жители сохраняются всегда."), s);
            }
        }

        float footer = FooterHeight(s);
        bool searching = view.Search.Trim().Length > 0;

        ImGui.BeginChild("##categories_" + view.Id, new Vec2(270f * s, -footer), true);
        try
        {
            foreach (Category category in tab.Categories)
            {
                DrawCategory(category, view, searching, s);
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.SameLine();
        ImGui.BeginChild("##entries_" + view.Id, new Vec2(0f, -footer), true);
        try
        {
            List<SpawnEntry> entries;
            if (searching)
            {
                entries = Catalog.Search(tab, view.Search);
                Ui.Colored(Ui.Accent, Loc.T($"Search in all categories: {entries.Count}", $"Поиск по всем категориям: {entries.Count}"));
            }
            else
            {
                entries = view.Selected?.AllEntries ?? new List<SpawnEntry>();
                Ui.Colored(Ui.Accent, $"{view.Selected?.Label}  ({entries.Count})");
            }
            if (creatures)
            {
                double tiles = Math.Round(Spawner.CreatureDistance / 16f);
                Ui.Disabled(Loc.T(
                    $"Click: spawn ×{view.Amount} {tiles} tiles in front of the character. Bosses are not in the list.",
                    $"Клик: заспавнить ×{view.Amount} в {tiles} тайлах перед персонажем. Боссов в списке нет."));
            }
            else if (!searching && view.Selected != null && view.Selected.Name == "Resources")
            {
                Ui.Disabled(Loc.T(
                    "Clay, Concrete, Ash and Water come in barrels: 1 = 1 barrel. Resources are laid out in rows in front of the character.",
                    "Clay, Concrete, Ash и Water появляются в бочках: 1 шт. = 1 бочка. Ресурсы выкладываются рядами перед персонажем."));
            }
            else
            {
                Ui.Disabled(Loc.T(
                    $"Click an item: drop ×{view.Amount} on the ground in front of the character.",
                    $"Клик по предмету: бросить ×{view.Amount} на землю перед персонажем."));
            }
            ImGui.Separator();
            DrawGrid(entries, view, s);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private static void DrawCategory(Category category, SpawnerView view, bool searching, float s)
    {
        float indent = category.Depth * 18f * s;
        if (indent > 0f)
        {
            ImGui.Indent(indent);
        }
        string label = $"{category.Name}  ({category.AllEntries.Count})##{category.Label}";
        if (ImGui.Selectable(label, !searching && view.Selected == category))
        {
            view.Selected = category;
            view.Search = string.Empty;
        }
        if (indent > 0f)
        {
            ImGui.Unindent(indent);
        }
        foreach (Category child in category.Children)
        {
            DrawCategory(child, view, searching, s);
        }
    }

    // Virtualized grid: only rows inside the scroll view are submitted.
    private static void DrawGrid(List<SpawnEntry> entries, SpawnerView view, float s)
    {
        float tileWidth = 100f * s;
        float tileHeight = 108f * s;
        float gap = 6f * s;
        float iconSize = 48f * s;

        float availableWidth = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)((availableWidth + gap) / (tileWidth + gap)));
        int rows = (entries.Count + columns - 1) / columns;
        float rowHeight = tileHeight + gap;
        Vec2 origin = ImGui.GetCursorPos();
        float scroll = ImGui.GetScrollY();
        float viewHeight = ImGui.GetWindowHeight();
        int firstRow = Math.Max(0, (int)((scroll - origin.Y) / rowHeight) - 1);
        int lastRow = Math.Min(rows - 1, (int)((scroll - origin.Y + viewHeight) / rowHeight) + 1);

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index >= entries.Count)
                {
                    break;
                }
                ImGui.SetCursorPos(new Vec2(origin.X + column * (tileWidth + gap), origin.Y + row * rowHeight));
                DrawTile(drawList, entries[index], view, tileWidth, tileHeight, iconSize, s);
            }
        }
        ImGui.SetCursorPos(new Vec2(origin.X, origin.Y + Math.Max(rows, 1) * rowHeight));
        ImGui.Dummy(new Vec2(1f, 1f));
    }

    private static void DrawTile(ImDrawListPtr drawList, SpawnEntry entry, SpawnerView view, float width, float height, float iconSize, float s)
    {
        Vec2 min = ImGui.GetCursorScreenPos();
        Vec2 max = min + new Vec2(width, height);
        ImGui.PushID(entry.Key);
        bool clicked = ImGui.InvisibleButton("##tile", new Vec2(width, height));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        ImGui.PopID();

        bool flashing = _flashKey == entry.Key && ImGui.GetTime() < _flashUntil;
        Vec4 background = flashing ? Ui.TileFlash : active ? Ui.TileActive : hovered ? Ui.TileHovered : Ui.TileBackground;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(background), 6f * s);
        if (hovered)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(Ui.Accent), 6f * s, ImDrawFlags.None, 1.5f * s);
        }

        Vec2 iconMin = new Vec2(min.X + (width - iconSize) / 2f, min.Y + 7f * s);
        Vec2 iconMax = iconMin + new Vec2(iconSize, iconSize);
        if (Catalog.TryGetIcon(entry, out IconRef icon))
        {
            FitImage(drawList, icon, iconMin, iconSize);
        }
        else
        {
            drawList.AddRect(iconMin, iconMax, ImGui.GetColorU32(Ui.Muted), 4f * s);
        }
        if (Spawner.SpawnsInBucket(entry))
        {
            Vec2 badge = new Vec2(max.X - 8f * s, min.Y + 8f * s);
            drawList.AddCircleFilled(badge, 4f * s, ImGui.GetColorU32(Ui.Accent));
        }
        else if (entry.Kind == EntryKind.Creature && entry.EntityType == EntityType.Hostile)
        {
            Vec2 badge = new Vec2(max.X - 8f * s, min.Y + 8f * s);
            drawList.AddCircleFilled(badge, 4f * s, ImGui.GetColorU32(Ui.Bad));
        }

        float lineHeight = ImGui.GetTextLineHeight();
        float textTop = iconMax.Y + 4f * s;
        float textWidth = width - 8f * s;
        uint textColor = ImGui.GetColorU32(Ui.TileText);
        string[] lines = WrapName(entry.NameEn, textWidth, 2);
        for (int i = 0; i < lines.Length; i++)
        {
            float lineWidth = ImGui.CalcTextSize(lines[i]).X;
            drawList.AddText(new Vec2(min.X + (width - lineWidth) / 2f, textTop + i * lineHeight), textColor, lines[i]);
        }

        if (hovered)
        {
            DrawTooltip(entry, s);
        }
        if (clicked)
        {
            bool error;
            string message = entry.IsEntity
                ? Spawner.SpawnCreature(entry, view.Amount, _creaturesPersistent, out error)
                : Spawner.Spawn(entry, view.Amount, out error);
            SetStatus(message, error);
            _flashKey = entry.Key;
            _flashUntil = ImGui.GetTime() + 0.35;
        }
    }

    // Draws a sprite as large as fits into a square box, keeping its proportions.
    private static void FitImage(ImDrawListPtr drawList, IconRef icon, Vec2 boxMin, float boxSize)
    {
        Vec2 size = icon.Size.X > 0f && icon.Size.Y > 0f ? icon.Size : new Vec2(1f, 1f);
        float scale = Math.Min(boxSize / size.X, boxSize / size.Y);
        Vec2 drawn = size * scale;
        Vec2 imageMin = boxMin + (new Vec2(boxSize, boxSize) - drawn) * 0.5f;
        drawList.AddImage(icon.Texture, imageMin, imageMin + drawn, icon.Uv0, icon.Uv1);
    }

    private static void DrawTooltip(SpawnEntry entry, float s)
    {
        // Items and resources get the game's own tooltip; creatures and citizens have none in the game.
        if (!entry.IsEntity && GameTooltip.TryDraw(entry))
        {
            return;
        }
        ImGui.BeginTooltip();
        if (Catalog.TryGetIcon(entry, out IconRef icon))
        {
            float box = (entry.Kind == EntryKind.Creature ? 96f : 64f) * s;
            FitImage(ImGui.GetWindowDrawList(), icon, ImGui.GetCursorScreenPos(), box);
            ImGui.Dummy(new Vec2(box, box));
            ImGui.SameLine();
        }
        ImGui.BeginGroup();
        Ui.Colored(Ui.Accent, entry.NameEn);
        if (!string.IsNullOrEmpty(entry.NameLocal) && entry.NameLocal != entry.NameEn)
        {
            Ui.Text(entry.NameLocal);
        }
        switch (entry.Kind)
        {
            case EntryKind.Creature:
                Ui.Text(entry.EntityType == EntityType.Hostile ? Loc.T("Enemy", "Враг") : Loc.T("Passive creature", "Мирное существо"));
                if (!string.IsNullOrEmpty(entry.Aliases))
                {
                    Ui.Disabled(Loc.T("Also found as: ", "Также ищется как: ") + entry.Aliases);
                }
                if (entry.MaxHealth > 0)
                {
                    Ui.Disabled(Loc.T($"Health: {entry.MaxHealth}", $"Здоровье: {entry.MaxHealth}"));
                }
                Ui.Disabled(entry.Id);
                break;
            case EntryKind.Citizen:
                Ui.Text(Loc.T("Wild citizen: talk to them and invite them to your settlement",
                    "Дикий житель: с ним можно поговорить и позвать в поселение"));
                Ui.Disabled(entry.Id);
                break;
            case EntryKind.Item:
                Ui.Disabled(entry.Id);
                Ui.Disabled(Loc.T(
                    $"Stack size: {entry.MaxStack}{(entry.Unique ? ", unique" : string.Empty)}",
                    $"Размер стопки: {entry.MaxStack}{(entry.Unique ? ", уникальный" : string.Empty)}"));
                break;
            default:
                Ui.Disabled(entry.Id);
                if (Spawner.SpawnsInBucket(entry))
                {
                    Ui.Disabled(Loc.T("Comes in a barrel (1 = 1 barrel)", "Появляется в бочке (1 шт. = 1 бочка)"));
                }
                break;
        }
        Ui.Disabled(entry.CategoryLabel);
        ImGui.EndGroup();
        string description = entry.IsEntity
            ? (Loc.IsRussian ? entry.NoteRu ?? entry.NoteEn : entry.NoteEn ?? entry.NoteRu)
            : entry.Description;
        if (!string.IsNullOrEmpty(description))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 380f * s);
            Ui.Text(description);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTooltip();
    }

    private static string[] WrapName(string text, float width, int maxLines)
    {
        string key = text + "|" + (int)width;
        if (WrappedNames.TryGetValue(key, out string[] cached))
        {
            return cached;
        }
        var lines = new List<string>();
        string[] words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = string.Empty;
        int index = 0;
        while (index < words.Length && lines.Count < maxLines)
        {
            string candidate = line.Length == 0 ? words[index] : line + " " + words[index];
            if (ImGui.CalcTextSize(candidate).X <= width)
            {
                line = candidate;
                index++;
                continue;
            }
            if (line.Length == 0)
            {
                line = words[index];
                index++;
            }
            lines.Add(line);
            line = string.Empty;
        }
        if (line.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(line);
            line = string.Empty;
        }
        bool truncated = index < words.Length || line.Length > 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (ImGui.CalcTextSize(lines[i]).X > width || (truncated && i == lines.Count - 1))
            {
                lines[i] = Ellipsize(lines[i], width);
            }
        }
        string[] result = lines.ToArray();
        WrappedNames[key] = result;
        return result;
    }

    private static string Ellipsize(string text, float width)
    {
        const string dots = "..";
        string trimmed = text;
        while (trimmed.Length > 1 && ImGui.CalcTextSize(trimmed + dots).X > width)
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }
        return trimmed.TrimEnd() + dots;
    }

    // ------------------------------------------------------------------ terraform tab

    private static void DrawTerraform(float s)
    {
        ImGui.BeginChild("##terraform", new Vec2(0f, -FooterHeight(s)), false);
        try
        {
            bool can = Terraform.CanTerraform(out string reason);
            Ui.Wrapped(Loc.T(
                "The brush changes map tiles right in the world. Pick what to paint on the left and press \"Paint with mouse\": the menu closes, "
                + "LMB paints, the mouse wheel changes the brush size, the middle button takes the tile under the cursor (eyedropper), Ctrl+Z undoes, "
                + "RMB, Esc or Ctrl+0 bring the menu back. You can keep walking meanwhile.",
                "Кисть меняет тайлы карты прямо в мире. Слева выберите, что рисовать, и нажмите «Рисовать мышью»: меню закроется, "
                + "ЛКМ рисует, колесо мыши меняет размер кисти, средняя кнопка берёт тайл под курсором (пипетка), Ctrl+Z отменяет, "
                + "ПКМ, Esc или Ctrl+0 возвращают в меню. Ходить персонажем при этом можно."), Ui.Muted);
            if (!can)
            {
                Ui.Colored(Ui.Bad, reason);
            }
            ImGui.Spacing();

            BrushDef current = Terraform.CurrentBrush;
            ImGui.BeginChild("##brushes", new Vec2(400f * s, 0f), true);
            try
            {
                string group = null;
                float swatch = ImGui.GetTextLineHeight();
                foreach (BrushDef brush in Terraform.MenuBrushes())
                {
                    if (brush.Group != group)
                    {
                        if (group != null)
                        {
                            ImGui.Spacing();
                        }
                        group = brush.Group;
                        Ui.Colored(Ui.Accent, group);
                        ImGui.Separator();
                    }
                    Vec2 position = ImGui.GetCursorScreenPos();
                    ImGui.GetWindowDrawList().AddRectFilled(position + new Vec2(0f, 1f * s), position + new Vec2(swatch, swatch + 1f * s),
                        ImGui.GetColorU32(Terraform.BrushColor(brush)), 3f * s);
                    ImGui.SetCursorScreenPos(position + new Vec2(swatch + 8f * s, 0f));
                    if (ImGui.Selectable(brush.Label + "###brush_" + brush.Id, brush == current))
                    {
                        Terraform.Select(brush);
                    }
                    if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(brush.Hint))
                    {
                        Tooltip(brush.Hint, s);
                    }
                }
            }
            finally
            {
                ImGui.EndChild();
            }

            ImGui.SameLine();
            ImGui.BeginChild("##brush_settings", new Vec2(0f, 0f), true);
            try
            {
                current = Terraform.CurrentBrush;
                Ui.Colored(Ui.Accent, Loc.T("Selected: ", "Выбрано: ") + current.Label);
                if (!string.IsNullOrEmpty(current.Hint))
                {
                    Ui.Wrapped(current.Hint, Ui.Muted);
                }
                ImGui.Spacing();

                float width = 300f * s;
                int radius = Settings.Data.BrushRadius;
                string size = (radius * 2 + 1).ToString(CultureInfo.InvariantCulture);
                ImGui.SetNextItemWidth(width);
                if (ImGui.SliderInt(Loc.T("Brush size", "Размер кисти") + "###brush_radius", ref radius, 0, Terraform.MaxRadius, size + " x " + size))
                {
                    Settings.Data.BrushRadius = radius;
                    Settings.MarkDirty();
                }
                bool circle = Settings.Data.BrushCircle;
                if (ImGui.Checkbox(Loc.T("Round brush", "Круглая кисть") + "###brush_circle", ref circle))
                {
                    Settings.Data.BrushCircle = circle;
                    Settings.MarkDirty();
                }
                if (current.PaintsGroundOnly)
                {
                    bool clear = Settings.Data.BrushClearsTerrain;
                    if (ImGui.Checkbox(Loc.T("Also remove cliffs, water and pits under the brush", "Заодно убирать скалы, воду и ямы под кистью") + "###brush_clear", ref clear))
                    {
                        Settings.Data.BrushClearsTerrain = clear;
                        Settings.MarkDirty();
                    }
                }
                ImGui.Spacing();

                if (!can)
                {
                    ImGui.BeginDisabled();
                }
                try
                {
                    if (ImGui.Button(Loc.T("Paint with mouse", "Рисовать мышью") + "###brush_start", new Vec2(width, 0f)))
                    {
                        string failure = Terraform.StartBrush();
                        if (failure != null)
                        {
                            SetStatus(failure, true);
                        }
                    }
                    ImGui.Spacing();
                    int area = Settings.Data.AreaRadius;
                    ImGui.SetNextItemWidth(width);
                    if (ImGui.SliderInt(Loc.T("Radius", "Радиус") + "###area_radius", ref area, 1, Terraform.MaxAreaRadius, Loc.T("%d tiles", "%d тайлов")))
                    {
                        Settings.Data.AreaRadius = area;
                        Settings.MarkDirty();
                    }
                    if (ImGui.Button(Loc.T("Apply around the character", "Применить вокруг персонажа") + "###area_apply", new Vec2(width, 0f)))
                    {
                        SetStatus(Terraform.ApplyAroundPlayer(area));
                    }
                    ImGui.Spacing();
                    if (ImGui.Button(Loc.T($"Undo the last change ({Terraform.UndoCount})", $"Отменить последнее изменение ({Terraform.UndoCount})") + "###terraform_undo", new Vec2(width, 0f)))
                    {
                        Terraform.Undo();
                        SetStatus(Loc.T("The last map change was undone.", "Последнее изменение карты отменено."));
                    }
                }
                finally
                {
                    if (!can)
                    {
                        ImGui.EndDisabled();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                Ui.Wrapped(Loc.T(
                    "The bank slope that goes down under the water is drawn by the game itself along the edge of Water tiles: judging by the game data, "
                    + "it is not a separate tile. Paint water next to land and the slope appears. To copy a spot you have seen in the world, "
                    + "start the brush and hover over it: the top of the screen shows its Ground and Structure, and the middle mouse button copies that tile into the brush.",
                    "Откос берега, уходящий под воду, игра рисует сама по краю тайла «Вода» (Water): отдельного тайла у него, "
                    + "судя по данным игры, нет. Рисуйте воду рядом с сушей, и склон появится. Если нужен именно какой-то "
                    + "встреченный в мире участок, включите кисть и наведите на него курсор: вверху экрана видно, какие там Ground и Structure, "
                    + "а средняя кнопка мыши копирует этот тайл в кисть."), Ui.Muted);
                Ui.Wrapped(Loc.T(
                    "Changes are saved with the world. The brush never touches buildings, fields or trees.",
                    "Изменения сохраняются вместе с миром. Постройки, поля и деревья кисть не трогает."), Ui.Muted);
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    // ------------------------------------------------------------------ skills tab

    private static void DrawSkills(float s)
    {
        PlayerCharacterModel character = GameAccess.LocalCharacter;
        if (character == null)
        {
            Ui.Disabled(Loc.T("Load a world to see the skills.", "Загрузите мир, чтобы увидеть навыки."));
            return;
        }
        ImGui.BeginChild("##skills", new Vec2(0f, -FooterHeight(s)), false);
        try
        {
            Skills skills = character.Skills;

            Ui.Colored(Ui.Accent, Loc.T("Favour points", "Favour points: очки преимуществ богов"));
            ImGui.Separator();
            ImGui.AlignTextToFramePadding();
            Ui.Text(Loc.T($"Available: {skills.CurrentFavourPoints}", $"Доступно: {skills.CurrentFavourPoints}"));
            ImGui.SameLine(0f, 30f * s);
            Ui.Disabled(Loc.T(
                $"level: {skills.FavourLevel}   learned: {skills.Favours?.Count ?? 0}",
                $"уровень: {skills.FavourLevel}   изучено: {skills.Favours?.Count ?? 0}"));
            ImGui.SetNextItemWidth(140f * s);
            if (ImGui.InputInt("##favour_amount", ref _favourAmount, 1, 5))
            {
                _favourAmount = Math.Clamp(_favourAmount, 1, 999);
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T($"Add +{_favourAmount}", $"Добавить +{_favourAmount}") + "###favour_add"))
            {
                SetStatus(PlayerCheats.ChangeFavourPoints(_favourAmount), !GameAccess.IsHost);
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T($"Remove -{_favourAmount}", $"Убрать -{_favourAmount}") + "###favour_remove"))
            {
                SetStatus(PlayerCheats.ChangeFavourPoints(-_favourAmount), !GameAccess.IsHost);
            }
            ImGui.SameLine(0f, 30f * s);
            if (ImGui.Button(Loc.T("Reset learned favours...", "Сбросить изученные преимущества...") + "###favour_reset"))
            {
                ImGui.OpenPopup(UnlearnPopupId);
            }
            DrawUnlearnPopup();
            Ui.Disabled(Loc.T("Spend the points as usual in the character's skills and favours window.",
                "Очки тратятся как обычно, в окне навыков и преимуществ персонажа."));

            ImGui.Spacing();
            ImGui.Spacing();
            Ui.Colored(Ui.Accent, Loc.T("Skills", "Skills: навыки"));
            ImGui.Separator();
            if (ImGui.BeginTable("##skills_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                try
                {
                    ImGui.TableSetupColumn(Loc.T("Skill", "Навык"), ImGuiTableColumnFlags.WidthStretch, 2.3f);
                    ImGui.TableSetupColumn(Loc.T("Level", "Уровень"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
                    ImGui.TableSetupColumn(Loc.T("Experience to the next level", "Опыт до следующего уровня"), ImGuiTableColumnFlags.WidthStretch, 2.1f);
                    ImGui.TableSetupColumn(Loc.T("Level up", "Прокачать"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
                    ImGui.TableHeadersRow();
                    List<CharacterSkill> ordered = skills.CharacterSkills.Values
                        .OrderBy(PlayerCheats.SkillName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (CharacterSkill skill in ordered)
                    {
                        DrawSkillRow(skill, s);
                    }
                }
                finally
                {
                    ImGui.EndTable();
                }
            }
            Ui.Disabled(Loc.T(
                "Levels are added through the game's own mechanics, so the gained experience also grants favour points.",
                "Уровни добавляются штатной механикой игры, поэтому за накопленный опыт начисляются и очки преимуществ."));
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private static void DrawSkillRow(CharacterSkill skill, float s)
    {
        ImGui.PushID(skill.SkillId);
        try
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f * s);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            string english = PlayerCheats.SkillName(skill);
            Ui.Text(english);
            string local = skill.Name;
            if (!string.IsNullOrEmpty(local) && local != english)
            {
                ImGui.SameLine();
                Ui.Disabled(local);
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Text($"{skill.Level} / 100");

            ImGui.TableNextColumn();
            bool maxed = skill.Level >= 100;
            float needed = skill.ExperienceRequiredToLevelUp;
            float fraction = maxed || needed <= 0f ? 1f : Math.Clamp(skill.CurrentExperience / needed, 0f, 1f);
            ImGui.ProgressBar(fraction, new Vec2(-1f, 0f), maxed ? "MAX" : $"{skill.CurrentExperience:0} / {needed:0}");

            ImGui.TableNextColumn();
            if (maxed)
            {
                ImGui.BeginDisabled();
            }
            foreach (int step in SkillSteps)
            {
                if (ImGui.Button("+" + step))
                {
                    SetStatus(PlayerCheats.AddSkillLevels(skill, step));
                }
                ImGui.SameLine();
            }
            if (ImGui.Button(Loc.T("To 100", "До 100") + "###to_max"))
            {
                SetStatus(PlayerCheats.AddSkillLevels(skill, 100));
            }
            if (maxed)
            {
                ImGui.EndDisabled();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static void DrawUnlearnPopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal(UnlearnPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        try
        {
            Ui.Text(Loc.T("Reset all learned favours of the gods?", "Сбросить все изученные преимущества богов?"));
            Ui.Disabled(Loc.T("The spent points come back and can be distributed again.", "Потраченные очки вернутся, их можно будет распределить заново."));
            ImGui.Spacing();
            if (ImGui.Button(Loc.T("Yes, reset", "Да, сбросить") + "###unlearn_yes"))
            {
                SetStatus(PlayerCheats.UnlearnFavours());
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Cancel", "Отмена") + "###unlearn_cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    // ------------------------------------------------------------------ stats tab

    private static void DrawStats(float s)
    {
        EntityWrapper player = GameAccess.LocalPlayerEntity;
        if (player == null)
        {
            Ui.Disabled(Loc.T("Load a world to change stats.", "Загрузите мир, чтобы менять характеристики."));
            return;
        }
        ImGui.BeginChild("##stats", new Vec2(0f, -FooterHeight(s)), false);
        try
        {
            Ui.Wrapped(Loc.T(
                "The bonus is added to the stat: for example, health 100 → 110. The buttons change the bonus by one step (×10 with Shift), or type a number.",
                "Бонус прибавляется к характеристике: например, здоровье 100 → 110. Кнопки меняют бонус на шаг (с Shift шаг ×10), число можно ввести вручную."));
            Ui.Wrapped(Loc.T(
                "Freeze: for health it means immortality and always full HP; for stamina/mana the energy is never spent; other stats stay fixed and don't drop from debuffs.",
                "Freeze: для здоровья это бессмертие и всегда полное HP; для выносливости/маны энергия не тратится; остальные значения зафиксированы и не падают от дебаффов."), Ui.Muted);
            Ui.Wrapped(Loc.T(
                "Romestead has no separate mana: spells and scrolls spend the same Energy. Settings are remembered per character and turn on when the world loads.",
                "Отдельной маны в Romestead нет: заклинания и свитки тратят ту же Энергию. Настройки запоминаются для персонажа и включаются при загрузке мира."), Ui.Muted);
            ImGui.Spacing();
            DrawStatTable("##main_stats", PlayerCheats.MainStats, player, s);
            ImGui.Spacing();
            if (ImGui.CollapsingHeader(Loc.T("Other stats", "Другие характеристики") + "###other_stats"))
            {
                DrawStatTable("##other_stats_table", PlayerCheats.OtherStats, player, s);
            }
            ImGui.Spacing();
            if (ImGui.Button(Loc.T("Reset all bonuses and Freeze", "Сбросить все бонусы и Freeze") + "###stats_reset"))
            {
                PlayerCheats.ResetAll();
                SetStatus(Loc.T("Bonuses and freezes were reset.", "Бонусы и заморозки сброшены."));
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private static void DrawStatTable(string id, IReadOnlyList<StatDef> defs, EntityWrapper player, float s)
    {
        if (!ImGui.BeginTable(id, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }
        try
        {
            ImGui.TableSetupColumn(Loc.T("Stat", "Характеристика"), ImGuiTableColumnFlags.WidthStretch, 2.6f);
            ImGui.TableSetupColumn(Loc.T("Current", "Сейчас"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn(Loc.T("Bonus", "Бонус"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn(Loc.T("Change", "Изменить"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Freeze", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableHeadersRow();
            bool shift = ImGui.GetIO().KeyShift;
            foreach (StatDef def in defs)
            {
                StatState state = PlayerCheats.GetState(def.Id);
                ImGui.PushID(def.Id);
                try
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f * s);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    // Our own label if there is one, otherwise the game's name in the menu language; the English stat name in grey.
                    string primary = def.Label ?? (Loc.IsRussian ? def.LocalName : def.EnglishName);
                    Ui.Text(primary);
                    if (def.EnglishName != primary)
                    {
                        ImGui.SameLine();
                        Ui.Disabled(def.EnglishName);
                    }

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    Ui.Text(PlayerCheats.FormatCurrent(def, player));

                    ImGui.TableNextColumn();
                    float bonus = state.Bonus;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputFloat("##bonus", ref bonus, 0f, 0f, PlayerCheats.IsPercentage(def) ? "%.2f" : "%.1f"))
                    {
                        PlayerCheats.SetBonus(def, bonus);
                    }

                    ImGui.TableNextColumn();
                    float step = PlayerCheats.StepFor(def, player) * (shift ? 10f : 1f);
                    string stepText = PlayerCheats.FormatValue(def, step);
                    if (ImGui.Button("-" + stepText))
                    {
                        PlayerCheats.SetBonus(def, state.Bonus - step);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("+" + stepText))
                    {
                        PlayerCheats.SetBonus(def, state.Bonus + step);
                    }

                    ImGui.TableNextColumn();
                    bool frozen = state.Frozen;
                    if (ImGui.Checkbox("##freeze", ref frozen))
                    {
                        PlayerCheats.SetFrozen(def, frozen);
                    }
                }
                finally
                {
                    ImGui.PopID();
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    // ------------------------------------------------------------------ status

    private static void DrawStatusBar()
    {
        ImGui.Separator();
        if (GameAccess.InWorld && !GameAccess.IsHost)
        {
            Ui.Colored(Ui.Bad, Loc.T(
                "You are not the host of this world: spawning, favour points and terraforming only work in your own world.",
                "Вы не хост этого мира: спавн, очки преимуществ и террафоминг работают только в вашем мире."));
        }
        else if (_status.Length > 0 && ImGui.GetTime() - _statusTime < 6.0)
        {
            Ui.Colored(_statusIsError ? Ui.Bad : Ui.Good, _status);
        }
        else
        {
            Ui.Disabled(Loc.T(
                "Ctrl+0 or Esc: close. While the menu is open the game is paused (single player) and gets no keyboard or mouse input.",
                "Ctrl+0 или Esc: закрыть. Пока меню открыто, игра на паузе (в одиночной игре), а клавиатура и мышь в неё не передаются."));
        }
    }

    private static void SetStatus(string text, bool error = false)
    {
        _status = text ?? string.Empty;
        _statusIsError = error;
        _statusTime = ImGui.GetTime();
        Log.Info((error ? "Status (error): " : "Status: ") + _status);
    }
}
