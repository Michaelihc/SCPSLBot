using CommandSystem;
using LabApi.Features.Wrappers;
using Mirror;
using System;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Tests.Commands;

/// <summary>Local-only fixture for a destroyed identity stranded in Mirror's registries.</summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
internal sealed class NetworkHealthFixtureCommand : ICommand
{
    private static NetworkIdentity? victim;
    private static NetworkIdentity? survivor;
    private static uint victimId;
    private static uint survivorId;
    public string Command => "bot_health_fixture";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Arrange and inspect a destroyed-network-identity recovery case in an isolated test server.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.ServerConsoleCommands, out response))
            return false;
        switch (arguments.Count == 0 ? "" : arguments.At(0))
        {
            case "session":
                response = "HEALTH_FIXTURE session=" + System.Diagnostics.Process.GetCurrentProcess().Id;
                return true;
            case "spawn":
                Cleanup();
                victim = Create("BotHealthFixture-Victim", new Vector3(130f, 300f, -40f));
                survivor = Create("BotHealthFixture-Survivor", new Vector3(132f, 300f, -40f));
                victimId = victim.netId;
                survivorId = survivor.netId;
                response = $"HEALTH_FIXTURE spawned victim={victimId} survivor={survivorId}";
                return true;
            case "corrupt":
                if (victim == null || survivor == null)
                {
                    response = "Fixture is not arranged.";
                    return false;
                }
                // Native destruction runs first, then this fixture deliberately restores the
                // tombstone to reproduce an orphan without invoking the recovery service.
                UnityEngine.Object.DestroyImmediate(victim.gameObject);
                NetworkServer.spawned[victimId] = victim;
                foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                {
                    connection.observing.Add(victim);
                    connection.owned.Add(victim);
                }
                response = $"HEALTH_FIXTURE corrupted victim={victimId}";
                return true;
            case "assert":
                bool valid = !NetworkServer.spawned.ContainsKey(victimId)
                    && survivor != null && NetworkServer.spawned.TryGetValue(survivorId, out NetworkIdentity live)
                    && ReferenceEquals(live, survivor)
                    && NetworkServer.connections.Values.All(connection =>
                        !connection.observing.Contains(victim!) && !connection.owned.Contains(victim!));
                response = $"HEALTH_FIXTURE {(valid ? "PASS" : "FAIL")} removed={victimId} live={survivorId}";
                return valid;
            case "cleanup":
                Cleanup();
                response = "HEALTH_FIXTURE cleaned";
                return true;
            default:
                response = "bot_health_fixture session|spawn|corrupt|assert|cleanup";
                return false;
        }
    }

    private static NetworkIdentity Create(string name, Vector3 position)
    {
        PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position);
        toy.GameObject.name = name;
        return toy.GameObject.GetComponent<NetworkIdentity>();
    }

    private static void Cleanup()
    {
        if (victim != null) NetworkServer.Destroy(victim.gameObject);
        if (survivor != null) NetworkServer.Destroy(survivor.gameObject);
        victim = null;
        survivor = null;
    }
}
