using Interactables;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration.Distributors;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses
{
    internal class LockersWithinSightSense : SightSense<Locker>
    {
        public HashSet<Locker> LockersWithinSight => ComponentsWithinSight;

        public LockersWithinSightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        { }

        private LayerMask interactableLayerMask = LayerMask.GetMask("InteractableNoPlayerCollision");
        protected override LayerMask LayerMask => interactableLayerMask;

        protected override void AddColliderDatas(Collider triggeringCollider, Locker locker)
        {
            if (TryGetColliders(triggeringCollider, locker, out var colliders))
            {
                foreach (var collider in colliders)
                {
                    TrackCollider(collider, locker);
                }
            }
        }

        protected override void RemoveColliderDatas(Collider triggeringCollider, Locker locker)
        {
            if (locker != null && TryGetColliders(triggeringCollider, locker, out var colliders))
            {
                foreach (var collider in colliders)
                {
                    UntrackCollider(collider);
                }
            }
        }

        private readonly List<InteractableCollider> interactables = new();
        private readonly List<Collider> interactableColliders = new();

        private bool TryGetColliders(Collider triggeringCollider, Locker locker, out IEnumerable<Collider> colliders)
        {
            locker.GetComponentsInChildren(interactables);

            if (interactables.Count > 0)
            {
                var lockerChamber = triggeringCollider.GetComponentInParent<LockerChamber>();
                var chamberColliderId = Array.IndexOf(locker.Chambers, lockerChamber);
                chamberColliderId %= interactables.Count;

                foreach (var interactable in interactables)
                {
                    if (interactable.ColliderId == chamberColliderId)
                    {
                        interactable.GetComponentsInChildren(interactableColliders);
                        colliders = interactableColliders;
                        return true;
                    }
                }
            }

            colliders = default;
            return false;
        }

        public override void ProcessSightSensedItems()
        {
        }
    }
}
