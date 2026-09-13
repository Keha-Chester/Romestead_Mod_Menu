using Candide;
using Candide.GameModels;
using Candide.Multiplayer.Network;
using Candide.Multiplayer.Services;
using Candide.Scene;
using CandideServer.Server;
using Shared.Entity;
using Shared.Models.Player;

namespace RomesteadCheatMenu;

internal static class GameAccess
{
    public static bool InWorld
    {
        get
        {
            CandideEngine engine = Globals.Game;
            if (engine == null || engine.Player == null || engine.Player.Removed)
            {
                return false;
            }
            if (GameSceneManager.GameSceneGame == null || GameSceneManager.GameSceneGame.CurrentScene != engine)
            {
                return false;
            }
            if (ConnectService.State == null || ConnectService.State.Step != ConnectionStep.Playing)
            {
                return false;
            }
            return GameState.LocalPlayer.Character != null;
        }
    }

    // Spawning and favour points run on the local server, which only exists for the host.
    public static bool IsHost => LocalHostServerManager.StartedServer && BaseServer.Instance != null && BaseServer.Instance.Running;

    public static PlayerCharacterModel LocalCharacter => GameState.LocalPlayer.Character?.Character;

    public static EntityWrapper LocalPlayerEntity => InWorld ? Globals.Game.Player : null;
}
