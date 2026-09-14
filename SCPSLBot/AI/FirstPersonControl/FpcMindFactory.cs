using Interactables.Interobjects.DoorUtils;
using SCPSLBot.AI.FirstPersonControl.Mind;
using SCPSLBot.AI.FirstPersonControl.Mind.Goals;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using SCPSLBot.AI.FirstPersonControl.Mind.Item;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Keycard;
using SCPSLBot.AI.FirstPersonControl.Mind.Misc;
using SCPSLBot.AI.FirstPersonControl.Mind.Room.Beliefs;
using SCPSLBot.AI.FirstPersonControl.Mind.Scp914;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using MapGeneration.Distributors;
using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Room;
using SCPSLBot.AI.FirstPersonControl.Mind.Elevation;
using SCPSLBot.AI.FirstPersonControl.Mind.Escape;

namespace SCPSLBot.AI.FirstPersonControl
{
    internal class FpcMindFactory
    {
        private const DoorPermissionFlags KeycardO5Permissions = DoorPermissionFlags.Checkpoints | DoorPermissionFlags.ExitGates | 
                                                                DoorPermissionFlags.Intercom | DoorPermissionFlags.AlphaWarhead | 
                                                                DoorPermissionFlags.ContainmentLevelOne | DoorPermissionFlags.ContainmentLevelTwo | DoorPermissionFlags.ContainmentLevelThree | 
                                                                DoorPermissionFlags.ArmoryLevelOne | DoorPermissionFlags.ArmoryLevelTwo | DoorPermissionFlags.ArmoryLevelThree;
        private const DoorPermissionFlags PermissionsCheckpointContainmentLevelOneTwo = DoorPermissionFlags.Checkpoints | DoorPermissionFlags.ContainmentLevelOne | DoorPermissionFlags.ContainmentLevelTwo;

        public static void BuildMind(FpcMind mind, FpcBotPlayer botPlayer, FpcBotPerception perception)
        {
            mind.AddBelief(new DoorObstacle(perception.GetSense<DoorsWithinSightSense>(), botPlayer.Navigator));
            mind.AddBelief(new GlassObstacle(perception.GetSense<GlassSightSense>(), botPlayer.Navigator));


            mind.AddBelief(new ElevationObstacle(perception.GetSense<DoorsWithinSightSense>(), botPlayer.Navigator));
            mind.AddAction(new CallAndWaitForElevator(botPlayer));
            mind.AddAction(new TravelOnElevator(botPlayer));


            mind.AddBelief(new RoomEnterLocation(perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneWithin(perception.GetSense<RoomSightSense>(), botPlayer.Navigator));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.LightContainment, FacilityZone.HeavyContainment, perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.LightContainment, perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.Entrance, perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.Entrance, FacilityZone.HeavyContainment, perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.Entrance, FacilityZone.Surface, perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new ZoneEnterLocation(FacilityZone.Surface, FacilityZone.Entrance, perception.GetSense<RoomSightSense>()));

            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.LightContainment, FacilityZone.HeavyContainment, botPlayer));
            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.LightContainment, botPlayer));
            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.Entrance, botPlayer));
            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.Entrance, FacilityZone.HeavyContainment, botPlayer));
            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.Entrance, FacilityZone.Surface, botPlayer));
            mind.AddAction(new GoToZoneEnterLocation(FacilityZone.Surface, FacilityZone.Entrance, botPlayer));
            mind.AddAction(new GoToSearchRoomForZoneEnterLocation(FacilityZone.LightContainment, FacilityZone.HeavyContainment, botPlayer));
            mind.AddAction(new GoToSearchRoomForZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.LightContainment, botPlayer));
            mind.AddAction(new GoToSearchRoomForZoneEnterLocation(FacilityZone.HeavyContainment, FacilityZone.Entrance, botPlayer));
            mind.AddAction(new GoToSearchRoomForZoneEnterLocation(FacilityZone.Entrance, FacilityZone.HeavyContainment, botPlayer));
            mind.AddAction(new GoToSearchRoomForZoneEnterLocation(FacilityZone.Surface, FacilityZone.Entrance, botPlayer));


            mind.AddBelief(new Scp914Location(perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new Scp914Controls(perception.GetSense<InteractablesWithinSightSense>()));
            mind.AddBelief(new Scp914RunningOnSetting(perception.GetSense<RoomSightSense>()));

            mind.AddAction(new GoToSearchRoomForScp914(botPlayer));
            mind.AddAction(new GoToStartScp914OnSetting(Scp914.Scp914KnobSetting.Fine, botPlayer));
            mind.AddAction(new GoToStartScp914OnSetting(Scp914.Scp914KnobSetting.OneToOne, botPlayer));
            mind.AddAction(new WaitForChamberDoorOpening(botPlayer));


            mind.AddBelief(new LockerSpawnsLocation(StructureType.StandardLocker, perception.GetSense<RoomSightSense>()));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardJanitor, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardJanitor, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardJanitor, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardZoneManager, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardZoneManager, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardZoneManager, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardScientist, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardScientist, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardScientist, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardResearchCoordinator, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardResearchCoordinator, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardResearchCoordinator, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardFacilityManager, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardFacilityManager, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardFacilityManager, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardMTFOperative, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardMTFOperative, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardMTFOperative, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardMTFCaptain, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardMTFCaptain, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardMTFCaptain, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardChaosInsurgency, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardChaosInsurgency, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardChaosInsurgency, botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(ItemType.KeycardO5, perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<ItemOfType>(ItemType.KeycardO5, perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<ItemOfType>(ItemType.KeycardO5, botPlayer));


            #region ContainmentLevelOne keycard picking up and searching
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), botPlayer));

            var containmentLevelOneSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardJanitor, ItemType.KeycardZoneManager, ItemType.KeycardScientist, ItemType.KeycardResearchCoordinator,
                ItemType.KeycardGuard, ItemType.KeycardMTFPrivate, ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardContainmentEngineer, ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), containmentLevelOneSpawnItemTypes, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), containmentLevelOneSpawnItemTypes, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelOne), idx, botPlayer));
            #endregion

            #region ContainmentLevelTwo keycard picking up and searching
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), botPlayer));

            var containmentLevelTwoSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardScientist, ItemType.KeycardResearchCoordinator,
                ItemType.KeycardMTFPrivate, ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardContainmentEngineer, ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), containmentLevelTwoSpawnItemTypes, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), containmentLevelTwoSpawnItemTypes, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<KeycardWithPermissions>(new(DoorPermissionFlags.ContainmentLevelTwo), idx, botPlayer));
            #endregion

            #region Checkpoints keycard picking up and searching
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), botPlayer));

            var checkpointsSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardZoneManager, ItemType.KeycardResearchCoordinator,
                ItemType.KeycardGuard, ItemType.KeycardMTFPrivate, ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardContainmentEngineer, ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), checkpointsSpawnItemTypes, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), checkpointsSpawnItemTypes, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<KeycardWithPermissions>(new(DoorPermissionFlags.Checkpoints), idx, botPlayer));
            #endregion

            #region ExitGates keycard picking up and searching
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), perception.GetSense<ItemsInInventorySense>()));
            mind.AddAction(new GoToPickupItem<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), botPlayer));

            var exitGatesSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), exitGatesSpawnItemTypes, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<KeycardWithPermissions>(new(DoorPermissionFlags.ExitGates), FacilityZone.HeavyContainment, idx, botPlayer));
            #endregion


            var outputDoorPermissionFlags = new (KeycardWithPermissions, bool)[]
            {
                (new(DoorPermissionFlags.Checkpoints), true),
                (new(DoorPermissionFlags.ExitGates), true),
                (new(DoorPermissionFlags.Intercom), false),
                (new(DoorPermissionFlags.AlphaWarhead), false),
                (new(DoorPermissionFlags.ContainmentLevelOne), true),
                (new(DoorPermissionFlags.ContainmentLevelTwo), true),
                (new(DoorPermissionFlags.ContainmentLevelThree), false),
                (new(DoorPermissionFlags.ArmoryLevelOne), false),
                (new(DoorPermissionFlags.ArmoryLevelTwo), false),
                (new(DoorPermissionFlags.ArmoryLevelThree), false),
                (new(PermissionsCheckpointContainmentLevelOneTwo), false),
            };
            foreach (var (withPermissions, addAction) in outputDoorPermissionFlags)
            {
                mind.AddBelief(new ItemsInOutakeChamber(withPermissions, perception.GetSense<ItemsWithinSightSense>()));

                if (addAction)
                {
                    mind.AddAction(new GoToItemInOutakeChamber<KeycardWithPermissions>(withPermissions, botPlayer));
                }
            }


            var outputKeycardFacilityManagerCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardFacilityManager),
                new KeycardWithPermissions(DoorPermissionFlags.Checkpoints),
                new KeycardWithPermissions(DoorPermissionFlags.ExitGates),
                new KeycardWithPermissions(DoorPermissionFlags.Intercom),
                new KeycardWithPermissions(DoorPermissionFlags.AlphaWarhead),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelTwo),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelThree),
                new KeycardWithPermissions(PermissionsCheckpointContainmentLevelOneTwo),
            };

            var outputKeycardResearchSupervisorCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardResearchCoordinator),
                new KeycardWithPermissions(DoorPermissionFlags.Checkpoints),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelTwo),
                new KeycardWithPermissions(PermissionsCheckpointContainmentLevelOneTwo),
            };

            var outputKeycardScientistCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardScientist),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelTwo),
            };

            var outputKeycardZoneManagerCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardZoneManager),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.Checkpoints),
            };

            var outputKeycardO5Criterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardO5),
                new KeycardWithPermissions(DoorPermissionFlags.Checkpoints),
                new KeycardWithPermissions(DoorPermissionFlags.ExitGates),
                new KeycardWithPermissions(DoorPermissionFlags.Intercom),
                new KeycardWithPermissions(DoorPermissionFlags.AlphaWarhead),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelTwo),
                new KeycardWithPermissions(DoorPermissionFlags.ContainmentLevelThree),
                new KeycardWithPermissions(DoorPermissionFlags.ArmoryLevelOne),
                new KeycardWithPermissions(DoorPermissionFlags.ArmoryLevelTwo),
                new KeycardWithPermissions(DoorPermissionFlags.ArmoryLevelThree),
                new KeycardWithPermissions(PermissionsCheckpointContainmentLevelOneTwo),
            };

            #region KeycardScientist in intake chamber
            mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardScientist)));
            mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardScientist), botPlayer));
            #endregion

            #region KeycardJanitor in intake chamber
            mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardJanitor)));
            mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardJanitor), botPlayer));
            #endregion

            #region KeycardScientist to KeycardResearchCoordinator on Fine
            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardResearchCoordinator), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardScientist, outputKeycardResearchSupervisorCriterias, Scp914.Scp914KnobSetting.Fine));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardResearchCoordinator), botPlayer));
            #endregion

            #region KeycardZoneManager to KeycardFacilityManager on Fine
            mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardZoneManager)));
            mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardZoneManager), botPlayer));

            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardFacilityManager), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardZoneManager, outputKeycardFacilityManagerCriterias, Scp914.Scp914KnobSetting.Fine));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardFacilityManager), botPlayer));
            #endregion

            #region KeycardResearchCoordinator to KeycardFacilityManager on Fine
            mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardResearchCoordinator)));
            mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardResearchCoordinator), botPlayer));

            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardFacilityManager), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardResearchCoordinator, outputKeycardFacilityManagerCriterias, Scp914.Scp914KnobSetting.Fine));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardFacilityManager), botPlayer));
            #endregion

            #region KeycardScientist to KeycardZoneManager on 1:1
            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardZoneManager), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardScientist, outputKeycardZoneManagerCriterias, Scp914.Scp914KnobSetting.OneToOne));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardZoneManager), botPlayer));
            #endregion

            #region KeycardJanitor to KeycardScientist on Fine
            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardZoneManager), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardJanitor, outputKeycardZoneManagerCriterias, Scp914.Scp914KnobSetting.OneToOne));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardZoneManager), botPlayer));
            #endregion

            #region KeycardJanitor to KeycardZoneManager on 1:1
            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardScientist), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardJanitor, outputKeycardScientistCriterias, Scp914.Scp914KnobSetting.Fine));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardScientist), botPlayer));
            #endregion

            #region KeycardFacilityManager to KeycardO5 on Fine
            mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardFacilityManager)));
            mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(ItemType.KeycardFacilityManager), botPlayer));

            mind.AddBelief(new ItemsInOutakeChamber(new ItemOfType(ItemType.KeycardO5), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddAction(new WaitForItemUpgrading(ItemType.KeycardFacilityManager, outputKeycardO5Criterias, Scp914.Scp914KnobSetting.Fine));
            mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(new(ItemType.KeycardO5), botPlayer));
            #endregion


            #region KeycardMTFOperative searching
            mind.AddBelief(new ItemSpawnsLocation<ItemOfType>(ItemType.KeycardMTFOperative, new[] { ItemType.KeycardMTFOperative }, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            
            mind.AddAction(new GoToItemSpawnLocation<ItemOfType>(ItemType.KeycardMTFOperative, botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<ItemOfType>(ItemType.KeycardMTFOperative, FacilityZone.HeavyContainment, idx, botPlayer));
            #endregion

            #region KeycardScientist searching
            mind.AddBelief(new ItemSpawnsLocation<ItemOfType>(ItemType.KeycardScientist, new[] { ItemType.KeycardScientist }, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<ItemOfType>(ItemType.KeycardScientist, new[] { ItemType.KeycardScientist }, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<ItemOfType>(ItemType.KeycardScientist, botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<ItemOfType>(ItemType.KeycardScientist, StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<ItemOfType>(ItemType.KeycardScientist, botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<ItemOfType>(ItemType.KeycardScientist, idx, botPlayer));
            #endregion

            #region KeycardZoneManager searching
            mind.AddBelief(new ItemSpawnsLocation<ItemOfType>(ItemType.KeycardZoneManager, new[] { ItemType.KeycardZoneManager }, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<ItemOfType>(ItemType.KeycardZoneManager, new[] { ItemType.KeycardZoneManager }, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<ItemOfType>(ItemType.KeycardZoneManager, botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<ItemOfType>(ItemType.KeycardZoneManager, StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<ItemOfType>(ItemType.KeycardZoneManager, botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<ItemOfType>(ItemType.KeycardZoneManager, idx, botPlayer));
            #endregion

            #region KeycardJanitor searching
            mind.AddBelief(new ItemSpawnsLocation<ItemOfType>(ItemType.KeycardJanitor, new[] { ItemType.KeycardJanitor }, perception.GetSense<RoomSightSense>(), perception.GetSense<ItemsWithinSightSense>()));
            mind.AddBelief(new ItemSpawnsInSightedLocker<ItemOfType>(ItemType.KeycardJanitor, new[] { ItemType.KeycardJanitor }, perception.GetSense<LockersWithinSightSense>()));

            mind.AddAction(new GoToItemSpawnLocation<ItemOfType>(ItemType.KeycardJanitor, botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<ItemOfType>(ItemType.KeycardJanitor, StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<ItemOfType>(ItemType.KeycardJanitor, botPlayer));
            mind.AddActions(idx => new GoToSearchRoom<ItemOfType>(ItemType.KeycardJanitor, idx, botPlayer));
            #endregion


            mind.AddAction(new OpenNonKeycardDoorObstacle(botPlayer));
            mind.AddAction(new OpenKeycardDoorObstacle(DoorPermissionFlags.ContainmentLevelOne, botPlayer));
            mind.AddAction(new OpenKeycardDoorObstacle(DoorPermissionFlags.ContainmentLevelTwo, botPlayer));
            mind.AddAction(new OpenKeycardDoorObstacle(DoorPermissionFlags.Checkpoints, botPlayer));
            mind.AddAction(new OpenKeycardDoorObstacle(DoorPermissionFlags.ExitGates, botPlayer));


            mind.AddBelief(new FacilityEscapeLocation(perception.GetSense<RoomSightSense>()));
            mind.AddBelief(new PlayerEscaped());
            mind.AddAction(new GoToEscapeLocation(botPlayer));


            mind.AddBelief(new ItemSightedLocation<ItemOfType>(new(ItemType.Medkit), perception.GetSense<ItemsWithinSightSense>()));


            mind.AddGoal(new EscapeTheFacility());
            //mind.AddGoal(new GetO5Keycard());
        }
    }
}
