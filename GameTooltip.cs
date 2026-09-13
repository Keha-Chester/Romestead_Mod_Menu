using System;
using System.Collections.Generic;
using Candide;
using Candide.CandideUI;
using Candide.CandideUI.Containers;
using Candide.CandideUI.Helpers;
using Candide.CandideUI.Tooltip;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Shared.Data;
using Shared.Models.Construction;
using Shared.Models.Items;
using Shared.Text;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace RomesteadCheatMenu;

// The game's own item tooltip: content built by CandideTooltipHelper exactly like inventory slots do
// (name in tier colour, stats, effects, price, description), laid out and drawn by the game's UI code
// into an off-screen texture, which the menu then shows as its tooltip.
internal static class GameTooltip
{
    // CandideTooltipWindow's padding, in unscaled UI pixels.
    private const int TooltipPaddingX = 16;
    private const int TooltipPaddingY = 12;

    private static CandideDesktop _desktop;
    private static CandideWindow _window;
    private static CandideVerticalStackPanel _panel;
    private static RenderTarget2D _target;
    private static IntPtr _targetId;
    private static string _renderedKey;
    private static Rectangle _renderedBounds;
    private static bool _broken;

    public static bool TryDraw(SpawnEntry entry)
    {
        if (_broken || entry == null || entry.IsEntity || Globals.ImGuiRenderer == null || Globals.GraphicsDevice == null)
        {
            return false;
        }
        try
        {
            int scale = Math.Max(1, Globals.InterfaceScale);
            int width = Math.Min(4096, (CandideTooltipWindow.DefaultMaxWidth + TooltipPaddingX * 2 + 8) * scale);
            int height = Math.Clamp(UiRenderHelper.GetUiRenderTargetHeight, 256, 4096);
            string key = $"{entry.Key}|{scale}|{Globals.InterfaceOutputScale}|{width}x{height}|{StringDefinitions.CurrentLanguageCode}";
            if (key != _renderedKey)
            {
                Render(entry, key, width, height);
            }
        }
        catch (Exception e)
        {
            // Keep the menu's own tooltip for the rest of the session.
            _broken = true;
            Log.Error("Game tooltip rendering failed, falling back to the simple tooltip", e);
            return false;
        }
        if (_renderedBounds.Width <= 0 || _renderedBounds.Height <= 0 || _target == null)
        {
            return false;
        }

        // The UI render target is scaled onto the screen by InterfaceOutputScale, so this is the in-game size.
        float outputScale = Globals.InterfaceOutputScale > 0f ? Globals.InterfaceOutputScale : 1f;
        Vec2 size = new Vec2(_renderedBounds.Width, _renderedBounds.Height) * outputScale;
        Vec2 uv0 = new Vec2(_renderedBounds.X / (float)_target.Width, _renderedBounds.Y / (float)_target.Height);
        Vec2 uv1 = new Vec2(_renderedBounds.Right / (float)_target.Width, _renderedBounds.Bottom / (float)_target.Height);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vec2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vec4(0f, 0f, 0f, 0f));
        ImGui.BeginTooltip();
        ImGui.Image(_targetId, size, uv0, uv1);
        ImGui.EndTooltip();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        return true;
    }

    private static void Render(SpawnEntry entry, string key, int width, int height)
    {
        _renderedKey = key;
        _renderedBounds = Rectangle.Empty;
        List<CandideUiElement> content = BuildContent(entry);
        if (content == null || content.Count == 0)
        {
            return;
        }

        GraphicsDevice device = Globals.GraphicsDevice;
        EnsureTarget(device, width, height);
        EnsureWindow();
        _panel.ClearChildren();
        foreach (CandideUiElement element in content)
        {
            _panel.AddChild(element);
        }
        _desktop.DesktopWidth = width;
        _desktop.DesktopHeight = height;
        _desktop.InvalidateLayout();
        _desktop.Update(Globals.GameTime ?? new GameTime());

        RenderTargetBinding[] previousTargets = device.GetRenderTargets();
        Viewport previousViewport = device.Viewport;
        Rectangle previousScissor = device.ScissorRectangle;
        try
        {
            device.SetRenderTarget(_target);
            device.Clear(Color.Transparent);
            _desktop.Draw();
        }
        finally
        {
            // The game's UI drawing leaves its scissor rectangle on the device; hand ImGui the state it had.
            if (previousTargets != null && previousTargets.Length > 0)
            {
                device.SetRenderTargets(previousTargets);
            }
            else
            {
                device.SetRenderTarget(null);
            }
            device.Viewport = previousViewport;
            device.ScissorRectangle = previousScissor;
        }
        _renderedBounds = Rectangle.Intersect(_window.LayoutBounds, new Rectangle(0, 0, width, height));
    }

    private static List<CandideUiElement> BuildContent(SpawnEntry entry)
    {
        if (entry.Kind == EntryKind.Item)
        {
            ItemData data = ItemDataBase.GetItemDataOrNull(entry.Id);
            if (data == null)
            {
                return null;
            }
            try
            {
                // Same as an inventory slot, including the sell price.
                return CandideTooltipHelper.BuildItemTooltipContent(data, null, skipMoney: false);
            }
            catch (KeyNotFoundException)
            {
                // The price needs world stats that are not always loaded.
                return CandideTooltipHelper.BuildItemTooltipContent(data, null);
            }
        }
        if (entry.Kind == EntryKind.Resource)
        {
            ConstructionResourceDataModel? resource = ConstructionResourcesDataBase.GetConstructionResourceOrNull(entry.Id);
            return resource.HasValue ? new List<CandideUiElement>(CandideTooltipHelper.BuildResourceTooltip(resource.Value)) : null;
        }
        return null;
    }

    private static void EnsureTarget(GraphicsDevice device, int width, int height)
    {
        if (_target != null && !_target.IsDisposed && _target.Width == width && _target.Height == height)
        {
            return;
        }
        _target?.Dispose();
        _target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        _targetId = Globals.ImGuiRenderer.BindTexture(_target);
    }

    // A private desktop with a window styled like CandideTooltipWindow (which the game keeps to itself).
    private static void EnsureWindow()
    {
        if (_desktop != null)
        {
            return;
        }
        _desktop = new CandideDesktop();
        _panel = new CandideVerticalStackPanel();
        _window = new CandideWindow(WindowGraphics.Dark)
        {
            Padding = new CandideThickness(TooltipPaddingX, TooltipPaddingY),
            MaxWidth = CandideTooltipWindow.DefaultMaxWidth
        };
        _window.SetChild(_panel);
        _window.Show(_desktop);
    }
}
