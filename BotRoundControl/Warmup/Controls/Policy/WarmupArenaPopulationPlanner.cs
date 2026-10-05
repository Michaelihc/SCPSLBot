#nullable enable

using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup.Controls;

public sealed class WarmupArenaPopulationRequest
{
    public int SurfacePlayers { get; set; }
    public int HeavyEntrancePlayers { get; set; }
    public int LightContainmentPlayers { get; set; }
    public int FallbackBotCount { get; set; } = 3;
    public int SurfaceBotCap { get; set; } = 6;
    public double SurfaceBotFactor { get; set; } = 1.2;
    public int HeavyEntranceBotCount { get; set; } = 2;
    public int LightContainmentScpBotCount { get; set; } = 1;
    public int TotalBotCap { get; set; } = 10;
    public string DefaultArenaId { get; set; } = "pvpve";
    public string FallbackRoleId { get; set; } = "ChaosRifleman";
    public int ScpRotation { get; set; }
}

public sealed class WarmupArenaPopulationEntry
{
    public WarmupArenaPopulationEntry(string key, string arenaId, string roleId)
    {
        Key = key;
        ArenaId = arenaId;
        RoleId = roleId;
    }

    public string Key { get; }
    public string ArenaId { get; }
    public string RoleId { get; }
}

/// <summary>Pure occupancy-to-population contract shared by runtime and deterministic tests.</summary>
public static class WarmupArenaPopulationPlanner
{
    private static readonly string[] ScpRoles =
    {
        "Scp173", "Scp049", "Scp096", "Scp106", "Scp939", "Scp3114",
    };

    public static IReadOnlyList<WarmupArenaPopulationEntry> Build(WarmupArenaPopulationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        int cap = Clamp(request.TotalBotCap, 0, 10);
        var result = new List<WarmupArenaPopulationEntry>(cap);

        if (request.SurfacePlayers > 0)
        {
            int count = Math.Max(2, (int)Math.Ceiling(request.SurfacePlayers * Clamp(request.SurfaceBotFactor, 1d, 2d)));
            count = Math.Min(count, Clamp(request.SurfaceBotCap, 2, 6));
            for (int index = 0; index < count && result.Count < cap; index++)
            {
                string role = index % 3 == 0 ? "ChaosConscript" : "ChaosRepressor";
                result.Add(new WarmupArenaPopulationEntry($"surface-{index}", "surface", role));
            }
        }

        if (request.HeavyEntrancePlayers > 0)
        {
            int count = Clamp(request.HeavyEntranceBotCount, 2, 5);
            for (int index = 0; index < count && result.Count < cap; index++)
            {
                // Keep both human factions represented so either player faction has an opponent.
                string role = index % 2 == 0 ? "ChaosRifleman" : "NtfPrivate";
                result.Add(new WarmupArenaPopulationEntry($"pvpve-{index}", "pvpve", role));
            }
        }

        if (request.LightContainmentPlayers > 0)
        {
            int count = Math.Max(1, request.LightContainmentScpBotCount);
            for (int index = 0; index < count && result.Count < cap; index++)
            {
                int roleIndex = PositiveModulo(request.ScpRotation + index, ScpRoles.Length);
                result.Add(new WarmupArenaPopulationEntry($"lcz-scp-{index}", "lcz", ScpRoles[roleIndex]));
            }
        }

        int baseline = Clamp(request.FallbackBotCount, 0, cap);
        for (int index = result.Count; index < baseline && result.Count < cap; index++)
        {
            result.Add(new WarmupArenaPopulationEntry(
                $"fallback-{index}",
                NormalizeArena(request.DefaultArenaId),
                string.IsNullOrWhiteSpace(request.FallbackRoleId) ? "ChaosRifleman" : request.FallbackRoleId));
        }

        return result;
    }

    private static string NormalizeArena(string? arenaId) =>
        string.Equals(arenaId, "surface", StringComparison.OrdinalIgnoreCase) ? "surface" :
        string.Equals(arenaId, "lcz", StringComparison.OrdinalIgnoreCase) ? "lcz" : "pvpve";

    private static int PositiveModulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));

    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
}
