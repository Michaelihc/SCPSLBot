using MEC;
using SCPSLBot.Components;
using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight
{
    internal abstract class SightSense<TComponent> : SightSense, ISense where TComponent : Component
    {
        private readonly struct TrackedCollider
        {
            public TrackedCollider(Collider collider, TComponent component)
            {
                Collider = collider;
                Component = component;
            }

            public Collider Collider { get; }
            public TComponent Component { get; }
        }

        public HashSet<TComponent> ComponentsWithinSight { get; } = new();

        protected abstract LayerMask LayerMask { get; }

        // Collider position is mutable state, not identity. Keying the old dictionary by
        // (instance id, bounds center) meant moving colliders could never be removed on exit.
        private readonly Dictionary<int, TrackedCollider> trackedColliders = new();
        private readonly List<int> staleColliderIds = new();

        protected SightSense(FpcBotPlayer botPlayer) : base(botPlayer)
        {
        }

        protected virtual TComponent GetComponent(Collider collider)
        {
            return collider.GetComponentInParent<TComponent>();
        }

        protected virtual void AddColliderDatas(Collider triggeringCollider, TComponent component)
        {
            TrackCollider(triggeringCollider, component);
        }

        protected virtual void RemoveColliderDatas(Collider triggeringCollider, TComponent component)
        {
            UntrackCollider(triggeringCollider);
        }

        protected void TrackCollider(Collider collider, TComponent component)
        {
            if (collider == null || component == null)
            {
                return;
            }

            var instanceId = collider.GetInstanceID();
            if (!trackedColliders.ContainsKey(instanceId))
            {
                AdjustTrackedColliderCount(1);
            }

            trackedColliders[instanceId] = new TrackedCollider(collider, component);
        }

        protected void UntrackCollider(Collider collider)
        {
            if (collider == null)
            {
                return;
            }

            if (trackedColliders.Remove(collider.GetInstanceID()))
            {
                AdjustTrackedColliderCount(-1);
            }
        }

        public void ProcessEnter(Collider triggeringCollider)
        {
            if (IsDisposed
                || triggeringCollider == null
                || (LayerMask & (1 << triggeringCollider.gameObject.layer)) == 0)
            {
                return;
            }

            var component = GetComponent(triggeringCollider);
            if (component != null)
            {
                AddColliderDatas(triggeringCollider, component);
            }
        }

        public void ProcessExit(Collider triggeringCollider)
        {
            if (IsDisposed || triggeringCollider == null)
            {
                return;
            }

            // Always attempt removal. A collider can change layer or lose its component before
            // Unity delivers OnTriggerExit, and either used to leave a permanent candidate behind.
            RemoveColliderDatas(triggeringCollider, GetComponent(triggeringCollider));
        }

        public IEnumerator<JobHandle> ProcessSensibility()
        {
            if (IsDisposed)
            {
                yield break;
            }

            try
            {
                RefreshAndPruneTrackedColliders();
                if (trackedColliders.Count == 0)
                {
                    ComponentsWithinSight.Clear();
                    yield break;
                }

                yield return ScheduleWithinFov(trackedColliders.Values);
                CompleteScheduledJob();

                var raycastCount = NumRaycasts;
                if (raycastCount > 0)
                {
                    yield return ScheduleRaycasts(raycastCount);
                    CompleteScheduledJob();
                }

                UpdateComponentsWithinSight(raycastCount);
            }
            finally
            {
                CompleteScheduledJob();
            }
        }

        private void RefreshAndPruneTrackedColliders()
        {
            staleColliderIds.Clear();
            foreach (var entry in trackedColliders)
            {
                var candidate = entry.Value;
                if (candidate.Collider == null || candidate.Component == null)
                {
                    staleColliderIds.Add(entry.Key);
                }
            }

            foreach (var instanceId in staleColliderIds)
            {
                if (trackedColliders.Remove(instanceId))
                {
                    AdjustTrackedColliderCount(-1);
                }
            }
        }

        private JobHandle ScheduleWithinFov(ICollection<TrackedCollider> candidates)
        {
            EnsureRaycastBufferCapacity(candidates.Count);

            var colliderIndex = 0;
            foreach (var candidate in candidates)
            {
                colliderDatasBuffer[colliderIndex] = new ColliderData(
                    candidate.Collider.GetInstanceID(),
                    candidate.Collider.bounds.center);
                colliderIndex++;
            }

            var withinFovJob = new WithinFovJob
            {
                Origin = BotPlayer.CameraPosition,
                Direction = BotPlayer.CameraForward,
                ColliderDatas = colliderDatasBuffer,
                IsWithinFov = withinFovResultsBuffer,
            };

            var withinFovHandle = withinFovJob.ScheduleParallel(colliderIndex, 8, default);
            var filterWithinFovJob = new FilterWithinFovResultsJob
            {
                CameraPosition = BotPlayer.CameraPosition,
                ColliderDatas = colliderDatasBuffer,
                IsWithinFov = withinFovResultsBuffer,
                CollisionMask = CollisionLayerMask,
                ColliderCount = colliderIndex,
                RaycastCommands = raycastCommandsBuffer,
                WithinFovColliderDatas = withinFovColliderDatasBuffer,
                NumRaycasts = numRaycastsBuffer,
            };

            try
            {
                return TrackJob(filterWithinFovJob.Schedule(withinFovHandle));
            }
            catch
            {
                withinFovHandle.Complete();
                throw;
            }
        }

        private JobHandle ScheduleRaycasts(int raycastCount)
        {
            var raycastCommands = raycastCommandsBuffer.GetSubArray(0, raycastCount);
            var raycastResults = raycastResultsBuffer.GetSubArray(0, raycastCount * MaxHitsPerRaycast);
            return TrackJob(RaycastCommand.ScheduleBatch(
                raycastCommands,
                raycastResults,
                MinCommandsPerRaycastJob,
                MaxHitsPerRaycast,
                default));
        }

        private void UpdateComponentsWithinSight(int raycastCount)
        {
            ComponentsWithinSight.Clear();
            for (var raycastIndex = 0; raycastIndex < raycastCount; raycastIndex++)
            {
                var colliderData = withinFovColliderDatasBuffer[raycastIndex];
                var hit = raycastResultsBuffer[raycastIndex * MaxHitsPerRaycast];
                if (hit.colliderInstanceID == colliderData.InstanceId
                    && trackedColliders.TryGetValue(colliderData.InstanceId, out var candidate)
                    && candidate.Component != null)
                {
                    ComponentsWithinSight.Add(candidate.Component);
                }
            }
        }

        protected override void DisposeManagedResources()
        {
            AdjustTrackedColliderCount(-trackedColliders.Count);
            trackedColliders.Clear();
            staleColliderIds.Clear();
            ComponentsWithinSight.Clear();
        }
    }

    internal abstract class SightSense : IDisposable
    {
        protected const int MaxHitsPerRaycast = 1;
        protected const int MinCommandsPerRaycastJob = 32;

        protected static readonly LayerMask CollisionLayerMask =
            LayerMask.GetMask("Default", "Door", "InteractableNoPlayerCollision", "Glass");

        protected NativeArray<ColliderData> colliderDatasBuffer;
        protected NativeArray<bool> withinFovResultsBuffer;
        protected NativeArray<ColliderData> withinFovColliderDatasBuffer;
        protected NativeArray<int> numRaycastsBuffer;
        protected NativeArray<RaycastCommand> raycastCommandsBuffer;
        protected NativeArray<RaycastHit> raycastResultsBuffer;

        private JobHandle scheduledJob;
        private bool hasScheduledJob;
        private bool isDisposing;
        private bool isDisposed;
        private bool cleanupComplete;
        private bool managedResourcesDisposed;

        private static int activeSenseCount;
        private static int totalRaycastCapacity;
        private static int totalTrackedColliderCount;
        private static readonly List<SightSense> PendingDisposals = new();
        private static float nextPendingDisposalRetryTime;
        private static bool pendingDisposalWorkerRunning;

        protected SightSense(FpcBotPlayer botPlayer)
        {
            BotPlayer = botPlayer;
            Interlocked.Increment(ref activeSenseCount);
            try
            {
                // Start the retry owner while the plugin and MEC scheduler are known to be live,
                // before this sense can acquire any native buffers. Failed startup therefore
                // aborts bot construction instead of discovering too late during plugin teardown
                // that a pending disposal has no owner.
                EnsurePendingDisposalWorker();
            }
            catch
            {
                Interlocked.Decrement(ref activeSenseCount);
                throw;
            }
        }

        public event Action OnAfterSightSensing;

        protected FpcBotPlayer BotPlayer { get; }
        protected bool IsDisposed => isDisposed || isDisposing;
        protected int NumRaycasts => numRaycastsBuffer.IsCreated ? numRaycastsBuffer[0] : 0;

        internal static SightSenseDiagnostics Diagnostics => new(
            Volatile.Read(ref activeSenseCount),
            Volatile.Read(ref totalRaycastCapacity),
            Volatile.Read(ref totalTrackedColliderCount));

        internal static void RetryPendingDisposals(bool force = false)
        {
            lock (PendingDisposals)
            {
                if (PendingDisposals.Count == 0
                    || !force && Time.realtimeSinceStartup < nextPendingDisposalRetryTime)
                {
                    return;
                }

                nextPendingDisposalRetryTime = Time.realtimeSinceStartup + 1f;
                for (var pendingIndex = PendingDisposals.Count - 1; pendingIndex >= 0; pendingIndex--)
                {
                    if (PendingDisposals[pendingIndex].TryReleasePendingNativeResources())
                    {
                        PendingDisposals.RemoveAt(pendingIndex);
                    }
                }
            }
        }

        protected static void AdjustTrackedColliderCount(int delta)
        {
            if (delta != 0)
            {
                Interlocked.Add(ref totalTrackedColliderCount, delta);
            }
        }

        public abstract void ProcessSightSensedItems();

        public void ProcessSensedItems()
        {
            if (IsDisposed)
            {
                return;
            }

            OnAfterSightSensing?.Invoke();
            ProcessSightSensedItems();
        }

        public bool IsPositionObstructed(Vector3 targetPosition) => IsPositionObstructed(targetPosition, out _);

        public bool IsPositionObstructed(Vector3 targetPosition, out RaycastHit outObstructtionHit)
        {
            var isObstructed = Physics.Linecast(
                BotPlayer.CameraPosition,
                targetPosition,
                out var hit,
                CollisionLayerMask);

            outObstructtionHit = isObstructed ? hit : default;
            return isObstructed;
        }

        public bool IsPositionWithinFov(Vector3 targetPosition)
        {
            return IsWithinFov(BotPlayer.CameraPosition, BotPlayer.CameraForward, targetPosition);
        }

        public float GetDistanceToPosition(Vector3 targetPosition)
        {
            return Vector3.Distance(targetPosition, BotPlayer.CameraPosition);
        }

        protected JobHandle TrackJob(JobHandle handle)
        {
            scheduledJob = handle;
            hasScheduledJob = true;
            return handle;
        }

        protected void CompleteScheduledJob()
        {
            if (!hasScheduledJob)
            {
                return;
            }

            try
            {
                scheduledJob.Complete();
            }
            finally
            {
                scheduledJob = default;
                hasScheduledJob = false;
            }
        }

        protected void EnsureRaycastBufferCapacity(int requiredCapacity)
        {
            if (requiredCapacity <= 0)
            {
                return;
            }

            var currentCapacity = raycastCommandsBuffer.IsCreated ? raycastCommandsBuffer.Length : 0;
            var buffersAreComplete = currentCapacity > 0
                                     && colliderDatasBuffer.IsCreated
                                     && colliderDatasBuffer.Length == currentCapacity
                                     && withinFovResultsBuffer.IsCreated
                                     && withinFovResultsBuffer.Length == currentCapacity
                                     && withinFovColliderDatasBuffer.IsCreated
                                     && withinFovColliderDatasBuffer.Length == currentCapacity
                                     && numRaycastsBuffer.IsCreated
                                     && numRaycastsBuffer.Length == 1
                                     && raycastResultsBuffer.IsCreated
                                     && raycastResultsBuffer.Length == currentCapacity * MaxHitsPerRaycast;
            if (buffersAreComplete && currentCapacity >= requiredCapacity)
            {
                return;
            }

            CompleteScheduledJob();
            var newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, MinCommandsPerRaycastJob));
            if (!DisposeNativeBuffers())
            {
                throw new InvalidOperationException("Failed to release the previous sight buffers before resizing.");
            }

            NativeArray<ColliderData> newColliderDatas = default;
            NativeArray<bool> newWithinFovResults = default;
            NativeArray<ColliderData> newWithinFovColliderDatas = default;
            NativeArray<int> newNumRaycasts = default;
            NativeArray<RaycastCommand> newRaycastCommands = default;
            NativeArray<RaycastHit> newRaycastResults = default;
            try
            {
                newColliderDatas = new NativeArray<ColliderData>(newCapacity, Allocator.Persistent);
                newWithinFovResults = new NativeArray<bool>(newCapacity, Allocator.Persistent);
                newWithinFovColliderDatas = new NativeArray<ColliderData>(newCapacity, Allocator.Persistent);
                newNumRaycasts = new NativeArray<int>(1, Allocator.Persistent);
                newRaycastCommands = new NativeArray<RaycastCommand>(newCapacity, Allocator.Persistent);
                newRaycastResults = new NativeArray<RaycastHit>(newCapacity * MaxHitsPerRaycast, Allocator.Persistent);
            }
            catch
            {
                TryDisposeNativeArray(ref newColliderDatas);
                TryDisposeNativeArray(ref newWithinFovResults);
                TryDisposeNativeArray(ref newWithinFovColliderDatas);
                TryDisposeNativeArray(ref newNumRaycasts);
                TryDisposeNativeArray(ref newRaycastCommands);
                TryDisposeNativeArray(ref newRaycastResults);
                throw;
            }

            colliderDatasBuffer = newColliderDatas;
            withinFovResultsBuffer = newWithinFovResults;
            withinFovColliderDatasBuffer = newWithinFovColliderDatas;
            numRaycastsBuffer = newNumRaycasts;
            raycastCommandsBuffer = newRaycastCommands;
            raycastResultsBuffer = newRaycastResults;
            Interlocked.Add(ref totalRaycastCapacity, newCapacity);
        }

        protected static bool IsWithinFov(Transform transform, Transform targetTransform) =>
            IsWithinFov(transform.position, transform.forward, targetTransform.position);

        protected static bool IsWithinFov(Vector3 position, Vector3 forward, Vector3 targetPosition)
        {
            // The FOV is a 180-degree forward hemisphere. The dot-product sign fully determines
            // membership; Vector3.Angle repeated normalization and acos work for no added result.
            return Vector3.Dot(forward, targetPosition - position) >= 0f;
        }

        public void Dispose()
        {
            if (cleanupComplete || isDisposing)
            {
                return;
            }

            isDisposed = true;
            isDisposing = true;
            try
            {
                try
                {
                    CompleteScheduledJob();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }

                var nativeResourcesReleased = DisposeNativeBuffers();
                if (!managedResourcesDisposed)
                {
                    managedResourcesDisposed = true;
                    try
                    {
                        DisposeManagedResources();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }

                OnAfterSightSensing = null;
                if (nativeResourcesReleased)
                {
                    CompleteCleanup();
                }
                else
                {
                    EnqueuePendingDisposal(this);
                }
            }
            finally
            {
                isDisposing = false;
            }
        }

        private bool TryReleasePendingNativeResources()
        {
            if (cleanupComplete)
            {
                return true;
            }

            if (isDisposing)
            {
                return false;
            }

            isDisposing = true;
            try
            {
                try
                {
                    CompleteScheduledJob();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }

                if (!DisposeNativeBuffers())
                {
                    return false;
                }

                CompleteCleanup();
                return true;
            }
            finally
            {
                isDisposing = false;
            }
        }

        private static void EnqueuePendingDisposal(SightSense sense)
        {
            lock (PendingDisposals)
            {
                if (!PendingDisposals.Contains(sense))
                {
                    PendingDisposals.Add(sense);
                }
            }
        }

        private static void EnsurePendingDisposalWorker()
        {
            lock (PendingDisposals)
            {
                if (pendingDisposalWorkerRunning
                    || PendingDisposals.Count == 0 && Volatile.Read(ref activeSenseCount) == 0)
                {
                    return;
                }

                pendingDisposalWorkerRunning = true;
            }

            try
            {
                // This worker deliberately starts with the first live sense and is not owned by
                // BotManager. If native cleanup is temporarily rejected while the plugin is
                // terminating, the old assembly keeps retrying its own buffers after reload.
                Timing.RunCoroutine(RunPendingDisposalWorker());
            }
            catch
            {
                lock (PendingDisposals)
                {
                    pendingDisposalWorkerRunning = false;
                }

                throw;
            }
        }

        private static IEnumerator<float> RunPendingDisposalWorker()
        {
            var exitedCleanly = false;
            try
            {
                while (true)
                {
                    try
                    {
                        RetryPendingDisposals(force: true);
                    }
                    catch (Exception exception)
                    {
                        // A cleanup fault must not make the coroutine immediately restart from
                        // its finally block. Keep the same owner alive and back off one second.
                        Debug.LogException(exception);
                    }

                    bool stopWorker;
                    lock (PendingDisposals)
                    {
                        stopWorker = PendingDisposals.Count == 0
                                     && Volatile.Read(ref activeSenseCount) == 0;
                        if (stopWorker)
                        {
                            // Publish the stopped state while holding the same lock used by
                            // constructors. A racing constructor will then start the replacement
                            // itself instead of seeing a worker that is already committed to exit.
                            pendingDisposalWorkerRunning = false;
                            exitedCleanly = true;
                        }
                    }

                    if (stopWorker)
                    {
                        yield break;
                    }

                    yield return Timing.WaitForSeconds(1f);
                }
            }
            finally
            {
                if (!exitedCleanly)
                {
                    bool restartWorker;
                    lock (PendingDisposals)
                    {
                        pendingDisposalWorkerRunning = false;
                        restartWorker = PendingDisposals.Count > 0
                                        || Volatile.Read(ref activeSenseCount) > 0;
                    }

                    // No plugin lifecycle path cancels this unowned worker. This branch exists for
                    // an external MEC cancellation; delay before replacement so cancellation-time
                    // scheduler faults cannot recurse in the same frame.
                    if (restartWorker)
                    {
                        try
                        {
                            Timing.RunCoroutine(RestartPendingDisposalWorkerAfterDelay());
                        }
                        catch (Exception exception)
                        {
                            Debug.LogException(exception);
                        }
                    }
                }
            }
        }

        private static IEnumerator<float> RestartPendingDisposalWorkerAfterDelay()
        {
            while (true)
            {
                yield return Timing.WaitForSeconds(1f);

                bool workerStillNeeded;
                lock (PendingDisposals)
                {
                    workerStillNeeded = PendingDisposals.Count > 0
                                        || Volatile.Read(ref activeSenseCount) > 0;
                }

                if (!workerStillNeeded)
                {
                    yield break;
                }

                try
                {
                    EnsurePendingDisposalWorker();
                    yield break;
                }
                catch (Exception exception)
                {
                    // The helper itself already has a scheduler slot, so retain that owner and
                    // keep backing off until a replacement worker can be started.
                    Debug.LogException(exception);
                }
            }
        }

        private void CompleteCleanup()
        {
            if (cleanupComplete)
            {
                return;
            }

            cleanupComplete = true;
            Interlocked.Decrement(ref activeSenseCount);
        }

        protected virtual void DisposeManagedResources()
        {
        }

        private bool DisposeNativeBuffers()
        {
            var releasedCapacity = raycastCommandsBuffer.IsCreated ? raycastCommandsBuffer.Length : 0;
            var success = TryDisposeNativeArray(ref colliderDatasBuffer)
                          & TryDisposeNativeArray(ref withinFovResultsBuffer)
                          & TryDisposeNativeArray(ref withinFovColliderDatasBuffer)
                          & TryDisposeNativeArray(ref numRaycastsBuffer)
                          & TryDisposeNativeArray(ref raycastCommandsBuffer)
                          & TryDisposeNativeArray(ref raycastResultsBuffer);
            var remainingCapacity = raycastCommandsBuffer.IsCreated ? raycastCommandsBuffer.Length : 0;
            if (releasedCapacity != remainingCapacity)
            {
                Interlocked.Add(ref totalRaycastCapacity, remainingCapacity - releasedCapacity);
            }

            return success;
        }

        private static bool TryDisposeNativeArray<T>(ref NativeArray<T> array) where T : struct
        {
            if (!array.IsCreated)
            {
                return true;
            }

            try
            {
                array.Dispose();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }
    }

    internal readonly struct SightSenseDiagnostics
    {
        public SightSenseDiagnostics(int activeSenseCount, int totalRaycastCapacity, int trackedColliderCount)
        {
            ActiveSenseCount = activeSenseCount;
            TotalRaycastCapacity = totalRaycastCapacity;
            TrackedColliderCount = trackedColliderCount;
        }

        public int ActiveSenseCount { get; }
        public int TotalRaycastCapacity { get; }
        public int TrackedColliderCount { get; }
    }
}
