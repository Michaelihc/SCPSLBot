#nullable enable
using System;
using System.Diagnostics;
using System.Threading;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>Optional read-only main-thread navigation timing observations; no samples are retained.</summary>
    public static class RuntimeNavigationTiming
    {
        public const int MaximumObservers = 4;
        private static readonly object Sync = new();
        private static Action<string, int, long, double, int, bool>? observers;
        private static int epoch;
        private static int failedObservers;

        /// <summary>
        /// Operation, Unity frame count, Stopwatch start timestamp, elapsed milliseconds, source count,
        /// and source-hash change. SourceCollection is nested within ReconcileStart; do not add them.
        /// ReconcileStart/Finish are synchronous work only, excluding asynchronous bake wait time.
        /// Handlers must remain short. Throwing handlers are removed without affecting navigation.
        /// Subscriptions are limited to four and cleared on map restart or plugin termination.
        /// </summary>
        public static event Action<string, int, long, double, int, bool>? Sampled
        {
            add
            {
                if (value == null) return;
                lock (Sync)
                {
                    int count = observers?.GetInvocationList().Length ?? 0;
                    if (count + value.GetInvocationList().Length > MaximumObservers)
                        throw new InvalidOperationException("Navigation timing observer limit reached.");
                    observers += value;
                }
            }
            remove { lock (Sync) { observers -= value; } }
        }

        public static int FailedObservers => Volatile.Read(ref failedObservers);

        internal readonly struct Observation
        {
            internal readonly Action<string, int, long, double, int, bool>? Observers;
            internal readonly int Epoch;
            internal readonly string Operation;
            internal readonly int FrameCount;
            internal readonly long StartTimestamp;

            internal Observation(Action<string, int, long, double, int, bool> handlers, int generation, string operation, int frame)
            {
                Observers = handlers;
                Epoch = generation;
                Operation = operation;
                FrameCount = frame;
                StartTimestamp = Stopwatch.GetTimestamp();
            }

            internal void Complete(int sourceCount, bool changed) => Publish(this, sourceCount, changed);
        }

        internal static Observation Begin(string operation, int frameCount)
        {
            // The common unobserved path allocates nothing and never reads the timing clock.
            if (Volatile.Read(ref observers) == null) return default;
            lock (Sync)
            {
                return observers == null ? default : new Observation(observers, epoch, operation, frameCount);
            }
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                observers = null;
                epoch = unchecked(epoch + 1);
                failedObservers = 0;
            }
        }

        private static void Publish(Observation sample, int sourceCount, bool changed)
        {
            if (sample.Observers == null || Volatile.Read(ref observers) == null || sample.Epoch != Volatile.Read(ref epoch)) return;
            double elapsed = (Stopwatch.GetTimestamp() - sample.StartTimestamp) * 1000d / Stopwatch.Frequency;
            foreach (Action<string, int, long, double, int, bool> handler in sample.Observers.GetInvocationList())
            {
                lock (Sync)
                {
                    if (sample.Epoch != epoch || observers == null) return;
                    bool present = false;
                    foreach (Delegate active in observers.GetInvocationList())
                        if (active.Equals(handler)) { present = true; break; }
                    if (!present) continue;
                }

                try { handler(sample.Operation, sample.FrameCount, sample.StartTimestamp, elapsed, sourceCount, changed); }
                catch
                {
                    lock (Sync)
                    {
                        if (sample.Epoch != epoch) return;
                        observers -= handler;
                        failedObservers = unchecked(failedObservers + 1);
                    }
                }
            }
        }
    }
}
