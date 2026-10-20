using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Movement
{
    /// <summary>
    /// Per-bot claim on the held item. A component that temporarily equips something (a keycard
    /// for a door, a medkit) acquires the lease so others that normally own the hands (combat
    /// re-equipping its firearm) back off until it is released. Leases always expire, so a
    /// holder that is never ticked again cannot pin the hands forever; restoring the previously
    /// held item stays the holder's job.
    /// <code>
    /// if (bot.ItemLease.Acquire(this, 5f)) { equip; use; }   // later: restore, then
    /// bot.ItemLease.Release(this);
    /// </code>
    /// </summary>
    internal sealed class BotItemLease
    {
        private object owner;
        private float expiresAt;

        public bool IsHeld
        {
            get
            {
                if (owner != null && Time.time >= expiresAt)
                {
                    owner = null;
                }

                return owner != null;
            }
        }

        public object Owner => IsHeld ? owner : null;

        public bool IsHeldBy(object candidate) => candidate != null && IsHeld && ReferenceEquals(owner, candidate);

        /// <summary>
        /// Claims the lease for at most <paramref name="maxSeconds"/>; false while another owner
        /// holds it. Re-acquiring a lease already held keeps its original deadline (a hard cap).
        /// </summary>
        public bool Acquire(object candidate, float maxSeconds)
        {
            if (candidate == null)
            {
                return false;
            }

            if (IsHeld)
            {
                return ReferenceEquals(owner, candidate);
            }

            owner = candidate;
            expiresAt = Time.time + Mathf.Max(0f, maxSeconds);
            return true;
        }

        public void Release(object candidate)
        {
            if (ReferenceEquals(owner, candidate))
            {
                owner = null;
            }
        }

        public void Reset()
        {
            owner = null;
        }
    }
}
