using System;
using System.Collections.Generic;

namespace ScpslPluginStarter.Core;

internal readonly struct BlockerUpdate
{
    public BlockerUpdate(bool tracked, bool reset, long punishableBeforeMilliseconds, long punishableDeltaMilliseconds, long resetRemainingMilliseconds)
    {
        Tracked = tracked;
        Reset = reset;
        PunishableBeforeMilliseconds = punishableBeforeMilliseconds;
        PunishableDeltaMilliseconds = punishableDeltaMilliseconds;
        ResetRemainingMilliseconds = resetRemainingMilliseconds;
    }

    public bool Tracked { get; }
    public bool Reset { get; }
    public long PunishableBeforeMilliseconds { get; }
    public long PunishableDeltaMilliseconds { get; }
    public long ResetRemainingMilliseconds { get; }
}

internal sealed class BlockerPenaltyTracker
{
    private sealed class State
    {
        public State(long nowMilliseconds)
        {
            LastObservedMilliseconds = nowMilliseconds;
            WasActive = true;
        }
        public long LastObservedMilliseconds { get; set; }
        public long ActiveMilliseconds { get; set; }
        public long? OutsideSinceMilliseconds { get; set; }
        public bool WasActive { get; set; }
    }

    private readonly Dictionary<int, State> _states = new();

    public BlockerUpdate Update(int playerId, bool active, long nowMilliseconds, int graceMilliseconds, int resetMilliseconds)
    {
        if (!_states.TryGetValue(playerId, out State? state))
        {
            if (!active)
            {
                return default;
            }

            state = new State(nowMilliseconds);
            _states[playerId] = state;
            return new BlockerUpdate(true, false, 0L, 0L, 0L);
        }

        long delta = Math.Max(0L, nowMilliseconds - state.LastObservedMilliseconds);
        state.LastObservedMilliseconds = Math.Max(state.LastObservedMilliseconds, nowMilliseconds);
        long grace = Math.Max(0, graceMilliseconds);

        if (active)
        {
            long beforeActive = state.ActiveMilliseconds;
            state.ActiveMilliseconds = SaturatingAdd(state.ActiveMilliseconds, state.WasActive ? delta : 0L);
            state.WasActive = true;
            state.OutsideSinceMilliseconds = null;
            long beforePunishable = Math.Max(0L, beforeActive - grace);
            long afterPunishable = Math.Max(0L, state.ActiveMilliseconds - grace);
            return new BlockerUpdate(true, false, beforePunishable, afterPunishable - beforePunishable, 0L);
        }

        state.WasActive = false;
        state.OutsideSinceMilliseconds ??= nowMilliseconds;
        long requiredReset = Math.Max(1, resetMilliseconds);
        long outsideFor = Math.Max(0L, nowMilliseconds - state.OutsideSinceMilliseconds.Value);
        if (outsideFor >= requiredReset)
        {
            _states.Remove(playerId);
            return new BlockerUpdate(false, true, 0L, 0L, 0L);
        }

        return new BlockerUpdate(true, false, Math.Max(0L, state.ActiveMilliseconds - grace), 0L, requiredReset - outsideFor);
    }

    public void Forget(int playerId) => _states.Remove(playerId);
    public void Clear() => _states.Clear();

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
