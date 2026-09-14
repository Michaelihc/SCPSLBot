using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SCPSLBot.Navigation.Runtime
{
    /// <summary>
    /// Temporary carving obstacles placed where a bot reported a blocked crossing: the navmesh
    /// loses that spot for a while so the next plan prefers another route when one exists. This
    /// is what the authored backend's A* link penalties approximated.
    /// </summary>
    internal sealed class BlockedCrossingObstacles
    {
        private const float ObstacleRadius = 0.6f;
        private const float ObstacleHeight = 2f;

        private readonly List<(GameObject Root, float ExpiresAt)> obstacles = new();

        public int Count => obstacles.Count;

        public void Add(Vector3 position, float seconds)
        {
            Prune(Time.time);
            var root = new GameObject("SCPSLBot.BlockedCrossing")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            root.transform.position = position;
            var obstacle = root.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Capsule;
            obstacle.radius = ObstacleRadius;
            obstacle.height = ObstacleHeight;
            obstacle.center = Vector3.up * (ObstacleHeight * 0.5f - 0.3f);
            obstacle.carveOnlyStationary = false;
            obstacle.carving = true;
            obstacles.Add((root, Time.time + seconds));
        }

        public void Prune(float now)
        {
            for (var index = obstacles.Count - 1; index >= 0; index--)
            {
                var (root, expiresAt) = obstacles[index];
                if (root == null || now >= expiresAt)
                {
                    if (root != null)
                    {
                        Object.Destroy(root);
                    }

                    obstacles.RemoveAt(index);
                }
            }
        }

        public void Clear()
        {
            foreach (var (root, _) in obstacles)
            {
                if (root != null)
                {
                    Object.Destroy(root);
                }
            }

            obstacles.Clear();
        }
    }
}
