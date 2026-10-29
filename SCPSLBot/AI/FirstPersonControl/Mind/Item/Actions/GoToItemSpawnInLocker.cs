using SCPSLBot.AI.FirstPersonControl.Mind.Item.Beliefs;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions
{
    /// <summary>
    /// Opens a known closed locker chamber that may hold a matching item and looks inside.
    /// Chambers that need permissions are opened with a carried keycard (equipped under the item
    /// lease, like a door); a chamber the bot cannot open fails the action and is skipped.
    /// </summary>
    internal class GoToItemSpawnInLocker<C> : GoTo<ItemSpawnsInSightedLocker<C>, C> where C : IItemBeliefCriteria, IEquatable<C>
    {
        private const float StandOffMeters = 1f;
        private const float StandArrivalMeters = 0.6f;
        private const float ReachChamberMeters = 1.3f;
        private const float CheckedChamberSeconds = 600f;

        private readonly BotLockerOpener opener;

        public GoToItemSpawnInLocker(C criteria, FpcBotPlayer botPlayer) : base(criteria, botPlayer)
        {
            opener = new BotLockerOpener(botPlayer);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<ItemSightedLocation<C>>(this, b => b.Criteria.Equals(Criteria));
        }

        public override float Weight { get; } = 1.2f;

        public override void Stop()
        {
            opener.Finish();
            base.Stop();
        }

        protected override ActionStatus TickTowardsTarget()
        {
            if (!location.TryGetChamber(Target, out var locker, out var chamberIndex))
            {
                return ActionStatus.Completed;
            }

            var chamberTransform = locker.Chambers[chamberIndex].transform;
            // Stand in front of the chamber: of the two sides along its axis, the open one is the
            // side the bot approaches from (the back is against a wall).
            var axis = Vector3.ProjectOnPlane(chamberTransform.forward, Vector3.up).normalized * StandOffMeters;
            var front = Target - axis;
            var back = Target + axis;
            var stand = (front - botPlayer.PlayerPosition).sqrMagnitude <= (back - botPlayer.PlayerPosition).sqrMagnitude ? front : back;
            stand.y = botPlayer.PlayerPosition.y;

            var toStand = stand - botPlayer.PlayerPosition;
            toStand.y = 0f;
            var toChamber = Target - botPlayer.PlayerPosition;
            toChamber.y = 0f;
            // The exact stand point may sit off the navmesh; close to the chamber is good enough.
            if (toStand.magnitude > StandArrivalMeters && toChamber.magnitude > ReachChamberMeters && !isWorkingChamber)
            {
                botPlayer.MoveToPosition(stand);
                return ActionStatus.Running;
            }

            isWorkingChamber = true;
            switch (opener.Tick(locker, chamberIndex))
            {
                case LockerOpenProgress.Denied:
                    isWorkingChamber = false;
                    return ActionStatus.Failed;
                case LockerOpenProgress.Working:
                    return ActionStatus.Running;
            }

            if (!LookAt(Target, 6f))
            {
                return ActionStatus.Running;
            }

            isWorkingChamber = false;
            MarkTargetDone(CheckedChamberSeconds);
            return ActionStatus.Completed;
        }

        // Once the bot started working the chamber it stays put even if it drifted a little.
        private bool isWorkingChamber;

        public override void Reset()
        {
            isWorkingChamber = false;
            base.Reset();
        }

        public override string ToString()
        {
            return $"{nameof(GoToItemSpawnInLocker<C>)}({this.Criteria})";
        }
    }
}
