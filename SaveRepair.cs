using CandideServer;
using CandideServer.Models.Buildings;
using CandideServer.SimulationModels;
using CandideServer.World;
using Shared.Data;
using Shared.Models.Buildings;
using Shared.Models.Construction;

namespace RomesteadCheatMenu;

// Some building levels exist in the game data before their maps ship: Bakery level 3 points at
// "maps/buildings/bakery_3", which is not in the game. A building upgraded to such a level makes the
// world stop loading with "Missing data files". Worlds are repaired on load by stepping the building
// back to its last level whose maps exist, and the buildings tool never offers such upgrades.
internal static class SaveRepair
{
    public static bool MapExists(string mapName)
    {
        return !string.IsNullOrWhiteSpace(mapName) && ServerWorldDataManager.WorldData.ContainsKey(mapName.Replace('\\', '/'));
    }

    // True when upgrading to this building level can load everything it needs (what BuildingsSController.UpgradeBuilding uses).
    public static bool HasMaps(BuildingData data, string biomeCategory)
    {
        if (data == null)
        {
            return false;
        }
        return MapExists(ExteriorMapFor(data, biomeCategory)) && (string.IsNullOrWhiteSpace(data.InteriorMap) || MapExists(data.InteriorMap));
    }

    private static string ExteriorMapFor(BuildingData data, string biomeCategory)
    {
        return biomeCategory == null ? data.ExteriorMap : data.GetExteriorMap(biomeCategory);
    }

    // On load the game falls back to the default exterior map when the biome one is missing.
    private static bool ExteriorLoads(BuildingData data, string biomeCategory)
    {
        return MapExists(ExteriorMapFor(data, biomeCategory)) || MapExists(data.ExteriorMap);
    }

    // Server thread, right before the game assigns building exterior maps while loading a world.
    public static void RepairBuildings()
    {
        foreach (BuildingSimulationModel building in ServerGameState.Buildings.Values)
        {
            BuildingInstanceModel instance = building.InstanceModel;
            BuildingData data = BuildingDataBase.GetBuildingOrNull(instance.BuildingDataId);
            if (data == null || ExteriorLoads(data, instance.BiomeCategory))
            {
                continue;
            }
            ConstructionModel construction = ConstructionDataBase.GetConstructionOrNull(instance.ConstructionId);
            ConstructionModel restored = null;
            BuildingData restoredData = null;
            for (int step = 0; step < 10 && construction?.UpgradeForId != null; step++)
            {
                ConstructionModel previous = ConstructionDataBase.GetConstructionOrNullForBackwardsUpgrade(construction.UpgradeForId, ConstructionType.Building);
                BuildingData previousData = BuildingDataBase.GetBuildingOrNull(previous?.SpawnedId);
                if (previousData == null)
                {
                    break;
                }
                if (ExteriorLoads(previousData, instance.BiomeCategory))
                {
                    restored = previous;
                    restoredData = previousData;
                    break;
                }
                construction = previous;
            }
            if (restored == null)
            {
                Log.Warn($"Save repair: building {instance.ConstructionId} ({instance.Id}) has no map '{data.ExteriorMap}' and no earlier level to step back to");
                continue;
            }
            Log.Info($"Save repair: {instance.ConstructionId} ({instance.Id}) needs the missing map '{data.ExteriorMap}'; stepped back to {restored.Id}");
            // The same fields BuildingsSController.UpgradeBuilding changes; the game sets the exterior map from BuildingDataId next.
            instance.ConstructionId = restored.Id;
            instance.BuildingDataId = restoredData.Id;
            instance.BuildingTypeId = restoredData.BuildingTypeId;
            instance.EntityProximityFunctionality = restoredData.EntityProximityFunctionality;
            instance.FoodDispenser = restoredData.FoodDispenser;
            instance.WaterDispenser = restoredData.WaterDispenser;
            instance.Input = restoredData.Input;
            instance.Output = restoredData.Output;
        }
    }
}
