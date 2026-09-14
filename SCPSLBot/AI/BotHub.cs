using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.AI.FirstPersonControl;
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI
{
    internal class BotHub : IDisposable
    {
        public readonly FpcBotPlayer FpcPlayer;

        public IBotPlayer CurrentBotPlayer { get; private set; }
        public ReferenceHub PlayerHub { get; }

        public BotHub(ReferenceHub hub)
        {
            PlayerHub = hub;

            FpcPlayer = new FpcBotPlayer(this);
        }

        private float lastExceptionLogTime = float.NegativeInfinity;
        private float exceptionWindowStart;
        private int recentExceptionCount;
        private float parkedUntilTime;

        public bool IsDisposed => isDisposed;
        public bool IsParked => !isDisposed && CurrentBotPlayer == null && parkedUntilTime > Time.time;
        public float ParkedUntilTime => parkedUntilTime;
        public string LastUpdateFault { get; private set; } = string.Empty;

        public IEnumerator<JobHandle> Update()
        {
            if (isDisposed)
            {
                yield break;
            }

            // Auto-rearm a bot that was briefly parked after repeated faults, once its cooldown elapses.
            if (CurrentBotPlayer == null
                && parkedUntilTime > 0f
                && Time.time >= parkedUntilTime
                && PlayerHub != null
                && PlayerHub.roleManager?.CurrentRole is FpcStandardRoleBase fpcRole)
            {
                parkedUntilTime = 0f;
                FpcPlayer.FpcRole = fpcRole;
                CurrentBotPlayer = FpcPlayer;
                FpcPlayer.OnRoleChanged();
            }

            var botPlayerUpdate = CurrentBotPlayer?.Update();
            if (botPlayerUpdate != null)
            {
                using (botPlayerUpdate)
                {
                    while (botPlayerUpdate.TryCatchMoveNext(HandleUpdateException))
                    {
                        yield return botPlayerUpdate.Current;
                    }
                }
            }
        }

        // A single per-tick fault must not permanently disable the bot for the whole round.
        // Abort only the current tick; rate-limit logging; only park (then auto-rearm) if a
        // bot is faulting every tick, so we stop spamming without killing it forever.
        private void HandleUpdateException(Exception ex)
        {
            LastUpdateFault = $"{ex.GetType().Name}: {ex.Message}";
            if (Time.realtimeSinceStartup - lastExceptionLogTime > 5f)
            {
                Debug.LogException(ex);
                lastExceptionLogTime = Time.realtimeSinceStartup;
            }

            try
            {
                FpcPlayer.Move.DesiredLocalDirection = Vector3.zero;
            }
            catch
            {
                // best-effort; ignore secondary faults while recovering
            }

            var now = Time.time;
            if (now - exceptionWindowStart > 5f)
            {
                exceptionWindowStart = now;
                recentExceptionCount = 0;
            }

            if (++recentExceptionCount >= 30)
            {
                CurrentBotPlayer = null;
                parkedUntilTime = now + 5f;
                recentExceptionCount = 0;
            }
        }

        public void OnRoleChanged(PlayerRoleBase prevRole, PlayerRoleBase newRole)
        {
            if (isDisposed)
            {
                return;
            }

            if (newRole is FpcStandardRoleBase fpcRole)
            {
                FpcPlayer.FpcRole = fpcRole;
                CurrentBotPlayer = FpcPlayer;
            }
            else
            {
                CurrentBotPlayer = null;
            }

            CurrentBotPlayer?.OnRoleChanged();

            if (BotLog.Verbose)
            {
                Debug.Log($"Bot got new role assigned. Role Id: {newRole.RoleTypeId}");
                Debug.Log($"Type of role: {newRole.GetType()}");
            }
        }

        public void NotifyHurt(ReferenceHub attacker)
        {
            if (!isDisposed)
            {
                FpcPlayer.Combat.NotifyDamagedBy(attacker);
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            CurrentBotPlayer = null;
            FpcPlayer.Dispose();
        }

        public override string ToString()
        {
            return $"{nameof(BotHub)}: {PlayerHub}";
        }

        private bool isDisposed;
    }

    internal static class IEnumeratorExtensions
    { 
        public static bool TryCatchMoveNext<T>(this IEnumerator<T> enumerator, Action<Exception> handleException)
        {
            try
            {
                return enumerator.MoveNext();
            }
            catch (Exception ex)
            {
                handleException(ex);
                return false;
            }
        }
    }
}
