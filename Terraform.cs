using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Candide;
using Candide.GameModels;
using Candide.World;
using CandideServer;
using CandideServer.Models;
using CandideServer.ServerManagers;
using CandideServer.ServerSystems;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Shared.Data;
using Shared.Entity;
using Shared.Models;
using G = Shared.Models.WorldTile.GroundType;
using S = Shared.Models.WorldTile.StructureType;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace RomesteadCheatMenu;

internal enum BrushKind
{
    // Sets the ground and/or structure of every tile under the brush.
    Paint,
    // Turns only the listed structures back into plain ground.
    Remove
}

internal sealed class BrushDef
{
    public string Id;
    public string GroupEn;
    public string GroupRu;
    public string LabelEn;
    public string LabelRu;
    public string HintEn;
    public string HintRu;
    public BrushKind Kind;
    public G? Ground;
    public S? Structure;
    public HashSet<S> Targets;

    public string Group => Loc.T(GroupEn, GroupRu ?? GroupEn);

    public string Label => Loc.T(LabelEn, LabelRu ?? LabelEn);

    public string Hint => Loc.T(HintEn, HintRu ?? HintEn);

    public bool PaintsGroundOnly => Kind == BrushKind.Paint && Ground.HasValue && !Structure.HasValue;
}

// Tile brush for the outdoor map. Every tile is a ground (grass, sand, road...) plus a structure
// (cliff, water, pit...). Changes are made on the local server, where tiles are saved with the
// world and terrain collisions are rebuilt, then mirrored into the host's own map and re-rendered.
internal static class Terraform
{
    public const int MaxRadius = 20;
    public const int MaxAreaRadius = 60;

    private const string PickedId = "picked";
    private const float TileSize = 16f;
    private const long StampIntervalMs = 25;
    private const int MaxUndoStrokes = 30;
    private const int MaxUndoTiles = 250000;

    private static readonly HashSet<S> Cliffs = new HashSet<S>
    {
        S.PlainsCliff, S.PlainsCliffDirt, S.DryPlainsCliff, S.DesertCliff, S.DesertCliffAndPit, S.BasaltCliff, S.Hill
    };

    private static readonly HashSet<S> WaterAndPits = new HashSet<S>
    {
        S.Water, S.SwampWater, S.Pit, S.Hole, S.DesertPit, S.BasaltPit, S.Lava, S.LavaMagma
    };

    private static readonly HashSet<S> TreeWalls = new HashSet<S> { S.TallTreeLeft, S.TallTreeRight, S.TallTreeBush };

    private static readonly HashSet<S> NaturalTerrain = new HashSet<S>(Cliffs.Concat(WaterAndPits).Concat(TreeWalls));

    // Tiles that belong to game objects (buildings, fields, trees, bushes, walls) are never touched.
    private static readonly HashSet<S> Protected = new HashSet<S>
    {
        S.Crop, S.Construction, S.ConstructionWalkable, S.Tree, S.Bush, S.Ruin, S.WallWood, S.WallBrick, S.UnmodifiableRoad,
        S.BehindWallCollisionBlock, S.TransportZone0, S.TransportZone1, S.TransportZone2, S.TransportZone3
    };

    private static readonly (G Type, string En, string Ru)[] GroundLabels =
    {
        (G.Grass, "Grass", "Трава"),
        (G.TallGrass, "Tall grass", "Высокая трава"),
        (G.DryGrass, "Dry grass", "Сухая трава"),
        (G.DryTallGrass, "Dry tall grass", "Сухая высокая трава"),
        (G.DarkBrownGrass, "Dark grass", "Тёмная трава"),
        (G.LightBrownGrass, "Light grass", "Светлая трава"),
        (G.BurntGrass, "Burnt grass", "Выжженная трава"),
        (G.SwampGrass, "Swamp grass", "Болотная трава"),
        (G.Dirt, "Dirt", "Земля"),
        (G.TallDirt, "Tall dirt", "Земля (высокая)"),
        (G.Clay, "Clay", "Глина"),
        (G.ClayDesert, "Desert clay", "Пустынная глина"),
        (G.Sand, "Sand", "Песок"),
        (G.Beach, "Beach", "Пляж"),
        (G.Stone, "Stone", "Камень"),
        (G.DesertStone, "Desert stone", "Пустынный камень"),
        (G.LowerDesertStone, "Lower desert stone", "Пустынный камень (нижний)"),
        (G.DesertPitEdge, "Desert pit edge", "Край пустынной ямы"),
        (G.AshPile, "Ash", "Пепел"),
        (G.BasaltLow, "Basalt", "Базальт"),
        (G.TallBasalt, "Tall basalt", "Базальт (высокий)"),
        (G.CooledLavaFlow, "Cooled lava", "Застывшая лава"),
        (G.DirtRoad, "Dirt road", "Грунтовая дорога"),
        (G.CobbleStoneRoad, "Cobblestone road", "Булыжная дорога"),
        (G.BrickRoad, "Brick road", "Кирпичная дорога"),
        (G.ConcreteRoad, "Concrete road", "Бетонная дорога"),
        (G.MarbleRoad, "Marble road", "Мраморная дорога"),
        (G.MarbleBrickRoad, "Marble tiles", "Мраморная плитка"),
        (G.TerracottaRoad, "Terracotta road", "Терракотовая дорога"),
        (G.BasaltRoad, "Basalt road", "Базальтовая дорога")
    };

    public static readonly IReadOnlyList<BrushDef> Brushes = CreateBrushes();

    // Client (game) thread.
    private static readonly ConcurrentQueue<Action> ClientQueue = new ConcurrentQueue<Action>();
    private static BrushDef _picked;
    private static bool _waitForRelease;
    private static bool _painting;
    private static Point _lastTile;
    private static long _lastStampTicks;
    private static int _lastScroll;
    private static bool _escapeWasDown;
    private static bool _rightWasDown;
    private static bool _middleWasDown;
    private static bool _undoWasDown;
    private static int _strokeCounter;
    private static Vector2 _viewport;
    private static string _note;
    private static long _noteUntil;

    // Local server thread.
    private static readonly List<Stroke> UndoStack = new List<Stroke>();
    private static WorldTile[,] _undoTiles;
    private static volatile int _undoCount;

    public static bool BrushActive { get; private set; }

    public static int UndoCount => _undoCount;

    private sealed class Stroke
    {
        public readonly int Id;
        public readonly List<TileChange> Changes = new List<TileChange>();

        public Stroke(int id)
        {
            Id = id;
        }
    }

    private readonly struct TileChange
    {
        public readonly int X;
        public readonly int Y;
        public readonly WorldTile Old;
        public readonly WorldTile New;

        public TileChange(int x, int y, WorldTile old, WorldTile next)
        {
            X = x;
            Y = y;
            Old = old;
            New = next;
        }
    }

    // ------------------------------------------------------------------ brushes

    private static List<BrushDef> CreateBrushes()
    {
        var list = new List<BrushDef>
        {
            RemoveBrush("remove:cliffs", Cliffs,
                "Mountains & cliffs → flat ground", "Горы и скалы → ровная земля",
                "Cliffs, mountains and hills under the brush turn into plain flat ground. Water, buildings and fields stay as they are.",
                "Скалы, горы и холмы под кистью становятся обычной ровной землёй. Вода, постройки и поля не трогаются."),
            RemoveBrush("remove:water", WaterAndPits,
                "Water & pits → fill in", "Вода и ямы → засыпать",
                "Water, swamp, pits, holes and lava under the brush are filled in.",
                "Вода, болото, ямы, впадины и лава под кистью становятся сушей."),
            RemoveBrush("remove:treewalls", TreeWalls,
                "Forest walls → remove", "Лесные стены → убрать",
                "The solid impassable walls of trees along the forest edges.",
                "Сплошные непроходимые стены деревьев по краям леса."),
            StructureBrush("structure:water", "Water and slopes", "Вода и склоны", S.Water, "Water", "Вода",
                "A lake or river. The game draws the bank slope that goes under the water by itself along the edge: paint water next to land.",
                "Озеро или река. Откос берега, уходящий под воду, игра строит сама по краю воды: рисуйте воду рядом с сушей."),
            StructureBrush("structure:swamp", "Water and slopes", "Вода и склоны", S.SwampWater, "Swamp water", "Болотная вода", null, null),
            StructureBrush("structure:pit", "Water and slopes", "Вода и склоны", S.Pit, "Pit with slopes", "Яма с откосами",
                "A deep pit with earth slopes but no water.", "Глубокая яма с земляными склонами, но без воды."),
            StructureBrush("structure:hole", "Water and slopes", "Вода и склоны", S.Hole, "Hole", "Впадина",
                "A shallow dip in the ground.", "Неглубокая выемка в земле."),
            StructureBrush("structure:plains_cliff", "Cliffs and hills", "Скалы и холмы", S.PlainsCliff, "Plains cliff", "Скала равнин", null, null),
            StructureBrush("structure:plains_cliff_dirt", "Cliffs and hills", "Скалы и холмы", S.PlainsCliffDirt, "Dirt cliff", "Земляная скала", null, null),
            StructureBrush("structure:dry_plains_cliff", "Cliffs and hills", "Скалы и холмы", S.DryPlainsCliff, "Dry plains cliff", "Скала сухих равнин", null, null),
            StructureBrush("structure:desert_cliff", "Cliffs and hills", "Скалы и холмы", S.DesertCliff, "Desert cliff", "Пустынная скала", null, null),
            StructureBrush("structure:basalt_cliff", "Cliffs and hills", "Скалы и холмы", S.BasaltCliff, "Basalt cliff", "Базальтовая скала", null, null),
            StructureBrush("structure:hill", "Cliffs and hills", "Скалы и холмы", S.Hill, "Hill, low ledge", "Холм, низкий уступ", null, null)
        };
        foreach ((G type, string en, string ru) in GroundLabels)
        {
            string code = type.ToString();
            list.Add(new BrushDef
            {
                Id = "ground:" + code,
                GroupEn = "Ground (surface only)",
                GroupRu = "Земля (меняет только покрытие)",
                Kind = BrushKind.Paint,
                Ground = type,
                LabelEn = WithCode(en, code),
                LabelRu = WithCode(ru, code),
                HintEn = "Changes the ground surface. Cliffs and water under the brush are removed if the matching checkbox is on.",
                HintRu = "Меняет покрытие земли. Скалы и вода под кистью убираются, если включена соответствующая галочка."
            });
        }
        return list;
    }

    private static BrushDef RemoveBrush(string id, HashSet<S> targets, string labelEn, string labelRu, string hintEn, string hintRu)
    {
        return new BrushDef
        {
            Id = id,
            GroupEn = "Remove",
            GroupRu = "Убрать",
            Kind = BrushKind.Remove,
            Targets = targets,
            LabelEn = labelEn,
            LabelRu = labelRu,
            HintEn = hintEn,
            HintRu = hintRu
        };
    }

    private static BrushDef StructureBrush(string id, string groupEn, string groupRu, S structure, string labelEn, string labelRu, string hintEn, string hintRu)
    {
        string code = structure.ToString();
        return new BrushDef
        {
            Id = id,
            GroupEn = groupEn,
            GroupRu = groupRu,
            Kind = BrushKind.Paint,
            Structure = structure,
            LabelEn = WithCode(labelEn, code),
            LabelRu = WithCode(labelRu, code),
            HintEn = hintEn,
            HintRu = hintRu
        };
    }

    // "Tall grass (TallGrass)": the game's own tile name, as shown under the cursor in brush mode.
    private static string WithCode(string label, string code)
    {
        return Normalize(label) == Normalize(code) ? label : $"{label} ({code})";
    }

    private static string Normalize(string text)
    {
        return new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static BrushDef PickedBrush
    {
        get
        {
            if (_picked == null && Settings.Data.PickedGround >= 0 && Settings.Data.PickedStructure >= 0)
            {
                _picked = MakePicked((G)Settings.Data.PickedGround, (S)Settings.Data.PickedStructure);
            }
            return _picked;
        }
    }

    private static BrushDef MakePicked(G ground, S structure)
    {
        return new BrushDef
        {
            Id = PickedId,
            GroupEn = "Eyedropper",
            GroupRu = "Пипетка",
            Kind = BrushKind.Paint,
            Ground = ground,
            Structure = structure,
            LabelEn = $"{ground} + {structure}",
            HintEn = "The tile taken with the eyedropper (middle mouse button in brush mode): paints exactly the same ground and structure.",
            HintRu = "Тайл, взятый пипеткой (средняя кнопка мыши в режиме кисти): рисует точно такие же землю и структуру."
        };
    }

    public static BrushDef CurrentBrush
    {
        get
        {
            string id = Settings.Data.BrushId;
            if (id == PickedId && PickedBrush != null)
            {
                return PickedBrush;
            }
            foreach (BrushDef brush in Brushes)
            {
                if (brush.Id == id)
                {
                    return brush;
                }
            }
            return Brushes[0];
        }
    }

    public static IEnumerable<BrushDef> MenuBrushes()
    {
        if (PickedBrush != null)
        {
            yield return PickedBrush;
        }
        foreach (BrushDef brush in Brushes)
        {
            yield return brush;
        }
    }

    public static void Select(BrushDef brush)
    {
        Settings.Data.BrushId = brush.Id;
        Settings.MarkDirty();
    }

    public static Vec4 BrushColor(BrushDef brush)
    {
        if (brush.Kind == BrushKind.Remove)
        {
            return new Vec4(0.9f, 0.4f, 0.32f, 1f);
        }
        Color color = Color.Gray;
        if (brush.Structure.HasValue && brush.Structure.Value != S.None)
        {
            color = TileDataBase.Structures[(uint)brush.Structure.Value]?.Color ?? Color.Gray;
        }
        else if (brush.Ground.HasValue)
        {
            color = TileDataBase.Grounds[(uint)brush.Ground.Value]?.Color ?? Color.Gray;
        }
        if (color.A == 0)
        {
            color = Color.Gray;
        }
        return new Vec4(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
    }

    // ------------------------------------------------------------------ brush mode (game thread)

    public static bool CanTerraform(out string reason)
    {
        if (!GameAccess.InWorld)
        {
            reason = Loc.T("Load a world first.", "Сначала загрузите мир.");
            return false;
        }
        if (!GameAccess.IsHost)
        {
            reason = Loc.T("You can only change the map in your own world (you need to be the host).",
                "Менять карту можно только в своём мире (нужно быть хостом).");
            return false;
        }
        var outside = ServerRunState.OutsideWorld;
        if (outside == null || GameState.CurrentWorld == null || GameState.CurrentWorld.Id != outside.Id)
        {
            reason = Loc.T("Tiles can only be changed outdoors: leave the building or dungeon.",
                "Менять тайлы можно только на поверхности: выйдите из здания или подземелья.");
            return false;
        }
        reason = null;
        return true;
    }

    public static string StartBrush()
    {
        if (!CanTerraform(out string reason))
        {
            return reason;
        }
        BrushActive = true;
        _waitForRelease = true;
        _painting = false;
        _escapeWasDown = true;
        _rightWasDown = true;
        _middleWasDown = true;
        _undoWasDown = true;
        _lastScroll = Mouse.GetState().ScrollWheelValue;
        CheatMenu.Close();
        Log.Info("Terraform brush started: " + CurrentBrush.LabelEn);
        return null;
    }

    public static void StopBrush(bool reopenMenu)
    {
        if (!BrushActive)
        {
            return;
        }
        BrushActive = false;
        _painting = false;
        if (reopenMenu && GameAccess.InWorld)
        {
            CheatMenu.Open();
        }
        else
        {
            InputBlocker.OnMenuClosed();
        }
    }

    // Runs before the game reads input: leaving the brush must not reach the game as Esc or a click.
    public static void BeforeInput(KeyboardState keyboard)
    {
        if (!BrushActive)
        {
            return;
        }
        if (!CanTerraform(out _))
        {
            StopBrush(reopenMenu: false);
            return;
        }
        bool escape = keyboard.IsKeyDown(Keys.Escape);
        bool right = Mouse.GetState().RightButton == ButtonState.Pressed;
        bool leave = (escape && !_escapeWasDown) || (right && !_rightWasDown && !_waitForRelease);
        _escapeWasDown = escape;
        _rightWasDown = right;
        if (leave)
        {
            StopBrush(reopenMenu: true);
        }
    }

    public static void ClientUpdate()
    {
        while (ClientQueue.TryDequeue(out Action action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("Terraform client update", e);
            }
        }
        if (!BrushActive || Globals.Game?.Camera == null)
        {
            return;
        }
        Viewport viewport = Globals.GraphicsDevice.Viewport;
        _viewport = new Vector2(viewport.Width, viewport.Height);

        MouseState mouse = Mouse.GetState();
        KeyboardState keyboard = Keyboard.GetState();
        bool left = mouse.LeftButton == ButtonState.Pressed;
        bool middle = mouse.MiddleButton == ButtonState.Pressed;
        if (_waitForRelease)
        {
            // The click on "paint" in the menu must not paint the first tile.
            if (!left && !middle && mouse.RightButton == ButtonState.Released)
            {
                _waitForRelease = false;
            }
            _lastScroll = mouse.ScrollWheelValue;
            return;
        }

        int wheel = mouse.ScrollWheelValue - _lastScroll;
        _lastScroll = mouse.ScrollWheelValue;
        if (wheel != 0)
        {
            int steps = wheel / 120;
            if (steps == 0)
            {
                steps = Math.Sign(wheel);
            }
            Settings.Data.BrushRadius = Math.Clamp(Settings.Data.BrushRadius + steps, 0, MaxRadius);
            Settings.MarkDirty();
        }

        bool ctrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        bool undo = ctrl && keyboard.IsKeyDown(Keys.Z);
        if (undo && !_undoWasDown)
        {
            Undo();
        }
        _undoWasDown = undo;

        Point tile = MouseTile(mouse);
        if (middle && !_middleWasDown)
        {
            Pick(tile);
        }
        _middleWasDown = middle;

        if (!left)
        {
            _painting = false;
            return;
        }
        long now = Environment.TickCount64;
        if (!_painting)
        {
            _painting = true;
            _strokeCounter++;
            StampLine(tile, tile);
            _lastTile = tile;
            _lastStampTicks = now;
        }
        else if (tile != _lastTile && now - _lastStampTicks >= StampIntervalMs)
        {
            StampLine(_lastTile, tile);
            _lastTile = tile;
            _lastStampTicks = now;
        }
    }

    // Stamps along the mouse path so fast strokes leave no gaps.
    private static void StampLine(Point from, Point to)
    {
        int radius = Settings.Data.BrushRadius;
        bool circle = Settings.Data.BrushCircle;
        var tiles = new HashSet<Point>();
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        int stride = Math.Max(1, radius);
        for (int i = 0; i < steps; i += stride)
        {
            var point = new Point(from.X + (int)Math.Round(dx * (double)i / steps), from.Y + (int)Math.Round(dy * (double)i / steps));
            AddFootprint(tiles, point, radius, circle);
        }
        AddFootprint(tiles, to, radius, circle);
        Submit(tiles, CurrentBrush, _strokeCounter);
    }

    private static void AddFootprint(HashSet<Point> tiles, Point center, int radius, bool circle)
    {
        int limit = radius * radius + radius;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (!circle || x * x + y * y <= limit)
                {
                    tiles.Add(new Point(center.X + x, center.Y + y));
                }
            }
        }
    }

    public static string ApplyAroundPlayer(int radius)
    {
        if (!CanTerraform(out string reason))
        {
            return reason;
        }
        EntityWrapper player = GameAccess.LocalPlayerEntity;
        if (player == null)
        {
            return Loc.T("The character was not found.", "Персонаж не найден.");
        }
        radius = Math.Clamp(radius, 1, MaxAreaRadius);
        var tiles = new HashSet<Point>();
        AddFootprint(tiles, PlayerTile(player), radius, circle: true);
        BrushDef brush = CurrentBrush;
        Submit(tiles, brush, ++_strokeCounter);
        return Loc.T(
            $"\"{brush.Label}\" applied within {radius} tiles around the character.",
            $"«{brush.Label}»: применено в радиусе {radius} тайлов вокруг персонажа.");
    }

    private static void Submit(HashSet<Point> tiles, BrushDef brush, int stroke)
    {
        // Never wall the player in with water, pits or cliffs.
        if (BlocksMovement(brush) && GameAccess.LocalPlayerEntity is EntityWrapper player)
        {
            Point feet = PlayerTile(player);
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    tiles.Remove(new Point(feet.X + x, feet.Y + y));
                }
            }
        }
        if (tiles.Count == 0)
        {
            return;
        }
        Point[] points = tiles.ToArray();
        bool clearTerrain = Settings.Data.BrushClearsTerrain;
        ServerQueue.Run(delegate
        {
            ApplyOnServer(points, brush, stroke, clearTerrain);
        });
    }

    private static bool BlocksMovement(BrushDef brush)
    {
        return brush.Kind == BrushKind.Paint
            && brush.Structure.HasValue
            && TileDataBase.Structures[(uint)brush.Structure.Value]?.Flags.HasFlags(StructureTileFlags.BlockMovement) == true;
    }

    private static Point PlayerTile(EntityWrapper player)
    {
        return new Point((int)MathF.Floor(player.Position.X / TileSize), (int)MathF.Floor(player.Position.Y / TileSize));
    }

    private static void Pick(Point tile)
    {
        WorldTile[,] tiles = GameState.WorldTiles;
        if (tiles == null || tile.X < 0 || tile.Y < 0 || tile.X >= tiles.GetLength(0) || tile.Y >= tiles.GetLength(1))
        {
            return;
        }
        WorldTile picked = tiles[tile.X, tile.Y];
        _picked = MakePicked(picked.Ground, picked.Structure);
        Settings.Data.PickedGround = (int)picked.Ground;
        Settings.Data.PickedStructure = (int)picked.Structure;
        Settings.Data.BrushId = PickedId;
        Settings.MarkDirty();
        ShowNote(Loc.T("Eyedropper: ", "Пипетка: ") + _picked.Label);
    }

    public static void Undo()
    {
        if (!GameAccess.IsHost)
        {
            return;
        }
        ServerQueue.Run(delegate
        {
            try
            {
                if (!ReferenceEquals(_undoTiles, ServerGameState.WorldTiles) || UndoStack.Count == 0)
                {
                    return;
                }
                Stroke stroke = UndoStack[UndoStack.Count - 1];
                UndoStack.RemoveAt(UndoStack.Count - 1);
                _undoCount = UndoStack.Count;
                Apply(stroke.Changes, forward: false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("Terraform undo", e);
            }
        });
        ShowNote(Loc.T("The last change was undone", "Последнее изменение отменено"));
    }

    private static void ShowNote(string text)
    {
        _note = text;
        _noteUntil = Environment.TickCount64 + 2500;
    }

    private static Point MouseTile(MouseState mouse)
    {
        Vector2 world = ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        return new Point((int)MathF.Floor(world.X / TileSize), (int)MathF.Floor(world.Y / TileSize));
    }

    // Same mapping as the game's NativeInput.CalculateMouseWorldPosition.
    private static Vector2 ScreenToWorld(Vector2 screen)
    {
        var camera = Globals.Game.Camera;
        return new Vector2(
            screen.X / camera.XScale + camera.CurrentX - _viewport.X / (2f * camera.XScale),
            screen.Y / camera.YScale + camera.CurrentY - _viewport.Y / (2f * camera.YScale));
    }

    private static Vec2 WorldToScreen(float x, float y)
    {
        var camera = Globals.Game.Camera;
        return new Vec2((x - camera.CurrentX) * camera.XScale + _viewport.X / 2f, (y - camera.CurrentY) * camera.YScale + _viewport.Y / 2f);
    }

    // ------------------------------------------------------------------ applying (server thread)

    private static void ApplyOnServer(Point[] points, BrushDef brush, int stroke, bool clearTerrain)
    {
        try
        {
            WorldTile[,] tiles = ServerGameState.WorldTiles;
            if (tiles == null || ServerRunState.ServerChunkedWorld == null)
            {
                return;
            }
            if (!ReferenceEquals(_undoTiles, tiles))
            {
                UndoStack.Clear();
                _undoTiles = tiles;
            }
            int width = tiles.GetLength(0);
            int height = tiles.GetLength(1);
            var changes = new List<TileChange>();
            foreach (Point point in points)
            {
                if (point.X <= 0 || point.Y <= 0 || point.X >= width - 1 || point.Y >= height - 1)
                {
                    continue;
                }
                WorldTile old = tiles[point.X, point.Y];
                if (IsProtected(old.Structure))
                {
                    continue;
                }
                WorldTile next = Compute(brush, old, clearTerrain);
                if (next != old)
                {
                    changes.Add(new TileChange(point.X, point.Y, old, next));
                }
            }
            if (changes.Count == 0)
            {
                return;
            }
            RecordUndo(stroke, changes);
            Apply(changes, forward: true);
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Terraform apply", e);
        }
    }

    private static bool IsProtected(S structure)
    {
        return Protected.Contains(structure) || TileDataBase.Structures[(uint)structure]?.Flags.HasFlags(StructureTileFlags.BlockReplacing) == true;
    }

    private static WorldTile Compute(BrushDef brush, WorldTile old, bool clearTerrain)
    {
        if (brush.Kind == BrushKind.Remove)
        {
            return brush.Targets.Contains(old.Structure) ? new WorldTile(old.Ground, S.None) : old;
        }
        G ground = brush.Ground ?? old.Ground;
        S structure = brush.Structure ?? (clearTerrain && NaturalTerrain.Contains(old.Structure) ? S.None : old.Structure);
        return new WorldTile(ground, structure);
    }

    private static void RecordUndo(int strokeId, List<TileChange> changes)
    {
        Stroke stroke = UndoStack.Count > 0 ? UndoStack[UndoStack.Count - 1] : null;
        if (stroke == null || stroke.Id != strokeId)
        {
            stroke = new Stroke(strokeId);
            UndoStack.Add(stroke);
        }
        stroke.Changes.AddRange(changes);
        int total = UndoStack.Sum(s => s.Changes.Count);
        while (UndoStack.Count > 1 && (UndoStack.Count > MaxUndoStrokes || total > MaxUndoTiles))
        {
            total -= UndoStack[0].Changes.Count;
            UndoStack.RemoveAt(0);
        }
        _undoCount = UndoStack.Count;
    }

    // forward: Old -> New; otherwise New -> Old in reverse order. Tiles the game changed since are left alone.
    private static void Apply(List<TileChange> changes, bool forward)
    {
        WorldTile[,] tiles = ServerGameState.WorldTiles;
        sbyte[,] heights = ServerRunState.TileHeights;
        var applied = new List<(int X, int Y, WorldTile Tile)>(changes.Count);
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        bool remesh = false;
        for (int n = 0; n < changes.Count; n++)
        {
            TileChange change = changes[forward ? n : changes.Count - 1 - n];
            WorldTile from = forward ? change.Old : change.New;
            WorldTile to = forward ? change.New : change.Old;
            if (tiles[change.X, change.Y] != from)
            {
                continue;
            }
            tiles[change.X, change.Y] = to;
            sbyte height = TileDataBase.Structures[(uint)to.Structure].Height;
            if (heights != null && change.X < heights.GetLength(0) && change.Y < heights.GetLength(1))
            {
                heights[change.X, change.Y] = height;
            }
            remesh |= from.Structure != to.Structure;
            applied.Add((change.X, change.Y, to));
            minX = Math.Min(minX, change.X);
            minY = Math.Min(minY, change.Y);
            maxX = Math.Max(maxX, change.X);
            maxY = Math.Max(maxY, change.Y);
        }
        if (applied.Count == 0)
        {
            return;
        }
        var bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        RebuildCollision(bounds);
        ClientQueue.Enqueue(() => ApplyOnClient(applied, bounds, remesh, tiles));
        Log.Info($"Terraform: {(forward ? "changed" : "restored")} {applied.Count} tile(s) in {bounds}");
    }

    // The server builds terrain collision per chunk only when the chunk loads; redo it for the touched chunks.
    private static void RebuildCollision(Rectangle bounds)
    {
        ServerChunkedWorld world = ServerRunState.ServerChunkedWorld;
        if (world == null)
        {
            return;
        }
        Point size = world.ChunkSize;
        Point count = world.ChunkCount;
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }
        int x0 = Math.Max(0, (bounds.Left - 1) / size.X);
        int y0 = Math.Max(0, (bounds.Top - 1) / size.Y);
        int x1 = Math.Min(count.X - 1, bounds.Right / size.X);
        int y1 = Math.Min(count.Y - 1, bounds.Bottom / size.Y);
        for (int cy = y0; cy <= y1; cy++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                ref ServerWorldChunk chunk = ref world.GetWorldChunk(cx, cy);
                if (!chunk.Active || chunk.CollisionTerrainGroup == null)
                {
                    continue;
                }
                chunk.CollisionTerrainGroup.Clear();
                ChunkServerManager.ChunkLoaded(world, new Point(cx, cy));
            }
        }
    }

    // Game thread: the host's own copy of the map, then the ground render and terrain meshes/collisions.
    private static void ApplyOnClient(List<(int X, int Y, WorldTile Tile)> applied, Rectangle bounds, bool remesh, WorldTile[,] serverTiles)
    {
        if (!ReferenceEquals(serverTiles, ServerGameState.WorldTiles))
        {
            return;
        }
        WorldTile[,] tiles = GameState.WorldTiles;
        sbyte[,] heights = GameState.TileHeights;
        if (tiles == null || heights == null)
        {
            return;
        }
        int width = Math.Min(tiles.GetLength(0), heights.GetLength(0));
        int height = Math.Min(tiles.GetLength(1), heights.GetLength(1));
        foreach ((int x, int y, WorldTile tile) in applied)
        {
            if (x >= width || y >= height)
            {
                continue;
            }
            tiles[x, y] = tile;
            heights[x, y] = TileDataBase.Structures[(uint)tile.Structure].Height;
        }
        ExteriorWorldHandler.UpdateChunkRenders(bounds, remesh);
    }

    // ------------------------------------------------------------------ overlay (ImGui)

    public static void DrawOverlay()
    {
        if (!BrushActive || Globals.Game?.Camera == null || _viewport.X <= 0f)
        {
            return;
        }
        float s = UiFonts.Scale;
        Point center = MouseTile(Mouse.GetState());
        int radius = Settings.Data.BrushRadius;
        bool circle = Settings.Data.BrushCircle;
        BrushDef brush = CurrentBrush;

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Vec4 color = BrushColor(brush);
        uint fill = ImGui.GetColorU32(new Vec4(color.X, color.Y, color.Z, 0.38f));
        uint edge = ImGui.GetColorU32(new Vec4(1f, 1f, 1f, 0.9f));
        int limit = radius * radius + radius;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (circle && x * x + y * y > limit)
                {
                    continue;
                }
                float left = (center.X + x) * TileSize;
                float top = (center.Y + y) * TileSize;
                draw.AddRectFilled(WorldToScreen(left, top), WorldToScreen(left + TileSize, top + TileSize), fill);
            }
        }
        draw.AddRect(WorldToScreen((center.X - radius) * TileSize, (center.Y - radius) * TileSize),
            WorldToScreen((center.X + radius + 1) * TileSize, (center.Y + radius + 1) * TileSize), edge, 0f, ImDrawFlags.None, Math.Max(1f, 1.5f * s));
        draw.AddRect(WorldToScreen(center.X * TileSize, center.Y * TileSize), WorldToScreen((center.X + 1) * TileSize, (center.Y + 1) * TileSize), edge, 0f, ImDrawFlags.None, 1f);

        DrawHud(center, brush, radius, circle, s);
    }

    private static void DrawHud(Point center, BrushDef brush, int radius, bool circle, float s)
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
        bool visible = ImGui.Begin("##terraform_hud", ref open, flags);
        try
        {
            if (visible)
            {
                string size = $"{radius * 2 + 1}×{radius * 2 + 1}{(circle ? Loc.T(", round", ", круг") : string.Empty)}";
                Ui.Colored(Ui.Accent, Loc.T(
                    $"Terraforming: {brush.Label}    Brush {size}    Undo steps: {UndoCount}",
                    $"Террафоминг: {brush.Label}    Кисть {size}    Отмен в запасе: {UndoCount}"));
                Ui.Text(Loc.T(
                    "LMB: paint   ·   Wheel: size   ·   MMB: eyedropper   ·   Ctrl+Z: undo   ·   RMB / Esc / Ctrl+0: back to the menu",
                    "ЛКМ: рисовать   ·   Колесо: размер   ·   СКМ: пипетка   ·   Ctrl+Z: отменить   ·   ПКМ / Esc / Ctrl+0: в меню"));
                WorldTile[,] tiles = GameState.WorldTiles;
                if (tiles != null && center.X >= 0 && center.Y >= 0 && center.X < tiles.GetLength(0) && center.Y < tiles.GetLength(1))
                {
                    WorldTile tile = tiles[center.X, center.Y];
                    Ui.Disabled(Loc.T(
                        $"Under the cursor ({center.X}, {center.Y}):  Ground = {tile.Ground},  Structure = {tile.Structure}",
                        $"Под курсором ({center.X}, {center.Y}):  Ground = {tile.Ground},  Structure = {tile.Structure}"));
                }
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
}
