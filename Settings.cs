using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RomesteadCheatMenu;

public sealed class SettingsData
{
    // Menu language: "en" (default) or "ru".
    public string Language { get; set; } = "en";

    public int Amount { get; set; } = 1;

    public int CreatureAmount { get; set; } = 1;

    public bool CreaturesPersistent { get; set; }

    public string BrushId { get; set; } = "remove:cliffs";

    public int BrushRadius { get; set; } = 1;

    public bool BrushCircle { get; set; }

    public bool BrushClearsTerrain { get; set; } = true;

    public int AreaRadius { get; set; } = 12;

    public int PickedGround { get; set; } = -1;

    public int PickedStructure { get; set; } = -1;

    // Keyed by character name.
    public Dictionary<string, CharacterSettings> Characters { get; set; } = new Dictionary<string, CharacterSettings>();
}

public sealed class CharacterSettings
{
    public Dictionary<string, StatSetting> Stats { get; set; } = new Dictionary<string, StatSetting>();
}

public sealed class StatSetting
{
    public float Bonus { get; set; }

    public bool Frozen { get; set; }

    public float FrozenValue { get; set; }
}

internal static class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _path;
    private static long _saveDueTicks;

    public static SettingsData Data { get; private set; } = new SettingsData();

    public static void Load(string dir)
    {
        _path = Path.Combine(dir, "CheatMenuSettings.json");
        try
        {
            if (File.Exists(_path))
            {
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_path, Encoding.UTF8), JsonOptions) ?? new SettingsData();
                Data.Characters ??= new Dictionary<string, CharacterSettings>();
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to load settings, using defaults", e);
            Data = new SettingsData();
        }
    }

    public static void MarkDirty()
    {
        _saveDueTicks = Environment.TickCount64 + 1500;
    }

    public static void SaveIfDue()
    {
        if (_saveDueTicks != 0 && Environment.TickCount64 >= _saveDueTicks)
        {
            Save();
        }
    }

    public static void Save()
    {
        _saveDueTicks = 0;
        if (_path == null)
        {
            return;
        }
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Data, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Log.Error("Failed to save settings", e);
        }
    }
}
