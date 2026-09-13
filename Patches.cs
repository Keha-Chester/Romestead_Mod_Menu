using System;
using System.Collections.Generic;
using System.Reflection;
using Candide;
using Candide.Input;
using Candide.Terminal;
using Candide.Toolkit;
using CandideServer.Entities.Controllers;
using CandideServer.ServerManagers;
using CandideServer.ServerSystems;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Shared.Combat;
using Shared.Entity;
using Shared.Models.Construction;
using Shared.Text;

namespace RomesteadCheatMenu;

internal static class Patches
{
    public static void Apply(Harmony harmony)
    {
        // Hotkey detection before the game reads input, input blocking right after.
        Patch(harmony, () => AccessTools.Method(typeof(InputManager), nameof(InputManager.Update)), nameof(InputUpdatePrefix), nameof(InputUpdatePostfix));
        // Terminal/text-field keyboard events bypass InputManager.
        Patch(harmony, () => AccessTools.Method(typeof(KeyboardInput), nameof(KeyboardInput.Update)), nameof(SkipWhileBlocking));
        Patch(harmony, () => AccessTools.Method(typeof(KeyboardInput), "TextEntered"), nameof(SkipWhileBlocking));
        // Walking reads an analog value InputManager.Update cached before our postfix; answer zero while blocking.
        Patch(harmony, () => AccessTools.Method(typeof(InputManager), nameof(InputManager.GetStickPadAxis)), nameof(StickPadAxisPrefix));
        // The game already renders Dear ImGui every frame; draw the menu inside its frame.
        Patch(harmony, () => AccessTools.Method(typeof(ImGuiRenderer), nameof(ImGuiRenderer.BeforeLayout)), nameof(BeforeLayoutPrefix));
        Patch(harmony, () => AccessTools.Method(typeof(ImGuiRenderer), nameof(ImGuiRenderer.AfterLayout)), nameof(AfterLayoutPrefix));
        Patch(harmony, () => AccessTools.Method(typeof(CandideEngine), nameof(CandideEngine.Update)), postfix: nameof(EngineUpdatePostfix));
        // Stat cheats: god mode and stat modifiers that survive the game's recalculation.
        Patch(harmony, () => AccessTools.Method(typeof(SharedCombatHandler), nameof(SharedCombatHandler.DoDamage), new[] { typeof(DamageInfo) }), nameof(DoDamagePrefix));
        Patch(harmony, () => AccessTools.Method(typeof(SharedEntityAuraManager), nameof(SharedEntityAuraManager.CalculateState), new[] { typeof(EntityWrapper) }), postfix: nameof(CalculateStatePostfix));
        // Runs on the local server thread every server loop.
        Patch(harmony, () => AccessTools.Method(typeof(ServerThreadSyncSystem), nameof(ServerThreadSyncSystem.Update)), postfix: nameof(ServerTickPostfix));

        // Erase Cart: the item, its name, flagging the placed cart and erasing instead of loading.
        Patch(harmony, () => AccessTools.Method(typeof(global::Shared.Data.SharedDataSetup), nameof(global::Shared.Data.SharedDataSetup.Setup)), postfix: nameof(SharedSetupPostfix));
        Patch(harmony, () => AccessTools.Method(typeof(LocalizationManager), nameof(LocalizationManager.SetLocalization)), postfix: nameof(LocalizationPostfix));
        Patch(harmony, () => AccessTools.Method(typeof(ConstructionsServerManager), nameof(ConstructionsServerManager.TryPlaceConstruction)), nameof(TryPlaceConstructionPrefix), finalizer: nameof(TryPlaceConstructionFinalizer));
        Patch(harmony, () => AccessTools.Method(typeof(ConstructionsServerManager), nameof(ConstructionsServerManager.SpawnConstruction), new[]
        {
            typeof(ConstructionModel), typeof(Rectangle), typeof(Guid), typeof(Guid), typeof(Dictionary<string, string>), typeof(Guid?), typeof(Guid?), typeof(Guid?)
        }), nameof(SpawnConstructionPrefix));
        Patch(harmony, () => AccessTools.Method(typeof(ServerCart2Controller), "PickupEntity"), nameof(CartPickupPrefix));
    }

    private static void Patch(Harmony harmony, Func<MethodBase> target, string prefix = null, string postfix = null, string finalizer = null)
    {
        MethodBase original = null;
        try
        {
            original = target();
            if (original == null)
            {
                Log.Error($"Patch target not found (prefix={prefix}, postfix={postfix}, finalizer={finalizer})");
                return;
            }
            harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Patches), finalizer));
            Log.Info($"Patched {original.DeclaringType?.FullName}.{original.Name}");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to patch {original?.DeclaringType?.FullName}.{original?.Name} (prefix={prefix}, postfix={postfix}, finalizer={finalizer})", e);
        }
    }

    private static void InputUpdatePrefix()
    {
        try
        {
            InputBlocker.BeforeInputUpdate();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("InputBlocker.BeforeInputUpdate", e);
        }
    }

    private static void InputUpdatePostfix()
    {
        try
        {
            InputBlocker.AfterInputUpdate();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("InputBlocker.AfterInputUpdate", e);
        }
    }

    private static bool SkipWhileBlocking()
    {
        return !InputBlocker.BlocksTextInput;
    }

    private static bool StickPadAxisPrefix(ref Vector2 __result)
    {
        if (!InputBlocker.Blocking)
        {
            return true;
        }
        __result = Vector2.Zero;
        return false;
    }

    private static void BeforeLayoutPrefix(ImGuiRenderer __instance)
    {
        try
        {
            UiFonts.EnsureLoaded(__instance);
        }
        catch (Exception e)
        {
            Log.ErrorOnce("UiFonts.EnsureLoaded", e);
        }
    }

    private static void AfterLayoutPrefix()
    {
        try
        {
            Terraform.DrawOverlay();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Terraform overlay", e);
        }
        try
        {
            CheatMenu.Draw();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("CheatMenu.Draw", e);
        }
    }

    private static void EngineUpdatePostfix()
    {
        try
        {
            Terraform.ClientUpdate();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Terraform update", e);
        }
        try
        {
            CheatMenu.AfterEngineUpdate();
            PlayerCheats.ClientTick();
            Settings.SaveIfDue();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Client tick", e);
        }
    }

    private static bool DoDamagePrefix(DamageInfo damageInfo, ref float __result)
    {
        if (!PlayerCheats.GodMode)
        {
            return true;
        }
        try
        {
            if (PlayerCheats.IsHostPlayer(damageInfo?.Victim))
            {
                __result = 0f;
                return false;
            }
        }
        catch (Exception e)
        {
            Log.ErrorOnce("DoDamage prefix", e);
        }
        return true;
    }

    private static void CalculateStatePostfix(EntityWrapper entity)
    {
        try
        {
            PlayerCheats.OnCalculateState(entity);
        }
        catch (Exception e)
        {
            Log.ErrorOnce("CalculateState postfix", e);
        }
    }

    private static void ServerTickPostfix()
    {
        try
        {
            PlayerCheats.ServerTick();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Server tick", e);
        }
        try
        {
            EraseCart.ServerTick();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Erase Cart tick", e);
        }
    }

    private static void SharedSetupPostfix()
    {
        try
        {
            EraseCart.RegisterItem();
        }
        catch (Exception e)
        {
            Log.Error("Erase Cart item registration failed", e);
        }
    }

    private static void LocalizationPostfix()
    {
        try
        {
            EraseCart.RegisterStrings();
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Erase Cart strings", e);
        }
    }

    private static void TryPlaceConstructionPrefix(Guid itemInstanceId)
    {
        try
        {
            EraseCart.BeforePlaceConstruction(itemInstanceId);
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Erase Cart placement check", e);
        }
    }

    private static Exception TryPlaceConstructionFinalizer(Exception __exception)
    {
        EraseCart.AfterPlaceConstruction();
        return __exception;
    }

    private static void SpawnConstructionPrefix(ConstructionModel construction, ref Dictionary<string, string> parameters)
    {
        try
        {
            EraseCart.BeforeSpawnConstruction(construction, ref parameters);
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Erase Cart spawn flag", e);
        }
    }

    private static bool CartPickupPrefix(ServerCart2Controller __instance, EntityWrapper entity, ref bool __result)
    {
        try
        {
            if (EraseCart.TryErase(__instance, entity))
            {
                __result = true;
                return false;
            }
        }
        catch (Exception e)
        {
            Log.ErrorOnce("Erase Cart pickup", e);
        }
        return true;
    }
}
