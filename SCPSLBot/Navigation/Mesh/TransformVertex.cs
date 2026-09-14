using System;
using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal record struct TransformVertex(Vertex Local, Transform Transform)
    {
        public readonly Vector3 Position => Transform.TransformPoint(Local.Position);

        [Obsolete]
        public static bool operator ==(TransformVertex? left, TransformVertex right)
        {
            throw new NotSupportedException();
        }
        [Obsolete]
        public static bool operator !=(TransformVertex? left, TransformVertex right)
        {
            throw new NotSupportedException();
        }
    }
}
