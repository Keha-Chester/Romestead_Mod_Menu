using System;
using System.Collections.Generic;
using System.Reflection;
using CandideServer;
using CandideServer.Entities;
using CandideServer.Entities.Controllers;
using CandideServer.MessageModels;
using CandideServer.ServerManagers;
using CandideServer.ServerServices;
using Microsoft.Xna.Framework;
using Shared;
using Shared.Data;
using Shared.Entity;
using Shared.Entity.Components;
using Shared.Models.Construction;
using Shared.Models.Items;
using Shared.Text;

namespace RomesteadCheatMenu;

// "Erase Cart": a real Bronze Cart (same entity, model and controller) marked with an entity
// parameter when it is placed from the Erase Cart item. Whatever the cart would load vanishes.
// Without the mod a placed Erase Cart is simply a Bronze Cart again.
internal static class EraseCart
{
    public const string ItemId = "placeable:erase_cart";

    private const string SourceItemId = "placeable:cart:1";
    private const string FlagKey = "rcm_erase_cart";
    private static readonly string BronzeCartEntityId = EntityIds.BronzeCart.ToString();

    [ThreadStatic]
    private static bool _placingEraseCart;

    // Local server thread only.
    private static readonly HashSet<Guid> PendingRemovals = new HashSet<Guid>();
    private static int _erasedThisSession;

    // ------------------------------------------------------------------ data

    // Called after the game fills its item database (it is cleared and rebuilt on data reloads).
    public static void RegisterItem()
    {
        ItemData source = ItemDataBase.GetItemDataOrNull(SourceItemId);
        if (source == null)
        {
            Log.Warn($"Erase Cart: source item {SourceItemId} not found, item not registered");
            return;
        }
        var item = new ItemData();
        foreach (FieldInfo field in typeof(ItemData).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!field.IsInitOnly)
            {
                field.SetValue(item, field.GetValue(source));
            }
        }
        item.Id = ItemId;
        // Any value: AddItems swaps name and description for their localization keys.
        item.Description = ItemId;
        ItemDataBase.AddItems(new List<ItemData> { item });
        RegisterStrings();
        Log.Info("Erase Cart item registered");
    }

    // Called after every localization load, which clears all strings.
    public static void RegisterStrings()
    {
        bool russian = (StringDefinitions.CurrentLanguageCode ?? string.Empty).StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        StringDefinitions.Strings[ItemId + ItemDataBase.SuffixName] = new StringDefinition("Erase Cart");
        StringDefinitions.Strings[ItemId + ItemDataBase.SuffixDescription] = new StringDefinition(russian
            ? "Бронзовая тележка-уничтожитель: всё, что в неё попадает, сразу исчезает."
            : "A bronze cart that destroys everything that gets loaded into it.");
    }

    // ------------------------------------------------------------------ placing (server thread)

    public static void BeforePlaceConstruction(Guid itemInstanceId)
    {
        _placingEraseCart = ServerGameState.ItemInstances.TryGetValue(itemInstanceId, out ItemInstanceModel item) && item.BaseDataId == ItemId;
    }

    public static void AfterPlaceConstruction()
    {
        _placingEraseCart = false;
    }

    // The item places the regular "cart:1" construction; only the spawned entity gets the flag.
    public static void BeforeSpawnConstruction(ConstructionModel construction, ref Dictionary<string, string> parameters)
    {
        if (!_placingEraseCart || construction == null || construction.Type != ConstructionType.Entity || construction.SpawnedId != BronzeCartEntityId)
        {
            return;
        }
        parameters ??= new Dictionary<string, string>();
        parameters[FlagKey] = "true";
        Log.Info("Erase Cart placed");
    }

    // ------------------------------------------------------------------ erasing (server thread)

    // Replaces ServerCart2Controller.PickupEntity for Erase Carts. Covers both loading paths:
    // objects touching the cart and objects thrown into it.
    public static bool TryErase(ServerCart2Controller cart, EntityWrapper target)
    {
        EntityWrapper cartEntity = cart?.Entity;
        if (cartEntity == null || target == null || target.Removed || target.Id == cartEntity.Id)
        {
            return false;
        }
        if (!IsEraseCart(cartEntity) || !CanErase(target))
        {
            return false;
        }
        // No carts pick it up (CanBePickedUp checks this) until it is removed next server loop.
        target.NoEntityCollision = true;
        if (PendingRemovals.Add(target.Id))
        {
            ServerSoundAndVfxService.Send_PlayVfxOnPosition(new PlayVfxOnPositionMessage
            {
                SpacVfxId = "dust_poof_big",
                Position = target.Position + new Vector3(0f, 0f, 8f)
            }, cartEntity.WorldId);
        }
        return true;
    }

    private static bool IsEraseCart(EntityWrapper cartEntity)
    {
        return ServerGameState.Entities.TryGetValue(cartEntity.Id, out ServerEntityModel model)
            && model.Parameters != null
            && model.Parameters.TryGetValue(FlagKey, out string value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    // Only lifeless objects vanish. Players, citizens, animals, other carts and anything that is
    // carrying something itself (a pot with a hiding player) are loaded like in a normal cart.
    private static bool CanErase(EntityWrapper target)
    {
        if (PlayerServerManager.IsPlayerEntity(target))
        {
            return false;
        }
        Guid baseId = target.BaseGuid;
        if (baseId == EntityIds.GenericCitizenBaseId || baseId == EntityIds.WildCitizen || Array.IndexOf(EntityIds.AllCreatureGuids, baseId) >= 0)
        {
            return false;
        }
        if (target.Mask.HasFlags(Component.Controller) && target.Controller is ServerCart2Controller)
        {
            return false;
        }
        return !(ServerGameState.Entities.TryGetValue(target.Id, out ServerEntityModel model) && model.CarriedEntityId.HasValue);
    }

    // Removal is deferred so the cart's scan over touching entities is never modified mid-iteration.
    public static void ServerTick()
    {
        if (PendingRemovals.Count == 0)
        {
            return;
        }
        int removed = 0;
        foreach (Guid id in PendingRemovals)
        {
            try
            {
                EntityServerManager.RemoveEntity(id, new EntityRemoveInfo { RemoveType = EntityRemoveType.ConsumedResource });
                removed++;
            }
            catch (Exception e)
            {
                Log.Error("Erase Cart: failed to remove entity " + id, e);
            }
        }
        PendingRemovals.Clear();
        _erasedThisSession += removed;
        Log.Info($"Erase Cart: removed {removed} object(s), {_erasedThisSession} this session");
    }
}
