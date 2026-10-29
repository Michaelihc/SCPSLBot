using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Infrastructure;
using System.Collections.Generic;
using UnityEngine;

using SCPSLBot.Navigation;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Misc
{
    /// <summary>
    /// Navigation goals whose current path runs through a window the bot sees. Window colliders
    /// are cached per window; a sensing pass walks the path legs without allocating.
    /// </summary>
    internal class GlassObstacle : Belief<bool>
    {
        private readonly IBotNavigator navigator;
        private readonly Dictionary<BreakableWindow, Collider[]> windowColliders = new(ManagedReferenceComparer<BreakableWindow>.Instance);
        private readonly List<Vector3> removeBuffer = new();
        private int windowsGeneration = int.MinValue;

        public GlassObstacle(GlassSightSense glassSight, IBotNavigator navigator)
        {
            this.navigator = navigator;
            glassSight.OnSensedWindowsWithinSight += OnSensedWindowsWithinSight;
        }

        private void OnSensedWindowsWithinSight(BreakableWindow window)
        {
            if (window == null)
            {
                return;
            }

            var generation = NavigationSystem.Instance.MapGeneration;
            if (generation != windowsGeneration)
            {
                // Windows (and goals on their paths) belong to the previous map.
                windowsGeneration = generation;
                windowColliders.Clear();
                if (Windows.Count > 0)
                {
                    Windows.Clear();
                    Rays.Clear();
                    InvokeOnUpdate();
                }
            }

            if (!windowColliders.TryGetValue(window, out var colliders))
            {
                colliders = window.GetComponentsInChildren<Collider>();
                windowColliders.Add(window, colliders);
            }

            // Remove windows not obstructing paths anymore
            removeBuffer.Clear();
            foreach (var pair in Windows)
            {
                if (pair.Value == window && !HitsAny(colliders, Rays[pair.Key]))
                {
                    removeBuffer.Add(pair.Key);
                }
            }

            foreach (var goalPos in removeBuffer)
            {
                Remove(goalPos);
            }

            // Add window if obstructs current navigation path
            var pathOfPoints = navigator.PointsPath;
            for (var index = 1; index < pathOfPoints.Count; index++)
            {
                var ray = new Ray(pathOfPoints[index - 1], pathOfPoints[index] - pathOfPoints[index - 1]);
                if (HitsAny(colliders, ray))
                {
                    Add(window, pathOfPoints[pathOfPoints.Count - 1], ray);
                    break;
                }
            }
        }

        private static bool HitsAny(Collider[] colliders, Ray ray)
        {
            foreach (var collider in colliders)
            {
                if (collider != null && collider.Raycast(ray, out _, 1f))
                {
                    return true;
                }
            }

            return false;
        }

        private void Add(BreakableWindow window, Vector3 goalPos, Ray ray)
        {
            if (!Windows.TryGetValue(goalPos, out var known) || known != window)
            {
                Windows[goalPos] = window;
                Rays[goalPos] = ray;
                InvokeOnUpdate();
            }
        }

        private void Remove(Vector3 goalPos)
        {
            if (Windows.Remove(goalPos))
            {
                Rays.Remove(goalPos);
                InvokeOnUpdate();
            }
        }

        public bool IsAny => Windows.Count > 0;
        public Dictionary<Vector3, BreakableWindow> Windows { get; } = new();
        public Dictionary<Vector3, Ray> Rays { get; } = new();

        public bool Is(Vector3 goalPos)
        {
            return Windows.Count > 0 && Windows.ContainsKey(goalPos);
        }

        public override string ToString()
        {
            return $"{nameof(GlassObstacle)}: {Windows.Count}";
        }
    }
}
