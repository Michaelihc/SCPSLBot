using System.ComponentModel;

namespace SCPSLBot.Navigation
{
    public enum NavigationBackend
    {
        /// <summary>Navmesh baked on the server at map load from the live collision geometry (Unity Recast).</summary>
        Runtime,
        /// <summary>Hand-authored cell mesh (navmesh.slnmf) plus runtime room fill and connector probes.</summary>
        Authored,
    }

    public sealed class NavigationConfig
    {
        [Description("Navigation backend: runtime bakes a navmesh from the live server geometry at map load (requires the patched server assets, see README); authored keeps the hand-authored cell mesh for one release as a fallback. A failed runtime bake falls back to authored for that map. / 导航后端：runtime 在地图加载时从实时碰撞几何烘焙导航网格（需要已打补丁的服务器资源，见 README）；authored 保留手工网格作为回退。")]
        public NavigationBackend Backend { get; set; } = NavigationBackend.Runtime;

        [Description("Seconds between navmesh reconciliations against the live geometry (only tiles whose sources changed are rebuilt, off the main thread). / 导航网格与实时几何对账的间隔（秒）。")]
        public float ReconcileIntervalSeconds { get; set; } = 5f;

        [Description("Navmesh voxel size in meters (agent radius / 4 by default). / 导航网格体素大小（米）。")]
        public float VoxelSize { get; set; } = 0.09f;

        [Description("Plan around keycard doors: each permission class becomes a navmesh area and bots only plan through doors their inventory can open. / 按门禁权限规划路径：机器人只会规划通过其钥匙卡能打开的门。")]
        public bool KeycardAreaRouting { get; set; } = true;

        [Description("Seconds a crossing stays carved out of the navmesh after a bot reported it blocked. / 机器人报告通道受阻后，该通道从导航网格中挖除的秒数。")]
        public float BlockedCrossingSeconds { get; set; } = 45f;

        internal void Normalize(System.Collections.Generic.ICollection<string> changes)
        {
            ReconcileIntervalSeconds = Clamp(nameof(ReconcileIntervalSeconds), ReconcileIntervalSeconds, 1f, 120f, changes);
            VoxelSize = Clamp(nameof(VoxelSize), VoxelSize, 0.05f, 0.3f, changes);
            BlockedCrossingSeconds = Clamp(nameof(BlockedCrossingSeconds), BlockedCrossingSeconds, 5f, 600f, changes);
        }

        private static float Clamp(string name, float value, float minimum, float maximum, System.Collections.Generic.ICollection<string> changes)
        {
            var original = value;
            value = System.Math.Max(minimum, System.Math.Min(maximum, value));
            if (System.Math.Abs(value - original) > 0.0001f)
            {
                changes.Add($"Navigation.{name} {original} -> {value}");
            }

            return value;
        }
    }
}
