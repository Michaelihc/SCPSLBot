using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind
{
    /// <summary>
    /// Positions an action should skip for a while: targets that failed (with an escalating
    /// cooldown) and targets already handled. Lists are short, so a linear scan with a match
    /// tolerance beats hashing positions that may differ by float noise. Allocation-free.
    /// </summary>
    internal sealed class TargetMemory
    {
        private const float MatchSqrMeters = 0.5f * 0.5f;
        private const float FailureBaseSeconds = 30f;
        private const float FailureMaxSeconds = 180f;

        private struct Entry
        {
            public Vector3 Position;
            public float Until;
            public int Failures;
        }

        private readonly List<Entry> entries = new();

        public bool IsBlocked(Vector3 position, float now)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                if ((entry.Position - position).sqrMagnitude > MatchSqrMeters)
                {
                    continue;
                }

                if (now < entry.Until)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Skips <paramref name="position"/> for 30 s, doubling per repeated failure up to 3 min.</summary>
        public void NoteFailure(Vector3 position, float now)
        {
            var index = Find(position);
            var failures = index >= 0 ? entries[index].Failures + 1 : 1;
            var seconds = Mathf.Min(FailureMaxSeconds, FailureBaseSeconds * (1 << Mathf.Min(failures - 1, 4)));
            Set(index, position, now + seconds, failures);
        }

        /// <summary>Skips <paramref name="position"/> for <paramref name="seconds"/> (a visited spot, a searched room).</summary>
        public void NoteDone(Vector3 position, float now, float seconds)
        {
            var index = Find(position);
            var failures = index >= 0 ? entries[index].Failures : 0;
            var until = now + seconds;
            if (index >= 0 && entries[index].Until > until)
            {
                until = entries[index].Until;
            }

            Set(index, position, until, failures);
        }

        public void Clear()
        {
            entries.Clear();
        }

        private int Find(Vector3 position)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if ((entries[index].Position - position).sqrMagnitude <= MatchSqrMeters)
                {
                    return index;
                }
            }

            return -1;
        }

        private void Set(int index, Vector3 position, float until, int failures)
        {
            var entry = new Entry { Position = position, Until = until, Failures = failures };
            if (index >= 0)
            {
                entries[index] = entry;
                return;
            }

            // Expired entries without a failure history carry no information; reuse their slot.
            var now = Time.time;
            for (var slot = 0; slot < entries.Count; slot++)
            {
                if (entries[slot].Until <= now && entries[slot].Failures == 0)
                {
                    entries[slot] = entry;
                    return;
                }
            }

            entries.Add(entry);
        }
    }
}
