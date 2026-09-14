using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RomesteadCheatMenu;

// One of the game's Content/localization files, read directly: the game may run in any language,
// while the menu shows English names and searches English and Russian ones.
// Format: int32 pair count, then key/value strings with 7-bit length prefixes.
internal sealed class LocaleFile
{
    public static readonly LocaleFile English = new LocaleFile("locale_en");
    public static readonly LocaleFile Russian = new LocaleFile("locale_ru_RU");

    private readonly string _fileName;
    private Dictionary<string, string> _map;

    private LocaleFile(string fileName)
    {
        _fileName = fileName;
    }

    public string Get(string key)
    {
        if (_map == null)
        {
            _map = Load();
        }
        if (key != null && _map.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        return null;
    }

    private Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "localization", _fileName);
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                map[key] = reader.ReadString();
            }
            Log.Info($"Locale {_fileName}: {map.Count} strings");
        }
        catch (Exception e)
        {
            Log.Error("Failed to read " + path, e);
        }
        return map;
    }
}
