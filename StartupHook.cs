using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// .NET startup hook entry point (no namespace on purpose). The runtime calls
// Initialize() before Romestead's Main when this dll is listed in STARTUP_HOOKS.
internal class StartupHook
{
    public static void Initialize()
    {
        string dir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);

        // 0Harmony.dll sits next to us and is not part of the game's deps.json.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        Start(dir);
    }

    // Kept separate so the JIT does not need Harmony before the resolver above exists.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start(string dir)
    {
        try
        {
            RomesteadCheatMenu.Plugin.Init(dir);
        }
        catch (Exception e)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "RomesteadCheatMenu.log"), $"[{DateTime.Now:HH:mm:ss}] FATAL init: {e}\r\n");
            }
            catch
            {
            }
        }
    }
}
