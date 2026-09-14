using System.Collections.Generic;

namespace SCPSLBot.Navigation.Policy
{
    /// <summary>
    /// Maps every distinct keycard-door permission class found on the map to its own Unity navmesh
    /// area, and turns a bot's permissions into the area mask it may plan through. Pure (engine
    /// free) so the routing rule is unit-testable: a bot without the card never receives a plan
    /// through that door class, a bot holding it does. Permission flags are the raw ushort values
    /// of the native DoorPermissionFlags enum.
    /// </summary>
    internal sealed class DoorAreaRegistry
    {
        public const int WalkableArea = 0;
        public const int NotWalkableArea = 1;
        public const int JumpArea = 2;
        public const int ElevatorArea = 3;
        public const int FirstDoorArea = 4;
        public const int MaxAreas = 32;
        public const ushort ScpOverrideFlag = 0x400;

        public readonly struct DoorClass
        {
            public DoorClass(ushort requiredPermissions, bool requireAll)
            {
                RequiredPermissions = requiredPermissions;
                RequireAll = requireAll;
            }

            public ushort RequiredPermissions { get; }
            public bool RequireAll { get; }

            public bool CanOpen(ushort permissions)
            {
                if (RequiredPermissions == 0)
                {
                    return true;
                }

                return RequireAll
                    ? (permissions & RequiredPermissions) == RequiredPermissions
                    : (permissions & RequiredPermissions) != 0;
            }

            public override string ToString() => $"{RequiredPermissions:X}{(RequireAll ? "/all" : "/any")}";
        }

        private readonly Dictionary<(ushort, bool), int> areasByClass = new();
        private readonly List<DoorClass> classesByArea = new();

        /// <summary>Door classes that no longer fit in the 32-area budget; they stay walkable.</summary>
        public int OverflowClasses { get; private set; }

        public int ClassCount => classesByArea.Count;

        public IReadOnlyList<DoorClass> Classes => classesByArea;

        public void Clear()
        {
            areasByClass.Clear();
            classesByArea.Clear();
            OverflowClasses = 0;
        }

        /// <summary>
        /// Returns the area for a door with the given policy: a dedicated area per distinct class,
        /// <see cref="WalkableArea"/> for doors without permissions or once the budget is exhausted.
        /// </summary>
        public int GetOrAddArea(ushort requiredPermissions, bool requireAll)
        {
            if (requiredPermissions == 0)
            {
                return WalkableArea;
            }

            var key = (requiredPermissions, requireAll);
            if (areasByClass.TryGetValue(key, out var area))
            {
                return area;
            }

            var next = FirstDoorArea + classesByArea.Count;
            if (next >= MaxAreas)
            {
                OverflowClasses++;
                return WalkableArea;
            }

            areasByClass[key] = next;
            classesByArea.Add(new DoorClass(requiredPermissions, requireAll));
            return next;
        }

        /// <summary>The area mask a bot with the given permissions may plan through.</summary>
        public int BuildAreaMask(ushort permissions, bool bypassAll = false)
        {
            var mask = (1 << WalkableArea) | (1 << JumpArea) | (1 << ElevatorArea);
            for (var index = 0; index < classesByArea.Count; index++)
            {
                if (bypassAll || classesByArea[index].CanOpen(permissions))
                {
                    mask |= 1 << (FirstDoorArea + index);
                }
            }

            return mask;
        }

        /// <summary>Mask that allows every area this registry could ever assign (diagnostics).</summary>
        public static int AllAreasMask => ~0;

        public bool TryGetClass(int area, out DoorClass doorClass)
        {
            var index = area - FirstDoorArea;
            if (index < 0 || index >= classesByArea.Count)
            {
                doorClass = default;
                return false;
            }

            doorClass = classesByArea[index];
            return true;
        }
    }
}
