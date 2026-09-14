using System.Collections.Generic;
using LabApi.Features.Wrappers;
using StatsBots.Core;

namespace StatsBots.Services;

internal sealed class DisplayPreferences
{
    public bool Hud { get; set; } = true;
    public bool Title { get; set; } = true;
    public bool CombatNotices { get; set; } = true;
    public bool BeginnerTips { get; set; } = true;
    public bool Community { get; set; } = true;
}

internal sealed class PlayerPreferences
{
    private readonly Dictionary<string, DisplayPreferences> _values = new(System.StringComparer.Ordinal);

    public DisplayPreferences For(Player player)
    {
        if (!AuthenticatedIdentity.TryNormalize(player?.UserId, out string userId)) return new DisplayPreferences();
        if (!_values.TryGetValue(userId, out DisplayPreferences value))
        {
            value = new DisplayPreferences();
            _values[userId] = value;
        }
        return value;
    }

    public void Remove(Player player)
    {
        if (AuthenticatedIdentity.TryNormalize(player?.UserId, out string userId)) _values.Remove(userId);
    }

    public void Clear() => _values.Clear();
}
