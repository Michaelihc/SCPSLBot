using System;
using UnityEngine;

namespace SCPSLBot.Components
{
    internal class PerceptionComponent : MonoBehaviour
    {
        public event Action<Collider> TriggerEnter;
        public event Action<Collider> TriggerExit;

        public void OnTriggerEnter(Collider other)
        {
            TriggerEnter?.Invoke(other);
        }

        public void OnTriggerExit(Collider other)
        {
            TriggerExit?.Invoke(other);
        }
    }
}
