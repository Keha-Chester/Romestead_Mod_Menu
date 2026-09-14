using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Candide;
using Candide.GameModels;
using Candide.GameModels.Managers;
using Candide.GameModels.Services;
using CandideServer;
using CandideServer.Entities;
using CandideServer.MessageModels.Favour;
using CandideServer.ServerServices;
using CandideServer.ServerSystems;
using CandideServer.SyncStrategies;
using HarmonyLib;
using Shared.Data;
using Shared.Entity;
using Shared.Entity.Components;
using Shared.Models.Player;
using Shared.Models.Skill;
using Shared.Models.Stats;
using Shared.Text;

namespace RomesteadCheatMenu;

internal sealed class StatDef
{
    public readonly string Id;
    public readonly string LabelEn;
    public readonly string LabelRu;
    public readonly bool Pool;

    public StatDef(string id, string labelEn = null, string labelRu = null, bool pool = false)
    {
        Id = id;
        LabelEn = labelEn;
        LabelRu = labelRu;
        Pool = pool;
    }

    // Our own label in the menu language, or null to show the game's stat name.
    public string Label => Loc.IsRussian ? LabelRu : LabelEn;

    public string EnglishName => LocaleFile.English.Get(Id + "*stats:name") ?? Id;

    public string LocalName
    {
        get
        {
            try
            {
                return new StringId(Id + "*stats:name").GetTranslation();
            }
            catch
            {
                return EnglishName;
            }
        }
    }
}

internal sealed class StatState
{
    public float Bonus;
    public bool Frozen;
    public float FrozenValue;
}

internal static class PlayerCheats
{
    private struct StatSnapshot
    {
        public string Id;
        public Guid ModifierId;
        public float Bonus;
        public bool Frozen;
        public float FrozenValue;
        public bool Pool;
    }

    public static readonly StatDef[] MainStats =
    {
        new StatDef("Health", "Max health", "Объём здоровья", pool: true),
        new StatDef("Energy", "Stamina / mana (energy)", "Выносливость / мана (энергия)", pool: true),
        new StatDef("EnergyRegeneration", "Energy regeneration", "Восстановление энергии"),
        new StatDef("MeleeDamage", "Strength (melee damage)", "Сила (ближний бой)")
    };

    public static readonly StatDef[] OtherStats =
    {
        new StatDef("RangedDamage", null),
        new StatDef("MagicDamage", null),
        new StatDef("ThrowingDamage", null),
        new StatDef("Armor", null),
        new StatDef("MagicResistance", null),
        new StatDef("MovementSpeed", null),
        new StatDef("AttackSpeed", null),
        new StatDef("CritChance", null),
        new StatDef("CritDamage", null),
        new StatDef("KnockbackResistance", null),
        new StatDef("AxePower", null),
        new StatDef("PickaxePower", null)
    };

    private static readonly StatDef[] AllStats = MainStats.Concat(OtherStats).ToArray();

    private static readonly Dictionary<string, Guid> ModifierIds = AllStats.ToDictionary(
        d => d.Id,
        d => new Guid(MD5.HashData(Encoding.UTF8.GetBytes("RomesteadCheatMenu/" + d.Id))));

    private static readonly Dictionary<string, bool> PercentageStats = new Dictionary<string, bool>();

    private static readonly MethodInfo LevelUpSkillMethod = AccessTools.Method(typeof(SkillsManager), "LevelUpSkill");

    // Main-thread state; other threads only read the immutable snapshot below.
    private static readonly Dictionary<string, StatState> States = new Dictionary<string, StatState>();
    private static volatile StatSnapshot[] _snapshot = Array.Empty<StatSnapshot>();
    private static int _version;
    private static int _clientAppliedVersion = -1;
    private static int _serverAppliedVersion = -1;
    private static volatile bool _anyModifier;
    private static string _characterName;

    public static volatile bool GodMode;
    public static volatile bool InfiniteEnergy;

    // ---------------------------------------------------------------- skills

    public static string SkillName(CharacterSkill skill)
    {
        return LocaleFile.English.Get(skill.SkillId + SkillsDataBase.SuffixName) ?? skill.Name;
    }

    // Uses the game's own level-up routine: it updates the character, grants favour points
    // for the experience, prints the chat message and tells the server.
    public static string AddSkillLevels(CharacterSkill skill, int levels)
    {
        PlayerCharacterModel character = GameAccess.LocalCharacter;
        if (character == null || skill == null)
        {
            return Loc.T("The character is not loaded.", "Персонаж не загружен.");
        }
        int add = Math.Min(levels, 100 - skill.Level);
        if (add <= 0)
        {
            return SkillName(skill) + Loc.T(": already at the maximum level.", ": уже максимальный уровень.");
        }
        if (LevelUpSkillMethod != null)
        {
            LevelUpSkillMethod.Invoke(null, new object[] { character, skill, add });
        }
        else
        {
            float experience = -skill.CurrentExperience + 1f;
            for (int i = 0; i < add; i++)
            {
                experience += skill.ExperienceRequiredToLevelUpAtLevel(skill.Level + i);
            }
            SkillsManager.AddExperienceToSkillType(character, skill.SkillId, experience / Math.Max(0.0001f, skill.ExperienceGainFactor));
        }
        // The level-up is sent to the server as a message; let a paused server take it now.
        ServerQueue.Nudge();
        return Loc.T($"{SkillName(skill)}: level {skill.Level}", $"{SkillName(skill)}: уровень {skill.Level}");
    }

    // ---------------------------------------------------------------- favours

    public static string ChangeFavourPoints(int delta)
    {
        if (!GameAccess.IsHost)
        {
            return Loc.T("Favour points can only be changed in your own world (you need to be the host).",
                "Очки преимуществ можно менять только в своём мире (нужно быть хостом).");
        }
        ServerQueue.Run(delegate
        {
            try
            {
                PlayerCharacterModel character = ServerGlobals.CurrentPlayerCharacter?.Character;
                if (character == null)
                {
                    return;
                }
                character.Skills.CurrentFavourPoints = Math.Max(0, character.Skills.CurrentFavourPoints + delta);
                FavourServerService.Send_UpdateFavours(new UpdateFavourMessage
                {
                    CharacterId = character.EntityId,
                    UpdatedFavours = Array.Empty<(string, int)>(),
                    NewCurrentFavourPoints = character.Skills.CurrentFavourPoints
                }, SyncStrategy.Everyone());
            }
            catch (Exception e)
            {
                Log.Error("Favour point change failed", e);
            }
        });
        return delta >= 0
            ? Loc.T($"Favour points added: {delta}", $"Добавлено очков преимуществ: {delta}")
            : Loc.T($"Favour points removed: {-delta}", $"Убрано очков преимуществ: {-delta}");
    }

    public static string UnlearnFavours()
    {
        FavourService.Send_UnlearnAllFavours();
        ServerQueue.Nudge();
        return Loc.T("Learned favours were reset, the points are returned.", "Изученные преимущества сброшены, очки возвращены.");
    }

    // ---------------------------------------------------------------- stats (main thread)

    public static StatState GetState(string id)
    {
        if (!States.TryGetValue(id, out StatState state))
        {
            state = new StatState();
            States[id] = state;
        }
        return state;
    }

    public static void SetBonus(StatDef def, float bonus)
    {
        StatState state = GetState(def.Id);
        if (MathF.Abs(state.Bonus - bonus) < 0.00001f)
        {
            return;
        }
        if (state.Frozen && !def.Pool)
        {
            state.FrozenValue += bonus - state.Bonus;
        }
        state.Bonus = bonus;
        Commit();
    }

    public static void SetFrozen(StatDef def, bool frozen)
    {
        StatState state = GetState(def.Id);
        state.Frozen = frozen;
        if (frozen && !def.Pool)
        {
            EntityWrapper player = GameAccess.LocalPlayerEntity;
            state.FrozenValue = player != null && player.Mask.HasFlags(Component.Stats) ? player.Stats.Get(def.Id) : 0f;
        }
        Commit();
    }

    public static void ResetAll()
    {
        States.Clear();
        Commit();
    }

    public static bool IsPercentage(StatDef def)
    {
        if (!PercentageStats.TryGetValue(def.Id, out bool percentage))
        {
            try
            {
                percentage = EntityStatsDataBase.GetStatOrMissing(def.Id).IsPercentage;
            }
            catch
            {
                percentage = false;
            }
            PercentageStats[def.Id] = percentage;
        }
        return percentage;
    }

    public static string FormatValue(StatDef def, float value)
    {
        return IsPercentage(def)
            ? (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string FormatCurrent(StatDef def, EntityWrapper player)
    {
        if (player == null || !player.Mask.HasFlags(Component.Stats))
        {
            return "-";
        }
        if (def.Id == "Health" && player.Mask.HasFlags(Component.Health))
        {
            return $"{player.Health:0} / {player.MaxHealth:0}";
        }
        if (def.Id == "Energy")
        {
            return $"{player.Energy:0} / {player.Stats.Get("Energy"):0}";
        }
        return FormatValue(def, player.Stats.Get(def.Id));
    }

    public static float StepFor(StatDef def, EntityWrapper player)
    {
        if (def.Pool)
        {
            return 10f;
        }
        if (IsPercentage(def))
        {
            return 0.05f;
        }
        float current = player != null && player.Mask.HasFlags(Component.Stats) ? MathF.Abs(player.Stats.Get(def.Id)) : 0f;
        return current >= 50f ? 5f : 1f;
    }

    private static void Commit()
    {
        RebuildSnapshot();
        if (_characterName == null)
        {
            return;
        }
        var saved = new CharacterSettings();
        foreach (KeyValuePair<string, StatState> pair in States)
        {
            if (pair.Value.Bonus != 0f || pair.Value.Frozen)
            {
                saved.Stats[pair.Key] = new StatSetting { Bonus = pair.Value.Bonus, Frozen = pair.Value.Frozen, FrozenValue = pair.Value.FrozenValue };
            }
        }
        if (saved.Stats.Count == 0)
        {
            Settings.Data.Characters.Remove(_characterName);
        }
        else
        {
            Settings.Data.Characters[_characterName] = saved;
        }
        Settings.MarkDirty();
    }

    private static void RebuildSnapshot()
    {
        var snapshot = new StatSnapshot[AllStats.Length];
        bool anyModifier = false;
        bool godMode = false;
        bool infiniteEnergy = false;
        for (int i = 0; i < AllStats.Length; i++)
        {
            StatDef def = AllStats[i];
            States.TryGetValue(def.Id, out StatState state);
            float bonus = state?.Bonus ?? 0f;
            bool frozen = state?.Frozen ?? false;
            snapshot[i] = new StatSnapshot
            {
                Id = def.Id,
                ModifierId = ModifierIds[def.Id],
                Bonus = bonus,
                Frozen = frozen,
                FrozenValue = state?.FrozenValue ?? 0f,
                Pool = def.Pool
            };
            anyModifier |= bonus != 0f || (frozen && !def.Pool);
            godMode |= frozen && def.Id == "Health";
            infiniteEnergy |= frozen && def.Id == "Energy";
        }
        _snapshot = snapshot;
        _anyModifier = anyModifier;
        GodMode = godMode;
        InfiniteEnergy = infiniteEnergy;
        Interlocked.Increment(ref _version);
    }

    private static void SyncCharacter(string name)
    {
        if (name == _characterName)
        {
            return;
        }
        _characterName = name;
        States.Clear();
        if (name != null && Settings.Data.Characters.TryGetValue(name, out CharacterSettings saved) && saved?.Stats != null)
        {
            foreach (KeyValuePair<string, StatSetting> pair in saved.Stats)
            {
                States[pair.Key] = new StatState { Bonus = pair.Value.Bonus, Frozen = pair.Value.Frozen, FrozenValue = pair.Value.FrozenValue };
            }
        }
        RebuildSnapshot();
        if (name != null)
        {
            Log.Info($"Character '{name}' loaded, saved stat cheats: {States.Count}");
        }
    }

    // ---------------------------------------------------------------- per frame / per tick

    public static void ClientTick()
    {
        if (!GameAccess.InWorld)
        {
            SyncCharacter(null);
            return;
        }
        SyncCharacter(GameAccess.LocalCharacter?.Name);
        EntityWrapper player = Globals.Game.Player;
        int version = _version;
        if (_anyModifier || version != _clientAppliedVersion)
        {
            ApplyModifiers(player, _snapshot);
            _clientAppliedVersion = version;
        }
        EnforcePools(player);
    }

    // Local server thread.
    public static void ServerTick()
    {
        int version = _version;
        if (!_anyModifier && !GodMode && !InfiniteEnergy && version == _serverAppliedVersion)
        {
            return;
        }
        PlayerCharacterModel character = ServerGlobals.CurrentPlayerCharacter?.Character;
        if (character == null || !ServerGameState.Entities.TryGetValue(character.EntityId, out ServerEntityModel model))
        {
            return;
        }
        EntityWrapper entity = model.EntityWrapper;
        if (entity == null || entity.Removed)
        {
            return;
        }
        ApplyModifiers(entity, _snapshot);
        _serverAppliedVersion = version;
        EnforcePools(entity);
    }

    // The game rebuilds stat modifiers from gear and auras here, which wipes ours.
    public static void OnCalculateState(EntityWrapper entity)
    {
        if (!_anyModifier || !IsHostPlayer(entity))
        {
            return;
        }
        ApplyModifiers(entity, _snapshot);
    }

    public static bool IsHostPlayer(EntityWrapper entity)
    {
        if (entity == null || entity.Removed)
        {
            return false;
        }
        Guid id = entity.Id;
        if (id == Guid.Empty)
        {
            return false;
        }
        if (GameState.LocalPlayer.Character != null && id == GameState.LocalPlayer.EntityId)
        {
            return true;
        }
        PlayerCharacterModel character = ServerGlobals.CurrentPlayerCharacter?.Character;
        return character != null && id == character.EntityId;
    }

    private static void ApplyModifiers(EntityWrapper entity, StatSnapshot[] snapshot)
    {
        if (entity == null || entity.Removed || !entity.Mask.HasFlags(Component.Stats))
        {
            return;
        }
        Dictionary<string, Stat> stats = entity.Stats.Stats;
        if (stats == null)
        {
            return;
        }
        bool healthModifierChanged = false;
        foreach (StatSnapshot item in snapshot)
        {
            stats.TryGetValue(item.Id, out Stat stat);
            float wanted = item.Bonus;
            if (item.Frozen && !item.Pool)
            {
                if (stat == null)
                {
                    stat = new Stat(item.Id);
                    stats[item.Id] = stat;
                }
                wanted = Compensation(stat, item.ModifierId, item.FrozenValue);
                if (MathF.Abs(wanted) < 0.0001f)
                {
                    wanted = 0f;
                }
            }
            if (stat == null)
            {
                if (wanted == 0f)
                {
                    continue;
                }
                stat = new Stat(item.Id);
                stats[item.Id] = stat;
            }
            bool present = stat.Modifiers.TryGetValue(item.ModifierId, out StatModifier current);
            if (wanted == 0f)
            {
                if (present)
                {
                    stat.RemoveStatModifier(item.ModifierId);
                    healthModifierChanged |= item.Id == "Health";
                }
            }
            else if (!present || MathF.Abs(current.ModificationData.Additive - wanted) > 0.0001f)
            {
                stat.SetStatModifier(item.ModifierId, new StatModifier
                {
                    Type = StatModifierType.Aura,
                    ModificationData = new StatModificationData { Additive = wanted }
                });
                healthModifierChanged |= item.Id == "Health";
            }
        }

        // The health component keeps its own max. Only resync it when our modifier moved the
        // Health stat, so an idle mod never touches the game's own values; keep the fill ratio.
        if (healthModifierChanged && entity.Mask.HasFlags(Component.Health))
        {
            float max = entity.Stats.Get("Health");
            float oldMax = entity.MaxHealth;
            if (max > 0f && MathF.Abs(oldMax - max) > 0.01f)
            {
                float health = entity.Health;
                entity.MaxHealth = max;
                if (health > 0f)
                {
                    entity.Health = oldMax > 0f ? Math.Clamp(health / oldMax * max, 1f, max) : max;
                }
            }
        }
    }

    // Additive value that makes the stat end up exactly at target, given every other modifier.
    // Mirrors Stat.Recalculate: (Base + (Add * AddMul + Base * BaseMul) * BonusMul) * Mul.
    private static float Compensation(Stat stat, Guid ownModifier, float target)
    {
        float additive = 0f;
        float additiveMultiplier = 1f;
        float baseMultiplier = 0f;
        float bonusMultiplier = 1f;
        float multiplier = 1f;
        foreach (KeyValuePair<Guid, StatModifier> pair in stat.Modifiers)
        {
            if (pair.Key == ownModifier)
            {
                continue;
            }
            StatModificationData data = pair.Value.ModificationData;
            additive += data.Additive;
            additiveMultiplier += data.AdditiveMultiplier;
            baseMultiplier += data.BaseMultiplier;
            bonusMultiplier += data.BonusMultiplier;
            multiplier += data.Multiplier;
        }
        if (MathF.Abs(multiplier) < 0.0001f || MathF.Abs(bonusMultiplier) < 0.0001f || MathF.Abs(additiveMultiplier) < 0.0001f)
        {
            return 0f;
        }
        float baseValue = stat.BaseValue;
        float neededAdditive = ((target / multiplier - baseValue) / bonusMultiplier - baseValue * baseMultiplier) / additiveMultiplier;
        return neededAdditive - additive;
    }

    private static void EnforcePools(EntityWrapper entity)
    {
        if (entity == null || entity.Removed)
        {
            return;
        }
        if (GodMode && entity.Mask.HasFlags(Component.Health))
        {
            float max = entity.MaxHealth;
            if (entity.Health > 0f && entity.Health < max)
            {
                entity.Health = max;
            }
        }
        if (InfiniteEnergy && entity.Mask.HasFlags(Component.Stats))
        {
            float max = entity.Stats.Get("Energy");
            if (entity.Energy < max)
            {
                entity.Energy = max;
            }
        }
    }
}
