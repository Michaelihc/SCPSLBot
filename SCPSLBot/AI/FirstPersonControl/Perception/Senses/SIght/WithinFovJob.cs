using SCPSLBot.Components;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Perception.Senses.Sight
{
    internal struct WithinFovJob : IJobFor
    {
        [ReadOnly] public Vector3 Origin;
        [ReadOnly] public Vector3 Direction;
        [ReadOnly] public NativeArray<ColliderData> ColliderDatas;

        [WriteOnly] public NativeArray<bool> IsWithinFov;

        public void Execute(int index)
        {
            // This sense intentionally uses a 180-degree forward hemisphere. The sign of the
            // dot product is sufficient and avoids normalization plus Vector3.Angle's acos.
            IsWithinFov[index] = Vector3.Dot(Direction, ColliderDatas[index].Center - Origin) >= 0f;
        }
    }
}
