using Interactables;
using MapGeneration;
using Scp914;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    /// <summary>
    /// Where SCP-914, its chambers and its knobs are. Like the zone borders this is map knowledge
    /// every player has, so it is read from the controller as soon as the map exists.
    /// </summary>
    internal class Scp914Location : Location
    {
        private int generation = int.MinValue;

        public Scp914Location(RoomSightSense roomSightSense)
        {
            roomSightSense.OnSensedRoomWithin += OnSensedRoomWithin;
        }

        private void OnSensedRoomWithin(RoomIdentifier roomWithin)
        {
            var current = Navigation.NavigationSystem.Instance.MapGeneration;
            if (current != generation && Positions.Count > 0)
            {
                IntakeChamberPosition = null;
                OutakeChamberPosition = null;
                ControlsPosition = null;
                SettingKnobPosition = null;
                StartKnobPosition = null;
                SettingKnob = null;
                StartKnob = null;
                SetPositions(System.Array.Empty<Vector3>());
            }

            generation = current;
            if (Positions.Count == 0)
            {
                this.Update(Scp914Controller.Singleton);
            }
        }

        public Vector3? IntakeChamberPosition { get; private set; }
        public Vector3? OutakeChamberPosition { get; private set; }
        public Vector3? ControlsPosition { get; private set; }
        public Vector3? SettingKnobPosition { get; private set; }
        public Vector3? StartKnobPosition { get; private set; }
        public InteractableCollider SettingKnob { get; private set; }
        public InteractableCollider StartKnob { get; private set; }

        private void Update(Scp914Controller controller)
        {
            if (controller == null
                || !InteractableCollider.AllInstances.TryGetValue(controller, out var colliders)
                || !colliders.TryGetValue((byte)Scp914InteractCode.ChangeMode, out var changeMode)
                || !colliders.TryGetValue((byte)Scp914InteractCode.Activate, out var activate)
                || changeMode == null
                || activate == null)
            {
                return;
            }

            this.IntakeChamberPosition = controller.IntakeChamber.position;
            this.OutakeChamberPosition = controller.OutputChamber.position;
            this.SettingKnobPosition = changeMode.GetComponent<Collider>().bounds.center;
            this.StartKnobPosition = activate.GetComponent<Collider>().bounds.center;
            this.ControlsPosition = this.StartKnobPosition;
            this.SettingKnob = changeMode;
            this.StartKnob = activate;
            this.AddPosition(controller.transform.position);
        }

        public override string ToString()
        {
            return $"{nameof(Scp914Location)}: {Positions.Count > 0}";
        }
    }
}
