using Logger = LabApi.Features.Console.Logger;
using Mirror;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.Health;

/// <summary>Records identity provenance and removes only Unity-destroyed registry entries.</summary>
internal sealed class NetworkRegistryHealthService
{
    private sealed class IdentityRecord
    {
        internal NetworkIdentity Identity;
        internal string Description;
    }

    private readonly Dictionary<uint, IdentityRecord> known = new();
    private readonly List<KeyValuePair<uint, NetworkIdentity>> destroyed = new();
    private readonly List<uint> departed = new();
    private readonly BotPluginConfig config;
    private Action<NetworkConnectionToClient> hookedConnect;
    private double nextAudit;
    private double nextFaultLog;
    private bool enabled;
    private long registryRepairs;
    private long observationRepairs;
    private long ownershipRepairs;
    private string lastRepair = "none";
    private string lastFault = "none";

    internal NetworkRegistryHealthService(BotPluginConfig config) => this.config = config;

    internal void Enable()
    {
        enabled = true;
        StaticUnityMethods.OnUpdate += OnUpdate;
        Logger.Info("[BotHealth] Network registry monitoring enabled; recovery=" + config.EnableNetworkRegistryRecovery + ".");
    }

    internal void Disable()
    {
        enabled = false;
        StaticUnityMethods.OnUpdate -= OnUpdate;
        NetworkServer.OnConnectedEvent -= OnConnected;
        hookedConnect = null;
        known.Clear();
        destroyed.Clear();
        departed.Clear();
    }

    internal string Status => $"enabled={enabled}; recovery={config.EnableNetworkRegistryRecovery}; tracked={known.Count}; "
        + $"spawned={NetworkServer.spawned.Count}; registry_repairs={registryRepairs}; observation_repairs={observationRepairs}; "
        + $"ownership_repairs={ownershipRepairs}; last_repair={lastRepair}; last_fault={lastFault}";

    private void OnUpdate()
    {
        if (!NetworkServer.active || NetworkServer.isLoadingScene)
        {
            known.Clear();
            return;
        }

        // NetworkManager replaces this delegate when a server session starts. Rebind after
        // that replacement instead of assuming an Enable-time subscription survives startup.
        if (NetworkServer.OnConnectedEvent != hookedConnect)
        {
            NetworkServer.OnConnectedEvent -= OnConnected;
            NetworkServer.OnConnectedEvent += OnConnected;
            hookedConnect = NetworkServer.OnConnectedEvent;
        }

        bool audit = Time.unscaledTimeAsDouble >= nextAudit;
        if (audit)
            nextAudit = Time.unscaledTimeAsDouble + 1d;
        Scan(audit, "update");
    }

    private void OnConnected(NetworkConnectionToClient _) => Scan(true, "connection");

    private void Scan(bool audit, string source)
    {
        try
        {
            destroyed.Clear();
            // The cheap null-only pass runs before network LateUpdate, closing the interval
            // between a destruction and the next identity/observer audit. Metadata is sampled
            // once per second; live entities are never removed or otherwise changed.
            foreach (KeyValuePair<uint, NetworkIdentity> entry in NetworkServer.spawned)
            {
                if (entry.Value == null)
                {
                    destroyed.Add(entry);
                }
                else if (audit && (!known.TryGetValue(entry.Key, out IdentityRecord record)
                         || !ReferenceEquals(record.Identity, entry.Value)))
                {
                    known[entry.Key] = new IdentityRecord { Identity = entry.Value, Description = Describe(entry.Value) };
                }
            }

            if (destroyed.Count > 0)
            {
                int reported = 0;
                foreach (KeyValuePair<uint, NetworkIdentity> entry in destroyed)
                {
                    if (!NetworkServer.spawned.TryGetValue(entry.Key, out NetworkIdentity current)
                        || !ReferenceEquals(current, entry.Value) || current != null)
                        continue;

                    string detail = known.TryGetValue(entry.Key, out IdentityRecord record) && ReferenceEquals(record.Identity, entry.Value)
                        ? record.Description : "identity=not-seen-live";
                    lastRepair = $"utc={DateTime.UtcNow:O}, netId={entry.Key}, source={source}, {detail}";
                    if (reported++ < 8 && (config.EnableNetworkRegistryRecovery || Time.unscaledTimeAsDouble >= nextFaultLog))
                        Logger.Warn("[BotHealth] DESTROYED_REGISTRY_ENTRY " + lastRepair + "; recovery=" + config.EnableNetworkRegistryRecovery);

                    if (config.EnableNetworkRegistryRecovery)
                    {
                        NetworkServer.spawned.Remove(entry.Key);
                        known.Remove(entry.Key);
                        registryRepairs++;
                        NotifyObservers(entry.Key, entry.Value);
                    }
                }
                if (reported > 8 && config.EnableNetworkRegistryRecovery)
                    Logger.Warn($"[BotHealth] Additional destroyed registry entries in this scan: {reported - 8}.");
                if (Time.unscaledTimeAsDouble >= nextFaultLog)
                    nextFaultLog = Time.unscaledTimeAsDouble + 15d;
            }

            if (audit || destroyed.Count > 0)
            {
                AuditConnections();
                departed.Clear();
                foreach (uint netId in known.Keys)
                    if (!NetworkServer.spawned.ContainsKey(netId))
                        departed.Add(netId);
                foreach (uint netId in departed)
                    known.Remove(netId);
            }
        }
        catch (Exception exception)
        {
            lastFault = exception.GetType().Name + ": " + exception.Message.Replace('\n', ' ').Replace(';', ',');
            if (Time.unscaledTimeAsDouble >= nextFaultLog)
            {
                nextFaultLog = Time.unscaledTimeAsDouble + 15d;
                Logger.Error("[BotHealth] Registry scan failed; will retry: " + exception);
            }
        }
    }

    private void NotifyObservers(uint netId, NetworkIdentity identity)
    {
        foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
        {
            if (connection.observing.Remove(identity))
            {
                observationRepairs++;
                if (connection.isReady)
                {
                    try { connection.Send(new ObjectDestroyMessage { netId = netId }); }
                    catch (Exception exception)
                    {
                        // A failed notification must not strand tombstones for other clients.
                        lastFault = "destroy notification: " + exception.GetType().Name;
                        if (Time.unscaledTimeAsDouble >= nextFaultLog)
                        {
                            nextFaultLog = Time.unscaledTimeAsDouble + 15d;
                            Logger.Warn($"[BotHealth] Destroy notification failed: netId={netId}, connId={connection.connectionId}, error={exception.Message}");
                        }
                    }
                }
            }
            if (connection.owned.Remove(identity))
                ownershipRepairs++;
        }
    }

    private void AuditConnections()
    {
        if (!config.EnableNetworkRegistryRecovery)
            return;

        int observations = 0;
        int ownerships = 0;
        foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
        {
            observations += connection.observing.RemoveWhere(identity => identity == null);
            ownerships += connection.owned.RemoveWhere(identity => identity == null);
        }
        observationRepairs += observations;
        ownershipRepairs += ownerships;
        if (observations != 0 || ownerships != 0)
            Logger.Warn($"[BotHealth] Pruned destroyed connection references: observing={observations}, owned={ownerships}.");
    }

    private static string Describe(NetworkIdentity identity)
    {
        Transform parent = identity.transform.parent;
        string behaviours = string.Join("|", identity.GetComponents<NetworkBehaviour>()
            .Where(component => component != null)
            .Select(component => component.GetType().FullName + "@" + component.GetType().Assembly.GetName().Name));
        return $"object={Clean(identity.gameObject.name)}, parent={Clean(parent == null ? "none" : parent.name)}, "
            + $"instance={identity.GetInstanceID()}, sceneId={identity.sceneId}, behaviours={Clean(behaviours)}";
    }

    private static string Clean(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',');
        return value.Length > 600 ? value.Substring(0, 600) : value;
    }
}
