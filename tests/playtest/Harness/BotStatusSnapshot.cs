using PlayerRoles;
using PlaytestHarness.Core;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCPSLBot.PlaytestScenarios.Harness;

internal sealed class BotStatusSnapshot
{
    private BotStatusSnapshot(IReadOnlyDictionary<string, string> values, string raw)
    {
        Values = values;
        Raw = raw;
        Mode = Get("mode");
        Desired = GetInt("desired");
        DesiredRole = GetRole("desired_role");
        Tracked = GetInt("tracked");
        Owned = GetInt("owned");
        Live = GetInt("live");
        NetworkReady = GetBool("network_ready");
        NavReady = GetBool("nav_ready");
        NavGeneration = GetInt("nav_generation");
        NavReadyGeneration = GetInt("nav_ready_generation");
        AiRunnerRunning = GetBool("ai_runner_running");
        AiHeartbeat = Get("ai_heartbeat");
        Parked = GetInt("parked");
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public string Raw { get; }
    public string Mode { get; }
    public int Desired { get; }
    public RoleTypeId DesiredRole { get; }
    public int Tracked { get; }
    public int Owned { get; }
    public int Live { get; }
    public bool NetworkReady { get; }
    public bool NavReady { get; }
    public int NavGeneration { get; }
    public int NavReadyGeneration { get; }
    public bool AiRunnerRunning { get; }
    public string AiHeartbeat { get; }
    public int Parked { get; }

    public static BotStatusSnapshot Read()
    {
        NativeCommandResult command = NativeCommandAdapter.RemoteAdmin("bot_status");
        if (!command.Success || string.IsNullOrWhiteSpace(command.Response)
            || command.Response.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new RequireException($"bot_status was unavailable or rejected: {command}");
        }

        Dictionary<string, string> values = CommandFields.Parse(command.Response, ';');
        string[] required =
        {
            "mode", "desired", "desired_role", "tracked", "owned", "live", "network_ready",
            "nav_ready", "nav_generation", "nav_ready_generation", "last_reconcile",
            "last_spawn_error", "reconcile_fault", "ai_runner_running", "ai_heartbeat",
            "ai_last_fault", "ai_last_fault_time", "parked", "sight_senses",
            "raycast_capacity", "tracked_colliders", "nav_error", "role_warning",
        };
        foreach (string key in required)
        {
            if (!values.ContainsKey(key))
            {
                throw new RequireException($"bot_status omitted required field '{key}': {command.Response}");
            }
        }

        return new BotStatusSnapshot(values, command.Response);
    }

    private string Get(string key) => Values.TryGetValue(key, out string value) ? value : string.Empty;

    private int GetInt(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : throw new RequireException($"bot_status field '{key}' is not an integer: '{Get(key)}'");

    private bool GetBool(string key) => bool.TryParse(Get(key), out bool value)
        ? value
        : throw new RequireException($"bot_status field '{key}' is not a boolean: '{Get(key)}'");

    private RoleTypeId GetRole(string key) => Enum.TryParse(Get(key), ignoreCase: true, out RoleTypeId role)
        ? role
        : throw new RequireException($"bot_status field '{key}' is not a RoleTypeId: '{Get(key)}'");
}

internal static class CommandFields
{
    public static Dictionary<string, string> Parse(string text, char separator)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            int equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            fields[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Trim();
        }

        return fields;
    }

    public static Dictionary<string, string> ParseWhitespace(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            fields[part.Substring(0, equals)] = part.Substring(equals + 1);
        }

        return fields;
    }
}
