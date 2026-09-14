using System;
using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal record struct TransformEdge(TransformVertex From, TransformVertex To, Transform Transform)
    {
        public readonly Edge Local => new (From.Local, To.Local);

        public TransformEdge((Vertex From, Vertex To, Transform Tranform) tuple)
            : this(new(tuple.From, tuple.Tranform), new(tuple.To, tuple.Tranform), tuple.Tranform)
        { }

        public TransformEdge(Edge Edge, Transform Tranform)
            : this(new(Edge.From, Tranform), new(Edge.To, Tranform), Tranform)
        { }

        [Obsolete]
        public static bool operator ==(TransformEdge? left, TransformEdge right)
        {
            throw new NotSupportedException();
        }
        [Obsolete]
        public static bool operator !=(TransformEdge? left, TransformEdge right)
        {
            throw new NotSupportedException();
        }
    }
}
