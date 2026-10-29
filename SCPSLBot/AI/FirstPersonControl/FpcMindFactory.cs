using Interactables.Interobjects.DoorUtils;
using SCPSLBot.AI.FirstPersonControl.Mind;
using SCPSLBot.AI.FirstPersonControl.Mind.Goals;
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
using SCPSLBot.AI.FirstPersonControl.Mind.Escape;
using System.Collections.Generic;

namespace SCPSLBot.AI.FirstPersonControl
{
    /// <summary>
    /// Builds the EscapeTheFacility mind for Class-D and scientists. The goal needs the escape
    /// route open (every zone border on the way passable); a border needs a keycard with
    /// Checkpoints (LCZ/HCZ/EZ) or ExitGates (Surface) unless its gate is held open; keycards
    /// come from pickups the bot sees, spawnpoints and lockers it checks, rooms it searches and
    /// SCP-914 upgrades. Zone borders, the escape zone and SCP-914 are map knowledge.
    /// </summary>
    internal class FpcMindFactory
    {
        /// <summary>Directed zone borders and the keycard permission their gates need by default.</summary>
        private static readonly (FacilityZone From, FacilityZone To, DoorPermissionFlags Permission)[] Borders =
        {
            (FacilityZone.LightContainment, FacilityZone.HeavyContainment, DoorPermissionFlags.Checkpoints),
            (FacilityZone.HeavyContainment, FacilityZone.LightContainment, DoorPermissionFlags.Checkpoints),
            (FacilityZone.HeavyContainment, FacilityZone.Entrance, DoorPermissionFlags.Checkpoints),
            (FacilityZone.Entrance, FacilityZone.HeavyContainment, DoorPermissionFlags.Checkpoints),
            (FacilityZone.Entrance, FacilityZone.Surface, DoorPermissionFlags.ExitGates),
            (FacilityZone.Surface, FacilityZone.Entrance, DoorPermissionFlags.ExitGates),
        };

        /// <summary>Zones in the order an escape crosses them.</summary>
        private static readonly FacilityZone[] EscapeChain =
        {
            FacilityZone.LightContainment, FacilityZone.HeavyContainment, FacilityZone.Entrance, FacilityZone.Surface,
        };

        /// <summary>The borders an escape still crosses after entering <paramref name="to"/>; none for crossings away from Surface.</summary>
        private static (FacilityZone From, FacilityZone To)[] OnwardBorders(FacilityZone from, FacilityZone to)
        {
            var fromIndex = System.Array.IndexOf(EscapeChain, from);
            var toIndex = System.Array.IndexOf(EscapeChain, to);
            if (fromIndex < 0 || toIndex != fromIndex + 1)
            {
                return System.Array.Empty<(FacilityZone, FacilityZone)>();
            }

            var onward = new List<(FacilityZone From, FacilityZone To)>();
            for (var index = toIndex; index + 1 < EscapeChain.Length; index++)
            {
                onward.Add((EscapeChain[index], EscapeChain[index + 1]));
            }

            return onward.ToArray();
        }

        public static void BuildMind(FpcMind mind, FpcBotPlayer botPlayer, FpcBotPerception perception)
        {
            var roomSense = perception.GetSense<RoomSightSense>();
            var itemsSense = perception.GetSense<ItemsWithinSightSense>();
            var inventorySense = perception.GetSense<ItemsInInventorySense>();
            var lockersSense = perception.GetSense<LockersWithinSightSense>();

            // Doors and elevators are crossed by BotLocomotion; the mind only chooses destinations.
            mind.AddBelief(new GlassObstacle(perception.GetSense<GlassSightSense>(), botPlayer.Navigator));

            mind.AddBelief(new RoomEnterLocation(roomSense));
            mind.AddBelief(new ZoneWithin(roomSense, botPlayer.Navigator));

            #region Zone borders and the escape
            foreach (var (from, to, _) in Borders)
            {
                mind.AddBelief(new ZoneEnterLocation(to, from));
                mind.AddBelief(new ZoneAccess(from, to));
            }

            mind.AddBelief(new FacilityEscapeLocation(roomSense));
            mind.AddBelief(new EscapeRouteAccess());
            mind.AddBelief(new PlayerEscaped(botPlayer));

            foreach (var (from, to, _) in Borders)
            {
                var crossing = new GoToZoneEnterLocation(to, from, OnwardBorders(from, to), botPlayer);
                mind.AddAction(crossing);
                if (to == FacilityZone.Surface)
                {
                    // On Surface nothing stands between the bot and the escape zone.
                    mind.ActionImpacts<EscapeRouteAccess>(crossing);
                }
            }

            mind.AddAction(new GoToEscapeLocation(botPlayer));
            #endregion

            #region SCP-914
            mind.AddBelief(new Scp914Location(roomSense));
            mind.AddBelief(new Scp914Controls(perception.GetSense<InteractablesWithinSightSense>()));
            mind.AddBelief(new Scp914RunningOnSetting(roomSense));

            mind.AddAction(new GoToStartScp914OnSetting(Scp914.Scp914KnobSetting.Fine, botPlayer));
            mind.AddAction(new GoToStartScp914OnSetting(Scp914.Scp914KnobSetting.OneToOne, botPlayer));
            #endregion

            mind.AddBelief(new LockerSpawnsLocation(StructureType.StandardLocker, roomSense, itemsSense));

            #region Keycards of a type (SCP-914 inputs and outputs)
            foreach (var keycardType in new[] { ItemType.KeycardJanitor, ItemType.KeycardScientist, ItemType.KeycardZoneManager, ItemType.KeycardResearchCoordinator, ItemType.KeycardFacilityManager })
            {
                mind.AddBelief(new ItemSightedLocation<ItemOfType>(keycardType, itemsSense));
                mind.AddBelief(new ItemInInventory<ItemOfType>(keycardType, inventorySense));
                mind.AddAction(new GoToPickupItem<ItemOfType>(keycardType, botPlayer));
            }
            #endregion

            #region Checkpoints keycard picking up and searching
            var checkpointsCard = new KeycardWithPermissions(DoorPermissionFlags.Checkpoints);
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(checkpointsCard, itemsSense));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(checkpointsCard, inventorySense));
            var pickupCheckpointsCard = new GoToPickupItem<KeycardWithPermissions>(checkpointsCard, botPlayer);
            mind.AddAction(pickupCheckpointsCard);

            var checkpointsSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardZoneManager, ItemType.KeycardResearchCoordinator,
                ItemType.KeycardGuard, ItemType.KeycardMTFPrivate, ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardContainmentEngineer, ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(checkpointsCard, checkpointsSpawnItemTypes, roomSense, itemsSense));
            mind.AddBelief(new ItemSpawnsInSightedLocker<KeycardWithPermissions>(checkpointsCard, checkpointsSpawnItemTypes, lockersSense));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(checkpointsCard, botPlayer));
            mind.AddAction(new GoToLockerSpawnLocation<KeycardWithPermissions>(checkpointsCard, StructureType.StandardLocker, botPlayer));
            mind.AddAction(new GoToItemSpawnInLocker<KeycardWithPermissions>(checkpointsCard, botPlayer));
            mind.AddAction(new GoToSearchRoom<KeycardWithPermissions>(checkpointsCard, botPlayer));
            #endregion

            #region ExitGates keycard picking up and searching
            var exitGatesCard = new KeycardWithPermissions(DoorPermissionFlags.ExitGates);
            mind.AddBelief(new ItemSightedLocation<KeycardWithPermissions>(exitGatesCard, itemsSense));
            mind.AddBelief(new ItemInInventory<KeycardWithPermissions>(exitGatesCard, inventorySense));
            var pickupExitGatesCard = new GoToPickupItem<KeycardWithPermissions>(exitGatesCard, botPlayer);
            mind.AddAction(pickupExitGatesCard);

            var exitGatesSpawnItemTypes = new ItemType[]
            {
                ItemType.KeycardMTFOperative, ItemType.KeycardMTFCaptain, ItemType.KeycardChaosInsurgency,
                ItemType.KeycardFacilityManager, ItemType.KeycardO5
            };
            mind.AddBelief(new ItemSpawnsLocation<KeycardWithPermissions>(exitGatesCard, exitGatesSpawnItemTypes, roomSense, itemsSense));

            mind.AddAction(new GoToItemSpawnLocation<KeycardWithPermissions>(exitGatesCard, botPlayer));
            // Searched in whichever zone the bot is in: the need shows up in Entrance (at the gate),
            // and a zone-bound search would send it back and forth across the checkpoint.
            mind.AddAction(new GoToSearchRoom<KeycardWithPermissions>(exitGatesCard, botPlayer));
            #endregion

            // Picking up the right card is what opens a border.
            foreach (var (from, to, permission) in Borders)
            {
                var pickup = permission == DoorPermissionFlags.ExitGates ? pickupExitGatesCard : pickupCheckpointsCard;
                mind.ActionImpacts<ZoneAccess>(pickup, b => b.From == from && b.To == to);
            }

            #region SCP-914 keycard upgrades
            var outputKeycardFacilityManagerCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardFacilityManager),
                checkpointsCard,
                exitGatesCard,
            };

            var outputKeycardResearchSupervisorCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardResearchCoordinator),
                checkpointsCard,
            };

            var outputKeycardScientistCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardScientist),
            };

            var outputKeycardZoneManagerCriterias = new IItemBeliefCriteria[]
            {
                new ItemOfType(ItemType.KeycardZoneManager),
                checkpointsCard,
            };

            var outakes = new OutakeRegistry(mind, botPlayer, itemsSense);

            foreach (var inputType in new[] { ItemType.KeycardJanitor, ItemType.KeycardScientist, ItemType.KeycardZoneManager, ItemType.KeycardResearchCoordinator })
            {
                mind.AddBelief(new ItemInIntakeChamber<ItemOfType>(new(inputType)));
                mind.AddAction(new GoToDropItemInIntakeChamber<ItemOfType>(new(inputType), botPlayer));
            }

            // KeycardScientist to KeycardResearchCoordinator on Fine
            outakes.AddUpgrade(ItemType.KeycardScientist, outputKeycardResearchSupervisorCriterias, Scp914.Scp914KnobSetting.Fine);
            // KeycardZoneManager to KeycardFacilityManager on Fine
            outakes.AddUpgrade(ItemType.KeycardZoneManager, outputKeycardFacilityManagerCriterias, Scp914.Scp914KnobSetting.Fine);
            // KeycardResearchCoordinator to KeycardFacilityManager on Fine
            outakes.AddUpgrade(ItemType.KeycardResearchCoordinator, outputKeycardFacilityManagerCriterias, Scp914.Scp914KnobSetting.Fine);
            // KeycardScientist to KeycardZoneManager on 1:1
            outakes.AddUpgrade(ItemType.KeycardScientist, outputKeycardZoneManagerCriterias, Scp914.Scp914KnobSetting.OneToOne);
            // KeycardJanitor to KeycardZoneManager on 1:1
            outakes.AddUpgrade(ItemType.KeycardJanitor, outputKeycardZoneManagerCriterias, Scp914.Scp914KnobSetting.OneToOne);
            // KeycardJanitor to KeycardScientist on Fine
            outakes.AddUpgrade(ItemType.KeycardJanitor, outputKeycardScientistCriterias, Scp914.Scp914KnobSetting.Fine);
            #endregion

            #region Keycard searching for SCP-914 inputs
            foreach (var keycardType in new[] { ItemType.KeycardScientist, ItemType.KeycardZoneManager, ItemType.KeycardJanitor })
            {
                var card = new ItemOfType(keycardType);
                var spawnTypes = new[] { keycardType };
                mind.AddBelief(new ItemSpawnsLocation<ItemOfType>(card, spawnTypes, roomSense, itemsSense));
                mind.AddBelief(new ItemSpawnsInSightedLocker<ItemOfType>(card, spawnTypes, lockersSense));

                mind.AddAction(new GoToItemSpawnLocation<ItemOfType>(card, botPlayer));
                mind.AddAction(new GoToLockerSpawnLocation<ItemOfType>(card, StructureType.StandardLocker, botPlayer));
                mind.AddAction(new GoToItemSpawnInLocker<ItemOfType>(card, botPlayer));
                mind.AddAction(new GoToSearchRoom<ItemOfType>(card, botPlayer));
            }
            #endregion

            mind.AddGoal(new EscapeTheFacility());

            _ = new FacilityAccessTracker(botPlayer, mind, inventorySense);
        }

        /// <summary>
        /// One <see cref="ItemsInOutakeChamber"/> belief per distinct output criteria (several
        /// recipes share outputs), and one trip to the output chamber for every output a sighting
        /// belief can take over from.
        /// </summary>
        private sealed class OutakeRegistry
        {
            private readonly FpcMind mind;
            private readonly FpcBotPlayer botPlayer;
            private readonly ItemsWithinSightSense itemsSense;
            private readonly List<IItemBeliefCriteria> registered = new();

            public OutakeRegistry(FpcMind mind, FpcBotPlayer botPlayer, ItemsWithinSightSense itemsSense)
            {
                this.mind = mind;
                this.botPlayer = botPlayer;
                this.itemsSense = itemsSense;
            }

            public void AddUpgrade(ItemType input, IItemBeliefCriteria[] outputs, Scp914.Scp914KnobSetting setting)
            {
                foreach (var output in outputs)
                {
                    Ensure(output);
                }

                mind.AddAction(new WaitForItemUpgrading(input, outputs, setting));
            }

            private void Ensure(IItemBeliefCriteria criteria)
            {
                foreach (var known in registered)
                {
                    if (known.Equals(criteria))
                    {
                        return;
                    }
                }

                registered.Add(criteria);
                mind.AddBelief(new ItemsInOutakeChamber(criteria, itemsSense));

                switch (criteria)
                {
                    case ItemOfType ofType when mind.Beliefs.ContainsKey(typeof(ItemSightedLocation<ItemOfType>))
                                                && mind.GetBelief<ItemSightedLocation<ItemOfType>>(b => b.Criteria.Equals(ofType)) != null:
                        mind.AddAction(new GoToItemInOutakeChamber<ItemOfType>(ofType, botPlayer));
                        break;
                    case KeycardWithPermissions withPermissions when mind.GetBelief<ItemSightedLocation<KeycardWithPermissions>>(b => b.Criteria.Equals(withPermissions)) != null:
                        mind.AddAction(new GoToItemInOutakeChamber<KeycardWithPermissions>(withPermissions, botPlayer));
                        break;
                }
            }
        }
    }
}
