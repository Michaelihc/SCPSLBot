using Interactables.Interobjects.DoorUtils;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.AI.FirstPersonControl.Perception;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace SCPSLBot.AI.FirstPersonControl
{
    internal class FpcBotPerception : IDisposable
    {
        // Capacity must be reserved before constructing a sense. If List.Add had to grow after a
        // SightSense constructor succeeded and that growth OOMed, the just-created disposable
        // would never enter this ownership list and constructor rollback could not reach it.
        public List<ISense> Senses { get; } = new(8);
        public DoorsWithinSightSense DoorsSense { get; private set; }
        public ItemsInInventorySense InventorySense { get; private set; }

        #region Debugging
        public Dictionary<Collider, (int, string)> Layers { get; } = new Dictionary<Collider, (int, string)>();
        #endregion

        public FpcBotPerception(FpcBotPlayer fpcBotPlayer)
        {
            try
            {
                Senses.Add(new ItemsWithinSightSense(fpcBotPlayer));

                DoorsSense = new DoorsWithinSightSense(fpcBotPlayer);
                Senses.Add(DoorsSense);

                InventorySense = new ItemsInInventorySense(fpcBotPlayer);
                Senses.Add(InventorySense);

                Senses.Add(new GlassSightSense(fpcBotPlayer));
                Senses.Add(new LockersWithinSightSense(fpcBotPlayer));
                Senses.Add(new SpatialSense(fpcBotPlayer));
                Senses.Add(new RoomSightSense(fpcBotPlayer));
                Senses.Add(new InteractablesWithinSightSense(fpcBotPlayer));

                jobHandlesBuffer = new NativeArray<JobHandle>(Senses.Count, Allocator.Persistent);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void AddTriggerHandlers(PerceptionComponent perceptionComponent)
        {
            if (isDisposed)
            {
                return;
            }

            RemoveTriggerHandlers();
            this.perceptionComponent = perceptionComponent;
            perceptionComponent.TriggerEnter += OnTriggerEnter;
            perceptionComponent.TriggerExit += OnTriggerExit;
        }

        public void OnTriggerEnter(Collider other)
        {
            foreach (var sense in Senses)
            {
                sense.ProcessEnter(other);
            }
        }

        public void OnTriggerExit(Collider other)
        {
            foreach (var sense in Senses)
            {
                sense.ProcessExit(other);
            }
        }

        private readonly List<IEnumerator<JobHandle>> processSensesEnumerators = new(8);

        public IEnumerator<JobHandle> Update()
        {
            if (isDisposed)
            {
                yield break;
            }

            var jobHandlesCount = 0;
            try
            {
                var sensesCount = Senses.Count;

                processSensesEnumerators.Clear();
                foreach (var sense in Senses)
                {
                    processSensesEnumerators.Add(sense.ProcessSensibility());
                }

                var completedCount = 0;
                while (completedCount < sensesCount)
                {
                    completedCount = 0;
                    jobHandlesCount = 0;
                    for (int i = 0; i < sensesCount; i++)
                    {
                        var processSenses = processSensesEnumerators[i];
                        if (processSenses.MoveNext())
                        {
                            jobHandlesBuffer[jobHandlesCount] = processSenses.Current;
                            jobHandlesCount++;
                        }
                        else
                        {
                            completedCount++;
                        }
                    }

                    if (jobHandlesCount > 0)
                    {
                        var jobHandles = jobHandlesBuffer.GetSubArray(0, jobHandlesCount);
                        yield return JobHandle.CombineDependencies(jobHandles);
                        jobHandlesCount = 0;
                    }
                }

                Profiler.BeginSample($"{nameof(FpcBotPerception)}.ProcessSensedItems");
                try
                {
                    foreach (var sense in Senses)
                    {
                        sense.ProcessSensedItems();
                    }
                }
                finally
                {
                    Profiler.EndSample();
                }
            }
            finally
            {
                if (jobHandlesCount > 0)
                {
                    try
                    {
                        var outstandingJobs = jobHandlesBuffer.GetSubArray(0, jobHandlesCount);
                        JobHandle.CompleteAll(outstandingJobs);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }

                foreach (var processSense in processSensesEnumerators)
                {
                    try
                    {
                        processSense.Dispose();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }

                processSensesEnumerators.Clear();
            }
        }

        public IEnumerable<DoorVariant> GetDoorsOnPath(IEnumerable<Vector3> pathOfPoints)
        {
            var rays = pathOfPoints.Zip(pathOfPoints.Skip(1), (point, nextPoint) => new Ray(point, nextPoint - point));

            var doorsOnPath = rays
                .Select(ray => DoorsSense.DoorsWithinSight
                    .FirstOrDefault(door => door.GetComponentsInChildren<Collider>()
                        .Any(collider => collider.Raycast(ray, out _, 1f))))
                .Where(d => d != null);

            return doorsOnPath;
        }

        public T GetSense<T>() where T : class
        {
            return Senses.Find(s => s is T) as T;
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            RemoveTriggerHandlers();

            foreach (var processSense in processSensesEnumerators)
            {
                try
                {
                    (processSense as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            processSensesEnumerators.Clear();
            foreach (var sense in Senses)
            {
                try
                {
                    (sense as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            Senses.Clear();
            Layers.Clear();
            if (jobHandlesBuffer.IsCreated)
            {
                jobHandlesBuffer.Dispose();
            }
        }

        private void RemoveTriggerHandlers()
        {
            if (perceptionComponent == null)
            {
                return;
            }

            perceptionComponent.TriggerEnter -= OnTriggerEnter;
            perceptionComponent.TriggerExit -= OnTriggerExit;
            perceptionComponent = null;
        }

        private NativeArray<JobHandle> jobHandlesBuffer;
        private PerceptionComponent perceptionComponent;
        private bool isDisposed;
    }
}
