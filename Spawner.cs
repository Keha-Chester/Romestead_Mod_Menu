using System;
using System.Collections.Generic;
using Candide;
using Candide.GameModels;
using CandideCreator.Shared.Helpers;
using CandideServer;
using CandideServer.Entities;
using CandideServer.ServerControllers;
using CandideServer.ServerManagers;
using CandideServer.ServerSystems;
using CandideServer.World;
using Microsoft.Xna.Framework;
using Shared;
using Shared.Data;
using Shared.Entity;
using Shared.Helpers;
using Shared.Models;
using Shared.Models.Construction;
using ServerWorldModel = CandideServer.Models.Worlds.WorldModel;

namespace RomesteadCheatMenu;

// Everything that changes world state is queued onto the local server thread,
// the same way the game's own citizens and loot do it.
internal static class Spawner
{
    private const int MaxItemDropsPerClick = 250;
    private const int MaxEntitiesPerClick = 100;
    private const int MaxCitizensPerClick = 25;

    // Creatures and citizens appear 10 tiles (16 px each) in front of the player.
    public const float CreatureDistance = 10 * 16f;

    private const float CreatureSpacing = 18f;

    // Liquid/bulk resources only exist inside buckets (barrels).
    private static readonly HashSet<string> BucketResources = new HashSet<string>
    {
        "resource:clay",
        "resource:concrete",
        "resource:ash",
        "resource:water"
    };

    private static string NoWorldMessage => Loc.T("Load a world first.", "Сначала загрузите мир.");

    private static string NotHostMessage => Loc.T("Spawning only works in your own world (you need to be the host).",
        "Спавн работает только в вашем собственном мире (нужно быть хостом).");

    public static bool SpawnsInBucket(SpawnEntry entry)
    {
        return entry.Kind == EntryKind.Resource && BucketResources.Contains(entry.Id);
    }

    public static string Spawn(SpawnEntry entry, int amount, out bool error)
    {
        error = true;
        if (!GameAccess.InWorld)
        {
            return NoWorldMessage;
        }
        if (!GameAccess.IsHost)
        {
            return NotHostMessage;
        }
        EntityWrapper player = Globals.Game.Player;
        Vector3 origin = player.Position;
        Vector2 forward = GetForward(player);
        Guid worldId = GameState.CurrentWorld.Id;
        error = false;
        amount = Math.Max(1, amount);
        return entry.Kind == EntryKind.Item
            ? SpawnItem(entry, amount, origin, forward, worldId)
            : SpawnResource(entry, amount, origin, forward, worldId, out error);
    }

    private static Vector2 GetForward(EntityWrapper player)
    {
        Vector2 forward = player.Direction.DirectionToVector2();
        if (forward.LengthSquared() < 0.0001f)
        {
            forward = new Vector2(0f, 1f);
        }
        forward.Normalize();
        return forward;
    }

    private static string SpawnItem(SpawnEntry entry, int amount, Vector3 origin, Vector2 forward, Guid worldId)
    {
        int stackSize = entry.Unique ? 1 : Math.Max(1, entry.MaxStack);
        string note = string.Empty;
        if ((amount + (long)stackSize - 1) / stackSize > MaxItemDropsPerClick)
        {
            amount = MaxItemDropsPerClick * stackSize;
            note = Loc.T($" (at most {MaxItemDropsPerClick} stacks at once)", $" (не больше {MaxItemDropsPerClick} стопок за раз)");
        }

        string id = entry.Id;
        int total = amount;
        ServerQueue.Run(delegate
        {
            try
            {
                var random = new Random();
                int remaining = total;
                while (remaining > 0)
                {
                    int count = Math.Min(stackSize, remaining);
                    // Same "toss in front of the player" as dropping an item from the inventory.
                    Vector2 direction = Rotate(forward, ((float)random.NextDouble() - 0.5f) * 0.8f);
                    float speed = 100f + (float)random.NextDouble() * 50f;
                    Vector3 start = origin + new Vector3(forward * 4f, 10f);
                    Vector3 velocity = new Vector3(direction * speed, 0f);
                    if (WorldItemServerManager.SpawnNewItemAsWorldItem(id, count, start, velocity, worldId) == null)
                    {
                        Log.Warn($"World item spawn failed: {id} x{count}");
                        break;
                    }
                    remaining -= count;
                }
            }
            catch (Exception e)
            {
                Log.Error("Item spawn failed: " + id, e);
            }
        });
        return Loc.T($"Spawned: {entry.NameEn} ×{amount}{note}", $"Заспавнено: {entry.NameEn} ×{amount}{note}");
    }

    private static string SpawnResource(SpawnEntry entry, int amount, Vector3 origin, Vector2 forward, Guid worldId, out bool error)
    {
        error = true;
        ConstructionResourceDataModel? data = ConstructionResourcesDataBase.GetConstructionResourceOrNull(entry.Id);
        if (!data.HasValue)
        {
            return Loc.T("Resource not found: ", "Ресурс не найден: ") + entry.Id;
        }
        bool fillBucket = BucketResources.Contains(entry.Id);
        bool bucket = fillBucket || entry.Id == "resource:bucket";
        Guid baseId = bucket ? EntityIds.BucketEntityGuid : data.Value.DefaultBaseGuid ?? Guid.Empty;
        if (baseId == Guid.Empty)
        {
            return Loc.T("This resource has no object in the world: ", "У этого ресурса нет объекта в мире: ") + entry.NameEn;
        }
        string note = string.Empty;
        if (amount > MaxEntitiesPerClick)
        {
            amount = MaxEntitiesPerClick;
            note = Loc.T($" (at most {MaxEntitiesPerClick} at once)", $" (не больше {MaxEntitiesPerClick} за раз)");
        }

        string id = entry.Id;
        int count = amount;
        ServerQueue.Run(delegate
        {
            try
            {
                // Carryable objects are laid out in rows in front of the player.
                Vector2 right = new Vector2(-forward.Y, forward.X);
                int columns = Math.Min(5, count);
                for (int i = 0; i < count; i++)
                {
                    int row = i / columns;
                    int column = i % columns;
                    int inRow = Math.Min(columns, count - row * columns);
                    Vector2 offset = forward * (28f + row * 16f) + right * ((column - (inRow - 1) / 2f) * 16f);
                    ServerEntityModel model = EntityServerManager.SpawnEntity(new SpawnEntityArgs
                    {
                        Id = Guid.NewGuid(),
                        BaseId = baseId,
                        WorldId = worldId,
                        Position = origin + new Vector3(offset, 0f)
                    });
                    if (model == null)
                    {
                        Log.Warn($"Entity spawn failed for {id} ({baseId})");
                        break;
                    }
                    if (fillBucket && !EntityServerManager.SetBucketContent(model, BucketEntityHelperShared.BucketContentType.Resource, id, 1))
                    {
                        Log.Warn("Could not fill bucket with " + id);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Resource spawn failed: " + id, e);
            }
        });
        error = false;
        return Loc.T(
            $"Spawned: {entry.NameEn} ×{amount}{(fillBucket ? " in barrels" : string.Empty)}{note}",
            $"Заспавнено: {entry.NameEn} ×{amount}{(fillBucket ? " в бочках" : string.Empty)}{note}");
    }

    // ------------------------------------------------------------------ creatures and citizens

    public static string SpawnCreature(SpawnEntry entry, int amount, bool persistent, out bool error)
    {
        error = true;
        if (!GameAccess.InWorld)
        {
            return NoWorldMessage;
        }
        if (!GameAccess.IsHost)
        {
            return NotHostMessage;
        }
        EntityWrapper player = Globals.Game.Player;
        Vector2 origin = new Vector2(player.Position.X, player.Position.Y);
        Vector2 forward = GetForward(player);
        Guid worldId = GameState.CurrentWorld.Id;
        Guid ownerId = player.Id;

        bool citizen = entry.Kind == EntryKind.Citizen;
        int limit = citizen ? MaxCitizensPerClick : MaxEntitiesPerClick;
        amount = Math.Max(1, amount);
        string note = string.Empty;
        if (amount > limit)
        {
            amount = limit;
            note = Loc.T($" (at most {limit} at once)", $" (не больше {limit} за раз)");
        }

        Guid baseId = entry.EntityId;
        EntityType type = entry.EntityType;
        // Wild citizens always stay; creatures behave like the game's own spawns unless asked otherwise.
        string tier = citizen ? entry.CitizenTier ?? string.Empty : null;
        bool temporary = !citizen && !persistent;
        string name = entry.NameEn;
        int count = amount;
        Vector2 center = origin + forward * CreatureDistance;
        ServerQueue.Run(delegate
        {
            try
            {
                int spawned = SpawnEntities(baseId, type, tier, temporary, count, center, origin, worldId, ownerId);
                if (spawned < count)
                {
                    Log.Warn($"{name}: spawned {spawned} of {count} (no free space there)");
                }
            }
            catch (Exception e)
            {
                Log.Error("Creature spawn failed: " + name, e);
            }
        });
        error = false;
        double tiles = Math.Round(CreatureDistance / 16f);
        return Loc.T(
            $"Spawned: {entry.NameEn} ×{amount}, {tiles} tiles in front of the character{note}",
            $"Заспавнено: {entry.NameEn} ×{amount}, в {tiles} тайлах перед персонажем{note}");
    }

    private static int SpawnEntities(Guid baseId, EntityType type, string tier, bool temporary, int count, Vector2 center, Vector2 origin, Guid worldId, Guid ownerId)
    {
        if (!ServerEntityDataManager.TryGetEntityBaseData(baseId, out EntityWrapper data))
        {
            Log.Warn("No server data for entity " + baseId);
            return 0;
        }
        ServerGameState.Worlds.TryGetValue(worldId, out ServerWorldModel world);
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            if (!TryFindFreeSpot(world, data, center + Spiral(i), origin, out Vector2 position))
            {
                continue;
            }
            var parameters = new Dictionary<string, string>();
            if (tier == null)
            {
                parameters["dsog"] = ownerId.ToString();
                if (temporary)
                {
                    parameters["temporary_spawn"] = "true";
                }
            }
            else if (tier.Length > 0)
            {
                parameters["tier_setting"] = "ManualTier";
                parameters["tier"] = tier;
            }
            ServerEntityModel model = EntityServerManager.SpawnEntity(new SpawnEntityArgs
            {
                Id = Guid.NewGuid(),
                BaseId = baseId,
                WorldId = worldId,
                Position = new Vector3(position, 0f),
                EntityType = type,
                DynamicSpawn = temporary,
                Parameters = parameters
            });
            if (model == null)
            {
                Log.Warn($"Entity spawn failed for {baseId}");
                break;
            }
            spawned++;
        }
        return spawned;
    }

    // Sunflower layout: a loose, evenly filled circle around the spawn point.
    private static Vector2 Spiral(int index)
    {
        if (index == 0)
        {
            return Vector2.Zero;
        }
        float radius = CreatureSpacing * MathF.Sqrt(index);
        float angle = index * 2.3999632f;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    // Rocks, water, walls and other creatures push the spawn point aside, or back towards the player.
    private static bool TryFindFreeSpot(ServerWorldModel world, EntityWrapper data, Vector2 desired, Vector2 origin, out Vector2 position)
    {
        if (IsFree(world, data, desired))
        {
            position = desired;
            return true;
        }
        for (int ring = 1; ring <= 6; ring++)
        {
            float radius = ring * 12f;
            int steps = 8 + ring * 4;
            for (int step = 0; step < steps; step++)
            {
                float angle = step * MathF.Tau / steps;
                Vector2 candidate = desired + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                if (IsFree(world, data, candidate))
                {
                    position = candidate;
                    return true;
                }
            }
        }
        Vector2 toPlayer = origin - desired;
        float length = toPlayer.Length();
        if (length > 1f)
        {
            toPlayer /= length;
            for (float distance = 16f; distance < length - 16f; distance += 16f)
            {
                Vector2 candidate = desired + toPlayer * distance;
                if (IsFree(world, data, candidate))
                {
                    position = candidate;
                    return true;
                }
            }
        }
        position = default;
        return false;
    }

    private static bool IsFree(ServerWorldModel world, EntityWrapper data, Vector2 position)
    {
        if (world == null)
        {
            return true;
        }
        if (world.Type == ServerWorldModel.WorldType.Chunked && ServerRunState.ServerChunkedWorld != null)
        {
            WorldTile[,] tiles = ServerGameState.WorldTiles;
            Point tile = ServerRunState.ServerChunkedWorld.WorldToTilePosition(position);
            if (tile.X <= 0 || tile.Y <= 0 || tile.X >= tiles.GetLength(0) - 1 || tile.Y >= tiles.GetLength(1) - 1)
            {
                return false;
            }
            if (TileDataBase.Structures[(uint)tiles[tile.X, tile.Y].Structure].Flags.HasFlags(StructureTileFlags.BlockMovement))
            {
                return false;
            }
        }
        var shape = data.Shape;
        if (shape == null)
        {
            return true;
        }
        if (!data.NoTerrainCollision && ServerWorldHandler.CheckForStaticCollision(world, position, 0f, shape, data.CollisionGroupMask))
        {
            return false;
        }
        if (!data.NoEntityCollision
            && ServerEntitySystemManager.WorldIdToSystemMap.TryGetValue(world.Id, out var pair)
            && pair.System.CollisionGroup.CheckForCollisionAtPosition(position, 0f, shape, data.CollisionGroupMask, null, null))
        {
            return false;
        }
        return true;
    }

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos);
    }
}
