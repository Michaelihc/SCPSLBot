using System;
using System.Linq;
using System.Reflection;
using LabApi.Features.Wrappers;

namespace StatsBots.Integration;

/// <summary>Late-bound consumer of SCPSLBot's public, read-only managed-bot identity contract.</summary>
internal sealed class ScpslBotAdapter
{
    private const string AssemblyName = "SCPSLBot";
    private PropertyInfo? _difficultyProperty;
    private PropertyInfo? _liveCountProperty;
    private MethodInfo? _isManagedMethod;

    public bool IsManagedBot(Player? player)
    {
        if (player?.ReferenceHub == null || !TryBind()) return false;
        try { return _isManagedMethod != null && (bool)_isManagedMethod.Invoke(null, new object[] { player }); }
        catch { Invalidate(); return false; }
    }

    public int? LiveBotCount
    {
        get
        {
            if (!TryBind()) return null;
            try
            {
                object? value = _liveCountProperty?.GetValue(null);
                return value == null ? null : Math.Max(0, Convert.ToInt32(value));
            }
            catch { Invalidate(); return null; }
        }
    }

    public string Difficulty
    {
        get
        {
            if (!TryBind()) return "--";
            try { return _difficultyProperty?.GetValue(null)?.ToString() ?? "--"; }
            catch { return "--"; }
        }
    }

    private bool TryBind()
    {
        if (_isManagedMethod != null) return true;
        try
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            Type? contractType = assembly?.GetType("SCPSLBot.Api.ManagedBotIdentity", false);
            _isManagedMethod = contractType?.GetMethod("IsManaged", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null);
            _liveCountProperty = contractType?.GetProperty("LiveCount", BindingFlags.Public | BindingFlags.Static);
            Type? combatType = assembly?.GetType("SCPSLBot.AI.FirstPersonControl.Combat.FpcBotCombat", false);
            _difficultyProperty = combatType?.GetProperty("Difficulty", BindingFlags.Public | BindingFlags.Static);
            return _isManagedMethod != null;
        }
        catch { Invalidate(); return false; }
    }

    private void Invalidate()
    {
        _difficultyProperty = null;
        _liveCountProperty = null;
        _isManagedMethod = null;
    }
}
