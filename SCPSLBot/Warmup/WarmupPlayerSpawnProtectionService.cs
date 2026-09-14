#nullable enable

using CustomPlayerEffects;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PlayerRoles;
using SCPSLBot.Warmup.Policy;
using System;
using System.Collections.Generic;

namespace SCPSLBot.Warmup;

/// <summary>
/// Narrows native spawn protection for real players during Standard warmup. A confirmed death
/// authorizes exactly one later playable respawn; other role/loadout assignments clear the native
/// effect after the game's completed-role event. Managed dummies remain owned by BotManager.
/// </summary>
internal sealed class WarmupPlayerSpawnProtectionService
{
    private readonly HashSet<ReferenceHub> pendingDeathRespawns = new();
    private Func<bool>? isStandardWarmup;
    private bool initialized;

    public void Init(Func<bool> standardWarmupProvider)
    {
        if (initialized)
        {
            return;
        }

        isStandardWarmup = standardWarmupProvider
            ?? throw new ArgumentNullException(nameof(standardWarmupProvider));
        initialized = true;
        PlayerEvents.Death += OnPlayerDeath;
        PlayerEvents.ChangedRole += OnPlayerChangedRole;
        PlayerEvents.Left += OnPlayerLeft;
        ServerEvents.RoundRestarted += OnRoundRestarted;
    }

    public void Terminate()
    {
        if (!initialized)
        {
            return;
        }

        initialized = false;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        PlayerEvents.Left -= OnPlayerLeft;
        PlayerEvents.ChangedRole -= OnPlayerChangedRole;
        PlayerEvents.Death -= OnPlayerDeath;
        pendingDeathRespawns.Clear();
        isStandardWarmup = null;
    }

    private void OnPlayerDeath(PlayerDeathEventArgs ev)
    {
        if (IsRealPlayer(ev.Player))
        {
            pendingDeathRespawns.Add(ev.Player.ReferenceHub);
        }
    }

    private void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev)
    {
        Player player = ev.Player;
        bool isRealPlayer = IsRealPlayer(player);
        ReferenceHub? hub = isRealPlayer ? player.ReferenceHub : null;
        bool hasPendingDeathRespawn = hub != null && pendingDeathRespawns.Contains(hub);
        WarmupPlayerSpawnProtectionAction action = WarmupPlayerSpawnProtectionPolicy.Evaluate(
            IsStandardWarmup(),
            isRealPlayer,
            ev.NewRole is IHealthbarRole,
            hasPendingDeathRespawn);

        if (action == WarmupPlayerSpawnProtectionAction.RetainDeathRespawnProtection)
        {
            pendingDeathRespawns.Remove(hub!);
            bool active = hub?.playerEffectsController?.GetEffect<SpawnProtected>().IsEnabled == true;
            Logger.Info(
                $"[SCPSLBot] PLAYER_SPAWN_PROTECTION_DEATH_RESPAWN player={player.PlayerId} "
                + $"role={ev.NewRole.RoleTypeId} active={active}");
            return;
        }

        if (action != WarmupPlayerSpawnProtectionAction.ClearNativeProtection
            || hub?.playerEffectsController == null)
        {
            return;
        }

        SpawnProtected effect = hub.playerEffectsController.GetEffect<SpawnProtected>();
        if (!effect.IsEnabled)
        {
            return;
        }

        hub.playerEffectsController.DisableEffect<SpawnProtected>();
        if (effect.IsEnabled)
        {
            Logger.Warn(
                $"[SCPSLBot] PLAYER_SPAWN_PROTECTION_CLEAR_FAILED player={player.PlayerId} "
                + $"role={ev.NewRole.RoleTypeId} reason={ev.ChangeReason}");
            return;
        }

        Logger.Info(
            $"[SCPSLBot] PLAYER_SPAWN_PROTECTION_CLEARED player={player.PlayerId} "
            + $"role={ev.NewRole.RoleTypeId} reason={ev.ChangeReason}");
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        if (ev.Player != null)
        {
            pendingDeathRespawns.Remove(ev.Player.ReferenceHub);
        }
    }

    private void OnRoundRestarted() => pendingDeathRespawns.Clear();

    private bool IsStandardWarmup()
    {
        try
        {
            return initialized && isStandardWarmup?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRealPlayer(Player? player) =>
        player != null
        && !player.IsDestroyed
        && !player.IsHost
        && !player.IsDummy;
}
