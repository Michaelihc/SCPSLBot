using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter.Services;

internal sealed class SafezoneOccupancyService
{
    private readonly SafezoneVolumeService _volumes;
    private readonly ExitProtectionService _exitProtection;
    private readonly Dictionary<int, SafezoneMembership> _membershipByPlayer = new();

    public SafezoneOccupancyService(SafezoneVolumeService volumes, ExitProtectionService exitProtection)
    {
        _volumes = volumes;
        _exitProtection = exitProtection;
    }

    public SafezoneMembership ResolveAtEvent(Player player) => Update(player);

    public void Recover()
    {
        HashSet<int> live = new();
        foreach (Player player in Player.List)
        {
            if (!SafezoneVolumeService.IsEligible(player))
            {
                Forget(player.PlayerId, clearProtection: true);
                continue;
            }

            live.Add(player.PlayerId);
            Update(player);
        }

        foreach (int stalePlayerId in _membershipByPlayer.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            Forget(stalePlayerId, clearProtection: true);
        }
    }

    public void Forget(int playerId, bool clearProtection)
    {
        _membershipByPlayer.Remove(playerId);
        if (clearProtection)
        {
            _exitProtection.Forget(playerId);
        }
    }

    public void Reset()
    {
        _membershipByPlayer.Clear();
        _exitProtection.Reset();
    }

    private SafezoneMembership Update(Player player)
    {
        if (!SafezoneVolumeService.IsEligible(player))
        {
            Forget(player.PlayerId, clearProtection: true);
            return SafezoneMembership.None;
        }

        SafezoneMembership current = _volumes.Resolve(player);
        _membershipByPlayer.TryGetValue(player.PlayerId, out SafezoneMembership previous);
        if (previous != SafezoneMembership.None && current == SafezoneMembership.None)
        {
            _exitProtection.Grant(player.PlayerId);
        }

        if (current == SafezoneMembership.None)
        {
            _membershipByPlayer.Remove(player.PlayerId);
        }
        else
        {
            _membershipByPlayer[player.PlayerId] = current;
        }

        return current;
    }
}
