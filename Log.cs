using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RomesteadCheatMenu;

internal static class Log
{
    private static readonly object Sync = new object();
    private static readonly HashSet<string> Once = new HashSet<string>();
    private static string _path;

    public static void Init(string dir)
    {
        _path = Path.Combine(dir, "RomesteadCheatMenu.log");
        try
        {
            // Keep the previous session too: problems are usually reported after the game was restarted.
            if (File.Exists(_path))
            {
                File.Copy(_path, Path.Combine(dir, "RomesteadCheatMenu.previous.log"), overwrite: true);
            }
            File.WriteAllText(_path, string.Empty, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception e) => Write("ERROR", message + ": " + e);

    // For code that runs every frame: report a failure once instead of flooding the log.
    public static void ErrorOnce(string key, Exception e)
    {
        lock (Sync)
        {
            if (!Once.Add(key))
            {
                return;
            }
        }
        Error(key, e);
    }

    private static void Write(string level, string message)
    {
        if (_path == null)
        {
            return;
        }
        lock (Sync)
        {
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}\r\n", Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
