using LabApi.Events.Arguments.Scp914Events;
using LabApi.Events.Handlers;
using MapGeneration;
using MEC;
using Scp914;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    internal class Scp914RunningOnSetting : Belief<Scp914KnobSetting?>, IDisposable
    {
        private readonly RoomSightSense roomSightSense;
        private CoroutineHandle activationHandle;
        private int activationGeneration;
        private bool isDisposed;

        public Scp914RunningOnSetting(RoomSightSense roomSightSense)
        {
            this.roomSightSense = roomSightSense;

            Scp914Events.Activated += OnActivateEvent;
        }

        public void OnActivateEvent(Scp914ActivatedEventArgs args)
        {
            if (isDisposed)
            {
                return;
            }

            var roomWithin = this.roomSightSense.RoomWithin;
            if (!roomWithin || roomWithin.Name != RoomName.Lcz914)
            {
                return;
            }

            this.Update(args.KnobSetting);

            activationGeneration++;
            if (activationHandle.IsRunning)
            {
                Timing.KillCoroutines(activationHandle);
            }

            activationHandle = Timing.RunCoroutine(Scp914RunningCoroutine(activationGeneration));
        }

        private IEnumerator<float> Scp914RunningCoroutine(int generation)
        {
            yield return Timing.WaitForSeconds(10f);
            if (isDisposed || generation != activationGeneration)
            {
                yield break;
            }

            this.ItemsTransformedTime = Time.time;

            yield return Timing.WaitForSeconds(5f);
            if (isDisposed || generation != activationGeneration)
            {
                yield break;
            }

            this.Update(null);
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            activationGeneration++;
            Scp914Events.Activated -= OnActivateEvent;

            if (activationHandle.IsRunning)
            {
                try
                {
                    Timing.KillCoroutines(activationHandle);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        public Scp914KnobSetting? RunningKnobSetting { get; private set; }
        public float? ItemsTransformedTime { get; private set; }

        private void Update(Scp914KnobSetting? newSetting)
        {
            if (newSetting != this.RunningKnobSetting)
            {
                this.RunningKnobSetting = newSetting;
                this.InvokeOnUpdate();
            }
        }

        public override string ToString()
        {
            return $"{nameof(Scp914RunningOnSetting)}: {this.RunningKnobSetting}";
        }
    }
}
