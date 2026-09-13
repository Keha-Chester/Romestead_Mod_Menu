using System;
using Candide;
using Candide.PlayerMode;
using CandideServer.Server;
using CandideServer.ServerSystems;

namespace RomesteadCheatMenu;

// Pauses a single-player game while the menu is open, the same way the game's own favours and
// world overview windows do it. Multiplayer games keep running, as they do for those windows.
internal static class GamePause
{
    private static AbstractModeManager _pausedManager;
    private static bool _previousValue;

    public static void Begin()
    {
        if (_pausedManager != null)
        {
            return;
        }
        AbstractModeManager manager = Globals.Game?.ModeManager;
        if (manager == null)
        {
            return;
        }
        _pausedManager = manager;
        _previousValue = manager.ShouldPauseInSinglePlayer;
        manager.ShouldPauseInSinglePlayer = true;
    }

    public static void End()
    {
        if (_pausedManager == null)
        {
            return;
        }
        _pausedManager.ShouldPauseInSinglePlayer = _previousValue;
        _pausedManager = null;
    }
}

internal static class ServerQueue
{
    // Runs the action on the local server thread.
    public static void Run(Action action)
    {
        ServerThreadSyncSystem.Queue(action);
        Nudge();
    }

    // A paused server does no ticks at all, so queued work and messages would wait until the menu
    // closes. Allow exactly one tick (what the game's frame stepper does) so the change shows up now.
    public static void Nudge()
    {
        if (GameAccess.IsHost && BaseServer.Instance.Paused)
        {
            BaseServer.Instance.DoOneUpdate = true;
        }
    }
}
