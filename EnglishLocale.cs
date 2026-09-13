using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RomesteadCheatMenu;

// The game may run in another language, but search works on English names,
// so read Content/localization/locale_en directly.
// Format: int32 pair count, then key/value strings with 7-bit length prefixes.
internal static class EnglishLocale
{
    private static Dictionary<string, string> _map;

    public static string Get(string key)
    {
        if (_map == null)
        {
            _map = Load();
        }
        if (key != null && _map.TryGetValue(key, out string value))
        {
            return value;
        }
        return null;
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "localization", "locale_en");
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                map[key] = reader.ReadString();
            }
            Log.Info($"English locale: {map.Count} strings");
        }
        catch (Exception e)
        {
            Log.Error("Failed to read " + path, e);
        }
        return map;
    }
}
