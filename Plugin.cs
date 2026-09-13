using System;
using HarmonyLib;

namespace RomesteadCheatMenu;

public static class Plugin
{
    public const string Version = "1.0.0";

    public static string ModDir { get; private set; }

    public static void Init(string modDir)
    {
        ModDir = modDir;
        Log.Init(modDir);
        Log.Info($"Romestead Cheat Menu {Version} (.NET {Environment.Version}, Harmony {typeof(Harmony).Assembly.GetName().Version})");
        Settings.Load(modDir);
        Patches.Apply(new Harmony("chester.romestead.cheatmenu"));
    }
}
