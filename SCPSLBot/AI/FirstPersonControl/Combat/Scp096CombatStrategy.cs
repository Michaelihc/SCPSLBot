using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp096;
using PlayerStatsSystem;
using SCPSLBot.AI.FirstPersonControl.Combat.ScpAbilities;
using System.Reflection;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Owns SCP-096 rage targeting, rage lifecycle, and the native slap fallback.
    /// </summary>
    internal sealed class Scp096CombatStrategy
    {
        private const float AttackRange = 3.2f;
        private const float RageDelay = 6.6f;
        private const float PostRageCooldown = 6.5f;
        private const float ChargeMinDistance = 6f;
        private const float ChargeMaxDistance = 18f;
        private const float ChargeAimAngle = 8f;
        private const float ChargeRetrySeconds = 0.4f;
        // Native Scp096PrygateAbility accepts a gate within sqrt(8.12) m of the player; stay just inside.
        private const float PryReachSqr = 7.5f;
        private const float PryRetrySeconds = 1.5f;

        // The private result of the last swing, read only to decide on the close-range fallback.
        private static readonly FieldInfo HitResultField = typeof(Scp096AttackAbility).GetField(
            "_hitResult",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly FpcBotCombat combat;
        private readonly CombatTargetSelector targetSelector;

        private float attackAllowedTime;
        private float rageEndTime;
        private float rageReleaseTime;
        private float nextRageAllowedTime;
        private float nextDebugLogTime;
        private ReferenceHub rageTarget;
        private float nextChargeTry;
        private float nextPryTry;
        private PryableDoor failedGate;

        public Scp096CombatStrategy(FpcBotCombat combat, CombatTargetSelector targetSelector)
        {
            this.combat = combat;
            this.targetSelector = targetSelector;
        }

        public bool TrySelectRageTarget(RoleTypeId role, out CombatTarget target)
        {
            target = null;
            if (role != RoleTypeId.Scp096
                || rageTarget == null
                || rageEndTime <= 0f
                || Time.time >= rageEndTime
                || !CombatTargetSelector.AreHostile(combat.BotPlayer.BotHub.PlayerHub, rageTarget))
            {
                return false;
            }

            var distance = Vector3.Distance(combat.BotPlayer.PlayerPosition, rageTarget.transform.position);
            target = targetSelector.BuildTarget(rageTarget, distance)
                     ?? targetSelector.BuildTarget(rageTarget, distance, false);
            return true;
        }

        public void EndRageIfExpired(RoleTypeId role)
        {
            if (role != RoleTypeId.Scp096 || rageEndTime <= 0f || Time.time < rageEndTime)
            {
                return;
            }

            TryEndRage();
            nextRageAllowedTime = Time.time + PostRageCooldown;
            ClearRageTarget();
            targetSelector.ExpireChase();
        }

        public void ClearRageTarget()
        {
            rageTarget = null;
            attackAllowedTime = 0f;
            rageEndTime = 0f;
            rageReleaseTime = 0f;
        }

        public bool IsReadyToAttack(ReferenceHub target)
        {
            if (Time.time < nextRageAllowedTime)
            {
                LogDebug("waiting for post-rage cooldown.");
                return false;
            }

            if (rageTarget != target)
            {
                if (TryAttachRageTarget(target))
                {
                    return Time.time >= attackAllowedTime;
                }

                attackAllowedTime = Time.time + RageDelay;
                var rageDuration = FpcBotCombat.CurrentSettings.Scp096RageDurationSeconds;
                if (!TryStartRage(target, rageDuration))
                {
                    ClearRageTarget();
                    return false;
                }

                rageTarget = target;
                rageEndTime = attackAllowedTime + rageDuration;
                return false;
            }

            if (!TryEnsureStillRaging(target))
            {
                return false;
            }

            return Time.time >= attackAllowedTime;
        }

        private bool TryAttachRageTarget(ReferenceHub target)
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role)
            {
                return false;
            }

            if (scp096Role.StateController.RageState is not (Scp096RageState.Enraged or Scp096RageState.Distressed))
            {
                return false;
            }

            AddRageTarget(scp096Role, target);
            rageTarget = target;

            if (scp096Role.StateController.RageState is Scp096RageState.Enraged)
            {
                attackAllowedTime = Time.time;
            }
            else if (attackAllowedTime <= Time.time)
            {
                attackAllowedTime = Time.time + 0.5f;
            }

            if (rageEndTime <= Time.time)
            {
                rageEndTime = Time.time + FpcBotCombat.CurrentSettings.Scp096RageDurationSeconds;
            }

            LogDebug($"attached new rage target while {scp096Role.StateController.RageState}.");
            return true;
        }

        private bool TryEnsureStillRaging(ReferenceHub target)
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role)
            {
                LogDebug("current role is no longer Scp096.");
                return false;
            }

            AddRageTarget(scp096Role, target);
            if (scp096Role.StateController.RageState is Scp096RageState.Enraged)
            {
                return true;
            }

            if (scp096Role.StateController.RageState is Scp096RageState.Distressed)
            {
                LogDebug("is distressed, waiting for enraged state.");
                return false;
            }

            var rageDuration = FpcBotCombat.CurrentSettings.Scp096RageDurationSeconds;
            if (!TryStartRage(target, rageDuration))
            {
                LogDebug($"could not restart rage from state {scp096Role.StateController.RageState}.");
                return false;
            }

            attackAllowedTime = Time.time + RageDelay;
            rageEndTime = attackAllowedTime + rageDuration;
            LogDebug("restarted rage after native state returned to docile.");
            return false;
        }

        private bool TryStartRage(ReferenceHub target, float duration)
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role)
            {
                LogDebug("cannot rage because current role is not Scp096.");
                return false;
            }

            if (!scp096Role.SubroutineModule.TryGetSubroutine<Scp096RageManager>(out var rageManager))
            {
                LogDebug("cannot rage because Scp096RageManager is unavailable.");
                return false;
            }

            if (scp096Role.StateController.RageState is not Scp096RageState.Docile)
            {
                LogDebug($"cannot start rage from state {scp096Role.StateController.RageState}.");
                return false;
            }

            AddRageTarget(scp096Role, target);
            rageManager.ServerEnrage(Mathf.Max(20f, duration));
            LogDebug("started rage.");
            return true;
        }

        private static void AddRageTarget(Scp096Role scp096Role, ReferenceHub target)
        {
            if (scp096Role.SubroutineModule.TryGetSubroutine<Scp096TargetsTracker>(out var targetsTracker))
            {
                targetsTracker.AddTarget(target, isLooking: true);
            }
        }

        private bool TryEndRage()
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role
                || !scp096Role.SubroutineModule.TryGetSubroutine<Scp096RageManager>(out var rageManager))
            {
                return false;
            }

            if (scp096Role.StateController.RageState is Scp096RageState.Enraged or Scp096RageState.Distressed)
            {
                rageManager.ServerEndEnrage();
            }

            return true;
        }

        public void ReleaseHeldRageKeyIfNeeded()
        {
            if (rageReleaseTime <= 0f || Time.time < rageReleaseTime)
            {
                return;
            }

            rageReleaseTime = 0f;
            combat.TryClickGroupedDummyAction("Scp096RageCycleAbility", "Reload->Release");
        }

        public bool TryUseNativeAttack(ReferenceHub target)
        {
            if (combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role)
            {
                return false;
            }

            if (scp096Role.StateController.RageState is not Scp096RageState.Enraged)
            {
                LogDebug($"cannot attack while state is {scp096Role.StateController.RageState}.");
                return false;
            }

            if (scp096Role.StateController.AbilityState is not Scp096AbilityState.None)
            {
                return true;
            }

            if (!combat.Abilities.TryGet<Scp096AttackAbility>(out var attackAbility))
            {
                LogDebug("native attack ability is unavailable.");
                return false;
            }

            // Same payload the client writes: owner position and camera rotation, no backtracked players.
            // The native command swings the left/right hand sphere at the camera and enforces the cooldown.
            if (!ScpAbilityCommands.SendOwnerPose(
                    attackAbility,
                    scp096Role.FpcModule.Position,
                    combat.BotPlayer.BotHub.PlayerHub.PlayerCameraReference.rotation))
            {
                return false;
            }

            // Only a swing that really ran (the native command sets the Attacking state) can have missed.
            var hitResult = scp096Role.StateController.AbilityState is Scp096AbilityState.Attacking
                ? HitResultField?.GetValue(attackAbility)
                : null;
            if (hitResult is Scp096HitResult result && result == Scp096HitResult.None && target != null)
            {
                TryApplyDirectCloseDamage(scp096Role, target);
            }

            return true;
        }

        /// <summary>Charge dash toward a rage target that is in clear sight at mid range.</summary>
        public void TryUseChargeAbility(CombatTarget target)
        {
            var now = Time.time;
            if (now < nextChargeTry
                || !target.HasLineOfSight
                || target.Distance < ChargeMinDistance
                || target.Distance > ChargeMaxDistance
                || !combat.Abilities.TryGet<Scp096ChargeAbility>(out var charge)
                || !charge.CanCharge
                || !combat.IsAimedAt(target.AimPoint, ChargeAimAngle))
            {
                return;
            }

            nextChargeTry = now + ChargeRetrySeconds;
            ScpAbilityCommands.Send(charge);
        }

        /// <summary>
        /// Pries a closed gate on the chase path instead of opening it like a normal door. Returns true
        /// while the bot is approaching or prying it; false hands the door to the regular door logic.
        /// </summary>
        public bool TryPryGate(DoorVariant door, Vector3 hitPoint)
        {
            if (door is not PryableDoor gate
                || combat.BotPlayer.BotHub.PlayerHub.roleManager.CurrentRole is not Scp096Role scp096Role
                || scp096Role.StateController.RageState is not Scp096RageState.Enraged)
            {
                return false;
            }

            if (scp096Role.StateController.AbilityState is Scp096AbilityState.PryingGate)
            {
                return true;
            }

            var now = Time.time;
            if (gate.TargetState
                || gate.GetExactState() > 0f
                || (failedGate == gate && now < nextPryTry)
                || !combat.Abilities.TryGet<Scp096PrygateAbility>(out var prygate))
            {
                return false;
            }

            var offset = gate.transform.position - combat.BotPlayer.PlayerPosition;
            if (offset.sqrMagnitude > PryReachSqr)
            {
                combat.BotPlayer.MoveToPosition(hitPoint);
                return true;
            }

            ScpAbilityCommands.SendPryableDoor(prygate, gate);
            if (gate.IsBeingPried || scp096Role.StateController.AbilityState is Scp096AbilityState.PryingGate)
            {
                failedGate = null;
                return true;
            }

            // Locked or otherwise refused: let the regular door logic try before retrying the pry.
            failedGate = gate;
            nextPryTry = now + PryRetrySeconds;
            return false;
        }

        private void TryApplyDirectCloseDamage(Scp096Role scp096Role, ReferenceHub target)
        {
            if (target == null || !CombatTargetSelector.AreHostile(combat.BotPlayer.BotHub.PlayerHub, target))
            {
                return;
            }

            var distance = Vector3.Distance(combat.BotPlayer.PlayerPosition, target.transform.position);
            if (distance > AttackRange)
            {
                return;
            }

            target.playerStats.DealDamage(
                new Scp096DamageHandler(scp096Role, 60f, Scp096DamageHandler.AttackType.SlapRight));
            LogDebug("applied direct close-range 096 slap fallback.");
        }

        internal void LogDebug(string message)
        {
            if (Time.time < nextDebugLogTime)
            {
                return;
            }

            nextDebugLogTime = Time.time + 1f;
            if (BotLog.Verbose)
            {
                Debug.Log($"[SCPSLBot] 096 {message}");
            }
        }
    }
}
