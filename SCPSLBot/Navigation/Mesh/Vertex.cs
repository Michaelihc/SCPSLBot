using UnityEngine;

namespace SCPSLBot.Navigation.Mesh
{
    internal class Vertex
    {
        public Vector3 Position { get; set; }

        public Vertex(Vector3 position)
        {
            Position = position;
        }
    }
}
