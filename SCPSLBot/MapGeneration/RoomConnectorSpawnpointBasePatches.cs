using MapGeneration.RoomConnectors.Spawners;
using HarmonyLib;
using System.Collections.Generic;
using MapGeneration.RoomConnectors;

namespace SCPSLBot.MapGeneration
{
    [HarmonyPatch(typeof(RoomConnectorSpawnpointBase))]
    internal static class RoomConnectorSpawnpointBasePatches
    {
        private static readonly HashSet<SpawnableRoomConnectorType> SupportedConnectorTypes = new()
        {
            SpawnableRoomConnectorType.LczStandardDoor,
            SpawnableRoomConnectorType.HczStandardDoor,
            SpawnableRoomConnectorType.EzStandardDoor,
        };

        [HarmonyPatch(nameof(RoomConnectorSpawnpointBase.Spawn))]
        [HarmonyPrefix]
        public static void SpawnWithSupportedType(RoomConnectorSpawnpointBase __instance, ref SpawnableRoomConnectorType type)
        {
            // Off by default: rewriting every connector to a standard door mutates the whole map and
            // conflicts with other map plugins. With it off, bots instead get navmesh links built
            // across door-less connectors (NavigationSystem.ConnectDoorlessConnectors).
            if (!(LabApiPlugin.Instance?.Config?.ForceStandardDoorConnectors ?? false))
            {
                return;
            }

            if (!SupportedConnectorTypes.Contains(type))
            {
                type = SpawnableRoomConnectorType.HczStandardDoor;
            }
        }
    }
}
