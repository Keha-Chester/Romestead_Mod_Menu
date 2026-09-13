namespace RomesteadCheatMenu;

// Menu language. English is the default; Russian is picked in the top-right corner of the menu.
// Texts sit next to their use as (english, russian) pairs.
internal static class Loc
{
    public const string English = "en";
    public const string Russian = "ru";

    public static bool IsRussian => Settings.Data.Language == Russian;

    public static string T(string english, string russian)
    {
        return IsRussian ? russian : english;
    }

    public static void SetLanguage(string code)
    {
        if (Settings.Data.Language == code)
        {
            return;
        }
        Settings.Data.Language = code;
        Settings.MarkDirty();
        Log.Info("Menu language: " + code);
    }
}
