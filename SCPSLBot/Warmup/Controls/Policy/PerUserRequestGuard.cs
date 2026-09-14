#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

/// <summary>
/// Rejects overlapping synchronous control callbacks for the same full authenticated UserId.
/// Keep one shared instance across role and item services so the user's mutations cannot overlap.
/// </summary>
public sealed class PerUserRequestGuard
{
    private readonly object sync = new();
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);

    public bool TryEnter(string fullUserId, out IDisposable? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(fullUserId))
        {
            return false;
        }

        lock (sync)
        {
            if (!inFlight.Add(fullUserId))
            {
                return false;
            }
        }

        lease = new Lease(this, fullUserId);
        return true;
    }

    public bool IsInFlight(string fullUserId)
    {
        lock (sync)
        {
            return inFlight.Contains(fullUserId);
        }
    }

    private void Exit(string fullUserId)
    {
        lock (sync)
        {
            inFlight.Remove(fullUserId);
        }
    }

    private sealed class Lease : IDisposable
    {
        private PerUserRequestGuard? owner;
        private readonly string fullUserId;

        public Lease(PerUserRequestGuard owner, string fullUserId)
        {
            this.owner = owner;
            this.fullUserId = fullUserId;
        }

        public void Dispose()
        {
            PerUserRequestGuard? current = owner;
            owner = null;
            current?.Exit(fullUserId);
        }
    }
}
