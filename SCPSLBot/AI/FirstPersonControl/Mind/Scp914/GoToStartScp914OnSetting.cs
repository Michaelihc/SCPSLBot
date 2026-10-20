using Scp914;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    /// <summary>Walks to the SCP-914 controls, turns the knob to <see cref="KnobSetting"/> and starts the machine.</summary>
    internal class GoToStartScp914OnSetting : GoTo<Scp914Location>
    {
        private const float ControlsArrivalMeters = 1f;
        private const float InteractRetrySeconds = 0.6f;

        public readonly Scp914KnobSetting KnobSetting;
        public GoToStartScp914OnSetting(Scp914KnobSetting knobSetting, FpcBotPlayer botPlayer) : base(botPlayer)
        {
            this.KnobSetting = knobSetting;
        }

        private Scp914Controls scp914Controls;
        private Scp914RunningOnSetting runningOnSetting;
        private float nextInteractAt;
        private bool activated;

        public override void SetEnabledByBeliefs(FpcMind fpcMind)
        {
            scp914Controls = fpcMind.GetBelief<Scp914Controls>();
            runningOnSetting = fpcMind.GetBelief<Scp914RunningOnSetting>();

            base.SetEnabledByBeliefs(fpcMind);
        }

        public override void SetImpactsBeliefs(FpcMind fpcMind)
        {
            fpcMind.ActionImpacts<Scp914RunningOnSetting, Scp914KnobSetting?>(this, b => KnobSetting);
        }

        public override float Weight { get; } = 1.5f;   // "average" effort to target knob setting

        public override float Cost => (HasTarget() && location.ControlsPosition.HasValue
            ? Vector3.Distance(location.ControlsPosition.Value, botPlayer.CameraPosition)
            : UnknownDistanceMeters) * Weight;

        public override void Reset()
        {
            nextInteractAt = 0f;
            activated = false;
            base.Reset();
        }

        protected override ActionStatus TickTowardsTarget()
        {
            if (!location.ControlsPosition.HasValue)
            {
                return ActionStatus.Failed;
            }

            if (activated && runningOnSetting.RunningKnobSetting == KnobSetting)
            {
                return ActionStatus.Completed;
            }

            var playerPosition = this.botPlayer.PlayerPosition;
            var controlsPosition = this.location.ControlsPosition.Value;

            if (Vector3.Distance(playerPosition, controlsPosition) > ControlsArrivalMeters)
            {
                this.botPlayer.MoveToPosition(controlsPosition);
            }
            else
            {
                this.botPlayer.Locomotion.Stop();
            }

            if (Time.time < nextInteractAt)
            {
                return ActionStatus.Running;
            }

            var controller = Scp914Controller.Singleton;
            var currentSetting = controller != null ? controller.KnobSetting : this.scp914Controls.KnobSetting;
            if (currentSetting != this.KnobSetting)
            {
                var settingKnob = this.location.SettingKnob ? this.location.SettingKnob : this.scp914Controls.SettingKnob;
                if (settingKnob && this.botPlayer.Interact(settingKnob))
                {
                    nextInteractAt = Time.time + InteractRetrySeconds;
                }
                else
                {
                    this.botPlayer.LookToPosition(this.location.SettingKnobPosition!.Value);
                }

                return ActionStatus.Running;
            }

            var startKnob = this.location.StartKnob ? this.location.StartKnob : this.scp914Controls.StartKnob;
            if (startKnob && this.botPlayer.Interact(startKnob))
            {
                activated = true;
                nextInteractAt = Time.time + InteractRetrySeconds;
            }
            else
            {
                this.botPlayer.LookToPosition(this.location.StartKnobPosition!.Value);
            }

            return ActionStatus.Running;
        }

        public override string ToString()
        {
            return $"{nameof(GoToStartScp914OnSetting)}({KnobSetting})";
        }
    }
}
