using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Candide;
using Candide.GameModels;
using Candide.GameModels.Models.Constructions;
using CandideServer;
using CandideServer.Entities;
using CandideServer.Models;
using CandideServer.Models.Buildings;
using CandideServer.ServerControllers;
using CandideServer.ServerManagers;
using CandideServer.SimulationModels;
using CandideServer.SyncStrategies;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Shared;
using Shared.Data;
using Shared.Data.Furniture;
using Shared.Entity;
using Shared.Entity.Components;
using Shared.Models;
using Shared.Models.Construction;
using Shared.Models.Player;
using S = Shared.Models.WorldTile.StructureType;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace RomesteadCheatMenu;

internal enum BuildAction
{
    Build,
    Upgrade,
    Demolish
}

// Mouse tool for the town: finish construction sites, upgrade or demolish buildings, and clear what the world
// generator placed (ghost town houses and walls, ruins). Targets are found in the host's own game state for the
// highlight; the change itself runs on the local server through the same routines the game uses.
internal static class BuildTool
{
    public const int MaxRadius = 10;

    private const float TileSize = 16f;

    public static readonly BuildAction[] Actions = { BuildAction.Build, BuildAction.Upgrade, BuildAction.Demolish };

    // Structure tiles that make up generated houses, city walls and ruins.
    private static readonly HashSet<S> GeneratedStructures = new HashSet<S>
    {
        S.Ruin, S.WallWood, S.WallBrick, S.Construction, S.ConstructionWalkable, S.BehindWallCollisionBlock,
        S.GhostTownCityWall, S.GhostTownCityWallTower, S.GhostTownHouseWallExterior,
        S.GhostTownHouseWallInterior1, S.GhostTownHouseWallInterior2, S.GhostTownHouseWallInterior3, S.GhostTownHouseWallInterior4
    };

    // Filled on the server thread, shown on the game thread.
    private static readonly ConcurrentQueue<string> Notes = new ConcurrentQueue<string>();
    // Targets already handled while the button is held, so dragging touches each one once.
    private static readonly HashSet<string> Stroke = new HashSet<string>();

    private static bool _waitForRelease;
    private static bool _leftWasDown;
    private static bool _escapeWasDown;
    private static bool _rightWasDown;
    private static int _lastScroll;
    private static string _note;
    private static long _noteUntil;

    public static bool Active { get; private set; }

    private enum TargetKind
    {
        None,
        Site,
        Building,
        Decoration,
        Area
    }

    private struct Target
    {
        public TargetKind Kind;
        public Guid Id;
        public Rectangle Bounds;
        public string ConstructionId;
        public string UpgradeId;
        // Why a click would do nothing; shown instead of applying.
        public string Problem;

        public string Key => Kind == TargetKind.Area ? $"area:{Bounds.X}:{Bounds.Y}" : Id.ToString();
    }

    // ------------------------------------------------------------------ menu

    public static BuildAction CurrentAction => (BuildAction)Math.Clamp(Settings.Data.BuildAction, 0, Actions.Length - 1);

    public static void Select(BuildAction action)
    {
        Settings.Data.BuildAction = (int)action;
        Settings.MarkDirty();
    }

    public static string ActionLabel(BuildAction action)
    {
        return action switch
        {
            BuildAction.Build => Loc.T("Build construction sites", "Построить стройплощадки"),
            BuildAction.Upgrade => Loc.T("Upgrade buildings", "Улучшить здания"),
            _ => Loc.T("Demolish", "Снести")
        };
    }

    public static string ActionHint(BuildAction action)
    {
        return action switch
        {
            BuildAction.Build => Loc.T(
                "Click a construction site (a building you placed that is not built yet): it is finished at once, without materials or builders. "
                + "Hold the button and drag to build every site you pass over, handy for roads.",
                "Щёлкните по стройплощадке (размещённому, но ещё не построенному зданию): оно сразу достроится, без материалов и строителей. "
                + "Зажмите кнопку и проведите, чтобы построить все площадки на пути, удобно для дорог."),
            BuildAction.Upgrade => Loc.T(
                "Click a building or decoration of your town: it goes up one level for free, the same upgrade the game offers. "
                + "If there are several upgrade options, the one you have already unlocked is used.",
                "Щёлкните по зданию или украшению города: оно бесплатно поднимется на уровень, это то же улучшение, что предлагает игра. "
                + "Если вариантов улучшения несколько, берётся уже открытый у вас."),
            _ => Loc.T(
                "A click on a building, decoration or construction site of your town removes it, like the game's own demolition. "
                + "A click anywhere else clears the square under the cursor of what the world generated there: houses and walls of ghost towns, ruins and other static objects. "
                + "Trees, rocks, doors, entrances, chests and everything you built yourself stay.",
                "Щелчок по зданию, украшению или стройплощадке вашего города сносит его, как обычный снос в игре. "
                + "Щелчок в любом другом месте расчищает квадрат под курсором от того, что сгенерировал мир: дома и стены городов-призраков, руины и другие неподвижные объекты. "
                + "Деревья, камни, двери, входы, сундуки и всё, что построили вы сами, остаются.")
        };
    }

    public static bool CanUse(out string reason)
    {
        if (!GameAccess.InWorld)
        {
            reason = Loc.T("Load a world first.", "Сначала загрузите мир.");
            return false;
        }
        if (!GameAccess.IsHost)
        {
            reason = Loc.T("Buildings can only be changed in your own world (you need to be the host).",
                "Постройки можно менять только в своём мире (нужно быть хостом).");
            return false;
        }
        var outside = ServerRunState.OutsideWorld;
        if (outside == null || GameState.CurrentWorld == null || GameState.CurrentWorld.Id != outside.Id)
        {
            reason = Loc.T("Go outside: this works on the world map, not inside buildings or dungeons.",
                "Выйдите на поверхность: это работает на карте мира, а не внутри зданий и подземелий.");
            return false;
        }
        reason = null;
        return true;
    }

    // ------------------------------------------------------------------ mouse mode (game thread)

    public static string Start()
    {
        if (!CanUse(out string reason))
        {
            return reason;
        }
        Active = true;
        _waitForRelease = true;
        _leftWasDown = true;
        _escapeWasDown = true;
        _rightWasDown = true;
        _lastScroll = Mouse.GetState().ScrollWheelValue;
        Stroke.Clear();
        CheatMenu.Close();
        Log.Info("Build tool started: " + CurrentAction);
        return null;
    }

    public static void Stop(bool reopenMenu)
    {
        if (!Active)
        {
            return;
        }
        Active = false;
        Stroke.Clear();
        if (reopenMenu && GameAccess.InWorld)
        {
            CheatMenu.Open();
        }
        else
        {
            InputBlocker.OnMenuClosed();
        }
    }

    // Runs before the game reads input: leaving the tool must not reach the game as Esc or a click.
    public static void BeforeInput(KeyboardState keyboard)
    {
        if (!Active)
        {
            return;
        }
        if (!CanUse(out _))
        {
            Stop(reopenMenu: false);
            return;
        }
        bool escape = keyboard.IsKeyDown(Keys.Escape);
        bool right = Mouse.GetState().RightButton == ButtonState.Pressed;
        bool leave = (escape && !_escapeWasDown) || (right && !_rightWasDown && !_waitForRelease);
        _escapeWasDown = escape;
        _rightWasDown = right;
        if (leave)
        {
            Stop(reopenMenu: true);
        }
    }

    public static void ClientUpdate()
    {
        while (Notes.TryDequeue(out string note))
        {
            ShowNote(note);
        }
        if (!Active || Globals.Game?.Camera == null)
        {
            return;
        }
        Terraform.UpdateViewport();

        MouseState mouse = Mouse.GetState();
        bool left = mouse.LeftButton == ButtonState.Pressed;
        if (_waitForRelease)
        {
            // The click on the menu button must not act on the map.
            if (!left && mouse.RightButton == ButtonState.Released)
            {
                _waitForRelease = false;
            }
            _leftWasDown = left;
            _lastScroll = mouse.ScrollWheelValue;
            return;
        }

        int wheel = mouse.ScrollWheelValue - _lastScroll;
        _lastScroll = mouse.ScrollWheelValue;
        if (wheel != 0 && CurrentAction == BuildAction.Demolish)
        {
            int steps = wheel / 120;
            if (steps == 0)
            {
                steps = Math.Sign(wheel);
            }
            Settings.Data.DemolishRadius = Math.Clamp(Settings.Data.DemolishRadius + steps, 0, MaxRadius);
            Settings.MarkDirty();
        }

        bool pressed = left && !_leftWasDown;
        _leftWasDown = left;
        if (!left)
        {
            Stroke.Clear();
            return;
        }
        Target target = FindTarget(Terraform.MouseTile(mouse));
        if (target.Kind == TargetKind.None)
        {
            if (pressed)
            {
                ShowNote(NothingHere());
            }
            return;
        }
        if (!Stroke.Add(target.Key))
        {
            return;
        }
        if (target.Problem != null)
        {
            ShowNote(target.Problem);
            return;
        }
        Apply(target);
    }

    private static string NothingHere()
    {
        return CurrentAction == BuildAction.Build
            ? Loc.T("There is no construction site under the cursor.", "Под курсором нет стройплощадки.")
            : Loc.T("There is no building or decoration of your town under the cursor.", "Под курсором нет здания или украшения вашего города.");
    }

    private static void ShowNote(string text)
    {
        _note = text;
        _noteUntil = Environment.TickCount64 + 3000;
    }

    // ------------------------------------------------------------------ targets (game thread)

    private static Target FindTarget(Point tile)
    {
        BuildAction action = CurrentAction;
        if (action != BuildAction.Upgrade)
        {
            foreach (ConstructionSite site in GameState.ConstructionSites.Values)
            {
                if (site.TileBounds.Contains(tile))
                {
                    return new Target { Kind = TargetKind.Site, Id = site.Id, Bounds = site.TileBounds, ConstructionId = site.ConstructionId };
                }
            }
            if (action == BuildAction.Build)
            {
                return default;
            }
        }
        foreach (Building building in GameState.Buildings.Values)
        {
            BuildingInstanceModel model = building.Model;
            if (model != null && model.TileBounds.Contains(tile))
            {
                return Check(new Target { Kind = TargetKind.Building, Id = model.Id, Bounds = model.TileBounds, ConstructionId = model.ConstructionId }, action);
            }
        }
        foreach (Decoration decoration in GameState.Decorations.Values)
        {
            if (decoration.Model != null && decoration.Model.TileBounds.Contains(tile))
            {
                return Check(new Target { Kind = TargetKind.Decoration, Id = decoration.Model.Id, Bounds = decoration.Model.TileBounds, ConstructionId = decoration.Model.ConstructionId }, action);
            }
        }
        if (action == BuildAction.Demolish)
        {
            int radius = Math.Clamp(Settings.Data.DemolishRadius, 0, MaxRadius);
            return new Target { Kind = TargetKind.Area, Bounds = new Rectangle(tile.X - radius, tile.Y - radius, radius * 2 + 1, radius * 2 + 1) };
        }
        return default;
    }

    private static Target Check(Target target, BuildAction action)
    {
        if (action == BuildAction.Upgrade)
        {
            ConstructionModel next = NextUpgrade(target.ConstructionId);
            target.UpgradeId = next?.Id;
            if (next == null)
            {
                target.Problem = Loc.T($"{ConstructionName(target.ConstructionId)} has no further upgrades.",
                    $"У «{ConstructionName(target.ConstructionId)}» больше нет улучшений.");
            }
        }
        else if (action == BuildAction.Demolish && target.Kind == TargetKind.Building
            && target.ConstructionId != null && target.ConstructionId.StartsWith("town_core", StringComparison.Ordinal))
        {
            target.Problem = Loc.T("The town core can't be demolished: the town would break.", "Центр города снести нельзя: город сломается.");
        }
        return target;
    }

    // The upgrades the game offers for this construction; an already unlocked one first.
    private static ConstructionModel NextUpgrade(string constructionId)
    {
        ConstructionModel current = ConstructionDataBase.GetConstructionOrNull(constructionId);
        if (current?.SpawnedId == null)
        {
            return null;
        }
        List<ConstructionModel> upgrades = current.Type switch
        {
            ConstructionType.Building => ConstructionDataBase.GetUpgradesForBuilding(current.SpawnedId),
            ConstructionType.Decoration => ConstructionDataBase.GetUpgradesForDecoration(current.SpawnedId),
            _ => null
        };
        return upgrades?
            .Where(upgrade => upgrade.Type == current.Type)
            .OrderBy(upgrade => GameState.ConstructionRecipes.Contains(upgrade.Id) ? 0 : 1)
            .ThenBy(upgrade => upgrade.UpgradeLevel)
            .FirstOrDefault();
    }

    private static string ConstructionName(string constructionId)
    {
        if (constructionId == null)
        {
            return "?";
        }
        string key = constructionId + "*construction:name";
        return (Loc.IsRussian ? LocaleFile.Russian.Get(key) : null) ?? LocaleFile.English.Get(key) ?? constructionId;
    }

    // ------------------------------------------------------------------ applying (queued to the server thread)

    private static void Apply(Target target)
    {
        string name = ConstructionName(target.ConstructionId);
        switch (CurrentAction)
        {
            case BuildAction.Build:
                Build(target.Id, Globals.Game.Player?.Id ?? Guid.Empty, name);
                break;
            case BuildAction.Upgrade:
                Upgrade(target, name, ConstructionName(target.UpgradeId));
                break;
            default:
                if (target.Kind == TargetKind.Area)
                {
                    ClearArea(target.Bounds);
                }
                else
                {
                    Demolish(target, name);
                }
                break;
        }
    }

    private static void Build(Guid siteId, Guid hostEntityId, string name)
    {
        ServerQueue.Run(delegate
        {
            try
            {
                if (!ServerGameState.ConstructionSites.ContainsKey(siteId))
                {
                    return;
                }
                PlayerCharacterModel character = PlayerCharacterServerManager.GetPlayerCharacterFromEntityId(hostEntityId)?.Character;
                var config = ServerGameState.Config;
                bool savedRule = config?.DisableConstructionMaterialRequirements ?? false;
                if (config != null)
                {
                    config.DisableConstructionMaterialRequirements = true;
                }
                try
                {
                    // Same routine as a player finishing the site with a hammer; with the rule off, missing materials don't stop it.
                    ConstructionSitesServerManager.TryBuildConstructionSite(siteId, character);
                }
                finally
                {
                    if (config != null)
                    {
                        config.DisableConstructionMaterialRequirements = savedRule;
                    }
                }
                bool built = !ServerGameState.ConstructionSites.ContainsKey(siteId);
                Log.Info($"Build tool: construction site {name} ({siteId}) {(built ? "built" : "was not built")}");
                Notes.Enqueue(built
                    ? Loc.T($"Built: {name}", $"Построено: {name}")
                    : Loc.T($"{name} could not be built, see the log.", $"«{name}» не удалось построить, подробности в логе."));
            }
            catch (Exception e)
            {
                Log.Error("Build tool: building a construction site failed", e);
                Notes.Enqueue(Loc.T("Error while building, see the log.", "Ошибка при строительстве, подробности в логе."));
            }
        });
    }

    private static void Upgrade(Target target, string name, string upgradeName)
    {
        Guid id = target.Id;
        string upgradeId = target.UpgradeId;
        bool building = target.Kind == TargetKind.Building;
        ServerQueue.Run(delegate
        {
            try
            {
                bool upgraded;
                if (building)
                {
                    BuildingsServerManager.TryUpgradeBuilding(id, upgradeId, consumeMaterials: false);
                    upgraded = ServerGameState.Buildings.TryGetValue(id, out BuildingSimulationModel model) && model.InstanceModel.ConstructionId == upgradeId;
                }
                else
                {
                    upgraded = DecorationsServerManager.TryUpgradeDecoration(id, upgradeId, consumeMaterials: false, Guid.Empty);
                }
                Log.Info($"Build tool: upgrade {name} -> {upgradeId}: {(upgraded ? "done" : "failed")}");
                Notes.Enqueue(upgraded
                    ? Loc.T($"Upgraded: {name} → {upgradeName}", $"Улучшено: {name} → {upgradeName}")
                    : Loc.T($"{name} could not be upgraded, see the log.", $"«{name}» не удалось улучшить, подробности в логе."));
            }
            catch (Exception e)
            {
                Log.Error("Build tool: upgrade failed", e);
                Notes.Enqueue(Loc.T("Error while upgrading, see the log.", "Ошибка при улучшении, подробности в логе."));
            }
        });
    }

    private static void Demolish(Target target, string name)
    {
        Guid id = target.Id;
        TargetKind kind = target.Kind;
        ServerQueue.Run(delegate
        {
            try
            {
                bool removed;
                switch (kind)
                {
                    case TargetKind.Site:
                        removed = ServerGameState.ConstructionSites.TryGetValue(id, out ConstructionSiteModel site);
                        if (removed)
                        {
                            ConstructionSitesServerManager.RemoveConstructionSite(site);
                        }
                        break;
                    case TargetKind.Building:
                        // Citizens move out, storages drop their contents and the materials come back, as with the game's demolition.
                        removed = BuildingsServerManager.TryRemoveBuilding(id, SyncStrategy.SendToAll);
                        break;
                    default:
                        removed = DecorationsServerManager.RemoveDecoration(id);
                        break;
                }
                Log.Info($"Build tool: demolish {kind} {name} ({id}): {(removed ? "done" : "failed")}");
                Notes.Enqueue(removed
                    ? Loc.T($"Demolished: {name}", $"Снесено: {name}")
                    : Loc.T($"{name} could not be demolished, see the log.", $"«{name}» не удалось снести, подробности в логе."));
            }
            catch (Exception e)
            {
                Log.Error("Build tool: demolition failed", e);
                Notes.Enqueue(Loc.T("Error while demolishing, see the log.", "Ошибка при сносе, подробности в логе."));
            }
        });
    }

    private static void ClearArea(Rectangle area)
    {
        ServerQueue.Run(delegate
        {
            try
            {
                (int tiles, int objects) = ClearAreaOnServer(area);
                if (tiles > 0 || objects > 0)
                {
                    Log.Info($"Build tool: cleared {tiles} structure tile(s) and {objects} object(s) in {area}");
                    Notes.Enqueue(Loc.T($"Cleared: {tiles} wall tiles, {objects} objects", $"Расчищено: тайлов стен {tiles}, объектов {objects}"));
                }
                else
                {
                    Notes.Enqueue(Loc.T("Nothing generated to clear here.", "Здесь нечего расчищать."));
                }
            }
            catch (Exception e)
            {
                Log.Error("Build tool: clearing failed", e);
                Notes.Enqueue(Loc.T("Error while clearing, see the log.", "Ошибка при расчистке, подробности в логе."));
            }
        });
    }

    private static (int Tiles, int Objects) ClearAreaOnServer(Rectangle area)
    {
        WorldTile[,] tiles = ServerGameState.WorldTiles;
        var outside = ServerRunState.OutsideWorld;
        if (tiles == null || outside == null || ServerRunState.ServerChunkedWorld == null)
        {
            return (0, 0);
        }
        area = Rectangle.Intersect(area, new Rectangle(1, 1, tiles.GetLength(0) - 2, tiles.GetLength(1) - 2));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return (0, 0);
        }

        // Town objects are only removed as a whole (click on them), never piece by piece.
        var owned = new List<Rectangle>();
        owned.AddRange(ServerGameState.Buildings.Values.Select(b => b.InstanceModel.TileBounds));
        owned.AddRange(ServerGameState.Decorations.Values.Select(d => d.TileBounds));
        owned.AddRange(ServerGameState.ConstructionSites.Values.Select(c => c.TileBounds));
        owned.RemoveAll(bounds => !bounds.Intersects(area));

        var changes = new List<(int X, int Y, WorldTile Tile)>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                WorldTile tile = tiles[x, y];
                var point = new Point(x, y);
                if (GeneratedStructures.Contains(tile.Structure) && !owned.Any(bounds => bounds.Contains(point)))
                {
                    changes.Add((x, y, new WorldTile(tile.Ground, S.None)));
                }
            }
        }
        int changed = changes.Count > 0 ? Terraform.ApplyTilesOnServer(changes) : 0;

        var remove = new List<Guid>();
        foreach (ServerEntityModel entity in EntitySController.GetEntitiesInTileRectangleArea(area, outside.Id, new List<ServerEntityModel>()))
        {
            var point = new Point((int)MathF.Floor(entity.Position.X / TileSize), (int)MathF.Floor(entity.Position.Y / TileSize));
            if (IsGeneratedObject(entity) && !owned.Any(bounds => bounds.Contains(point)))
            {
                remove.Add(entity.Id);
            }
        }
        foreach (Guid id in remove)
        {
            EntityServerManager.RemoveEntity(id, new EntityRemoveInfo { RemoveType = EntityRemoveType.Removed });
        }
        return (changed, remove.Count);
    }

    // Static scenery the world placed: not living, not a resource, not interactive, not built or owned by a player.
    private static bool IsGeneratedObject(ServerEntityModel entity)
    {
        EntityWrapper wrapper = entity.EntityWrapper;
        if (entity.ServerOnly || wrapper == null || !wrapper.Immovable)
        {
            return false;
        }
        // Trees, rocks and ore can be chopped or mined; only pieces placed by the map designers go.
        if (wrapper.Mask.HasFlags(Component.Health) && entity.EntityType != EntityType.PlacedInEditor)
        {
            return false;
        }
        // Doors, dungeon entrances, shrines and quest objects.
        if (wrapper.Mask.HasFlags(Component.Interactable))
        {
            return false;
        }
        if (entity.InventoryId.HasValue || (entity.ConstructionMaterials != null && !entity.ConstructionMaterials.IsEmpty))
        {
            return false;
        }
        if (entity.Parameters != null && (entity.Parameters.ContainsKey("owner_character_id") || entity.Parameters.ContainsKey("inventory_id")))
        {
            return false;
        }
        if (ServerGameState.Decorations.ContainsKey(entity.Id) || EntityIds.CropEntities.ContainsKey(entity.BaseId))
        {
            return false;
        }
        return !FurnitureDataBase.TryGetFurnitureIdFromEntity(entity.BaseId, wrapper.Frame, out _);
    }

    // ------------------------------------------------------------------ overlay (ImGui)

    private static Vec4 ActionColor(BuildAction action)
    {
        return action switch
        {
            BuildAction.Build => new Vec4(0.36f, 0.82f, 0.42f, 1f),
            BuildAction.Upgrade => new Vec4(0.38f, 0.66f, 0.98f, 1f),
            _ => new Vec4(0.92f, 0.38f, 0.32f, 1f)
        };
    }

    public static void DrawOverlay()
    {
        if (!Active || Globals.Game?.Camera == null || !Terraform.HasViewport)
        {
            return;
        }
        float s = UiFonts.Scale;
        Point tile = Terraform.MouseTile(Mouse.GetState());
        Target target = FindTarget(tile);
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        if (target.Kind != TargetKind.None)
        {
            Vec4 color = target.Problem != null ? new Vec4(0.62f, 0.62f, 0.62f, 1f) : ActionColor(CurrentAction);
            Vec2 min = Terraform.WorldToScreen(target.Bounds.Left * TileSize, target.Bounds.Top * TileSize);
            Vec2 max = Terraform.WorldToScreen(target.Bounds.Right * TileSize, target.Bounds.Bottom * TileSize);
            draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vec4(color.X, color.Y, color.Z, 0.3f)));
            draw.AddRect(min, max, ImGui.GetColorU32(new Vec4(color.X, color.Y, color.Z, 0.95f)), 0f, ImDrawFlags.None, Math.Max(1f, 2f * s));
        }
        draw.AddRect(Terraform.WorldToScreen(tile.X * TileSize, tile.Y * TileSize), Terraform.WorldToScreen((tile.X + 1) * TileSize, (tile.Y + 1) * TileSize),
            ImGui.GetColorU32(new Vec4(1f, 1f, 1f, 0.8f)), 0f, ImDrawFlags.None, 1f);
        DrawHud(target, s);
    }

    private static void DrawHud(Target target, float s)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        bool pushedFont = UiFonts.HasMain;
        if (pushedFont)
        {
            ImGui.PushFont(UiFonts.Main);
        }
        int colorCount = Ui.PushTheme();
        int styleCount = Ui.PushStyle(s);
        ImGui.SetNextWindowPos(new Vec2(io.DisplaySize.X * 0.5f, 14f * s), ImGuiCond.Always, new Vec2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove;
        bool open = true;
        bool visible = ImGui.Begin("##build_hud", ref open, flags);
        try
        {
            if (visible)
            {
                BuildAction action = CurrentAction;
                Ui.Colored(Ui.Accent, Loc.T("Buildings: ", "Постройки: ") + ActionLabel(action));
                Ui.Text(action switch
                {
                    BuildAction.Build => Loc.T(
                        "LMB: build the site under the cursor (hold and drag for several)   ·   RMB / Esc / Ctrl+0: back to the menu",
                        "ЛКМ: построить площадку под курсором (зажать и провести: несколько)   ·   ПКМ / Esc / Ctrl+0: в меню"),
                    BuildAction.Upgrade => Loc.T(
                        "LMB: upgrade the building under the cursor by one level   ·   RMB / Esc / Ctrl+0: back to the menu",
                        "ЛКМ: улучшить здание под курсором на уровень   ·   ПКМ / Esc / Ctrl+0: в меню"),
                    _ => Loc.T(
                        "LMB: demolish the town object under the cursor or clear the square   ·   Wheel: square size   ·   RMB / Esc / Ctrl+0: back to the menu",
                        "ЛКМ: снести объект города под курсором или расчистить квадрат   ·   Колесо: размер квадрата   ·   ПКМ / Esc / Ctrl+0: в меню")
                });
                Ui.Disabled(Describe(target, action));
                if (_note != null && Environment.TickCount64 < _noteUntil)
                {
                    Ui.Colored(Ui.Good, _note);
                }
            }
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
    }

    private static string Describe(Target target, BuildAction action)
    {
        if (target.Kind == TargetKind.None)
        {
            return action == BuildAction.Build
                ? Loc.T("Point at a construction site.", "Наведите курсор на стройплощадку.")
                : Loc.T("Point at a building or decoration of your town.", "Наведите курсор на здание или украшение вашего города.");
        }
        if (target.Problem != null)
        {
            return target.Problem;
        }
        string name = ConstructionName(target.ConstructionId);
        switch (target.Kind)
        {
            case TargetKind.Area:
                string size = $"{target.Bounds.Width}×{target.Bounds.Height}";
                return Loc.T($"Clear the {size} square of generated walls, ruins and objects", $"Расчистить квадрат {size} от сгенерированных стен, руин и объектов");
            case TargetKind.Site:
                return action == BuildAction.Build
                    ? Loc.T($"Construction site: {name}", $"Стройплощадка: {name}")
                    : Loc.T($"Remove the construction site: {name}", $"Убрать стройплощадку: {name}");
            default:
                return action == BuildAction.Upgrade
                    ? $"{name} → {ConstructionName(target.UpgradeId)}"
                    : Loc.T($"Demolish: {name}", $"Снести: {name}");
        }
    }
}
