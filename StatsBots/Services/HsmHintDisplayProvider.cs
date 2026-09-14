using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using StatsBots.Config;

namespace StatsBots.Services;

/// <summary>
/// StatsBots-local copy of the reusable HintDisplayProvider pattern. HSM remains optional and
/// late-bound; every entry uses a stable ID/group and Center alignment with an explicit X.
/// </summary>
internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
{
    private const float MinimumMiddleLineHeight = 12f;
    private readonly HintDisplayConfig _config;
    private readonly Dictionary<(ReferenceHub Hub, string Tag), ActiveHint> _active = new();
    private Assembly? _assembly;
    private Type? _displayType;
    private Type? _hintType;
    private Type? _abstractHintType;
    private ConstructorInfo? _hintConstructor;
    private MethodInfo? _getDisplay;
    private MethodInfo? _addHint;
    private MethodInfo? _removeHint;
    private MethodInfo? _forceUpdate;
    private PropertyInfo? _id;
    private PropertyInfo? _text;
    private PropertyInfo? _x;
    private PropertyInfo? _y;
    private PropertyInfo? _size;
    private PropertyInfo? _lineHeight;
    private PropertyInfo? _alignment;
    private PropertyInfo? _verticalAlignment;
    private PropertyInfo? _syncSpeed;
    private object? _center;
    private object? _middle;
    private object? _fast;
    private bool _available;
    private bool _loggedFailure;

    public HsmHintDisplayProvider(HintDisplayConfig config) => _config = config;
    public bool IsAvailable => _available;

    public bool TryInitialize(bool logResult = true)
    {
        if (_available) return true;
        try
        {
            _assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static asm => string.Equals(asm.GetName().Name, "HintServiceMeow", StringComparison.OrdinalIgnoreCase));
            _displayType = _assembly?.GetType("HintServiceMeow.Core.Utilities.PlayerDisplay", false);
            _hintType = _assembly?.GetType("HintServiceMeow.Core.Models.Hints.Hint", false);
            _abstractHintType = _assembly?.GetType("HintServiceMeow.Core.Models.Hints.AbstractHint", false);
            Type? alignType = _assembly?.GetType("HintServiceMeow.Core.Enum.HintAlignment", false);
            Type? verticalType = _assembly?.GetType("HintServiceMeow.Core.Enum.HintVerticalAlign", false);
            Type? speedType = _assembly?.GetType("HintServiceMeow.Core.Enum.HintSyncSpeed", false);
            if (_displayType == null || _hintType == null || _abstractHintType == null || alignType == null || verticalType == null || speedType == null)
                return Fail("required HSM types were not found", logResult);

            _hintConstructor = _hintType.GetConstructor(Type.EmptyTypes);
            _getDisplay = _displayType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null);
            _addHint = _displayType.GetMethod("AddHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { _abstractHintType, typeof(string) }, null);
            _removeHint = _displayType.GetMethod("RemoveHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { _abstractHintType, typeof(string) }, null);
            _forceUpdate = _displayType.GetMethod("ForceUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            _id = _abstractHintType.GetProperty("Id");
            _text = _abstractHintType.GetProperty("Text");
            _size = _abstractHintType.GetProperty("FontSize");
            _lineHeight = _abstractHintType.GetProperty("LineHeight");
            _syncSpeed = _abstractHintType.GetProperty("SyncSpeed");
            _x = _hintType.GetProperty("XCoordinate");
            _y = _hintType.GetProperty("YCoordinate");
            _alignment = _hintType.GetProperty("Alignment");
            _verticalAlignment = _hintType.GetProperty("YCoordinateAlign");
            _center = Enum.Parse(alignType, "Center");
            _middle = Enum.Parse(verticalType, "Middle");
            _fast = Enum.Parse(speedType, "Fast");
            if (_hintConstructor == null || _getDisplay == null || _addHint == null || _removeHint == null || _id == null || _text == null
                || _size == null || _lineHeight == null || _syncSpeed == null || _x == null || _y == null || _alignment == null || _verticalAlignment == null)
                return Fail("required HSM members were not found", logResult);
            _available = true;
            if (logResult) Logger.Info("[StatsBots:Hints] HintServiceMeow detected.");
            return true;
        }
        catch (Exception ex) { return Fail(ex.GetBaseException().Message, logResult); }
    }

    public void Enable()
    {
        TryInitialize();
        PlayerEvents.Left += OnLeft;
    }

    public void Disable()
    {
        PlayerEvents.Left -= OnLeft;
        foreach (var key in _active.Keys.ToArray()) Remove(key.Hub, key.Tag);
        _active.Clear();
    }

    public void Show(Player player, string tagId, float x, float y, int size, string message, float durationSeconds = 0f)
    {
        if (!_available && !TryInitialize()) return;
        if (player?.ReferenceHub == null || (!player.IsDummy && (!player.IsPlayer || !player.IsReady))) return;
        string tag = Normalize(tagId);
        (ReferenceHub Hub, string Tag) key = (player.ReferenceHub, tag);
        try
        {
            object? display = _getDisplay!.Invoke(null, new object[] { player });
            if (display == null) return;
            if (!_active.TryGetValue(key, out ActiveHint active))
            {
                object hint = _hintConstructor!.Invoke(Array.Empty<object>());
                _id!.SetValue(hint, tag);
                _alignment!.SetValue(hint, _center);
                _verticalAlignment!.SetValue(hint, _middle);
                _syncSpeed!.SetValue(hint, _fast);
                active = new ActiveHint(display, hint);
                _active[key] = active;
                _addHint!.Invoke(display, new[] { hint, (object)_config.GroupName });
            }
            active.Generation++;
            _text!.SetValue(active.Hint, message ?? string.Empty);
            _x!.SetValue(active.Hint, x);
            _y!.SetValue(active.Hint, y);
            _size!.SetValue(active.Hint, Math.Max(6, size));
            _lineHeight!.SetValue(active.Hint, Math.Max(MinimumMiddleLineHeight, _config.LineHeight));
            if (_config.ForceFastUpdates) _forceUpdate?.Invoke(active.Display, new object[] { true });
            if (durationSeconds > 0f)
            {
                int generation = active.Generation;
                Timing.CallDelayed(durationSeconds, () =>
                {
                    if (_active.TryGetValue(key, out ActiveHint current) && current.Generation == generation) Remove(key.Hub, key.Tag);
                });
            }
        }
        catch (Exception ex) { Fail("HSM invocation failed: " + ex.GetBaseException().Message, true); }
    }

    public void Remove(Player player, string tagId)
    {
        if (player?.ReferenceHub != null) Remove(player.ReferenceHub, Normalize(tagId));
    }

    public void Clear(Player player)
    {
        if (player?.ReferenceHub == null) return;
        foreach (var key in _active.Keys.Where(k => k.Hub == player.ReferenceHub).ToArray()) Remove(key.Hub, key.Tag);
    }

    private void Remove(ReferenceHub hub, string tag)
    {
        if (!_active.TryGetValue((hub, tag), out ActiveHint active)) return;
        _active.Remove((hub, tag));
        try
        {
            _removeHint?.Invoke(active.Display, new[] { active.Hint, (object)_config.GroupName });
            if (_config.ForceFastUpdates) _forceUpdate?.Invoke(active.Display, new object[] { true });
        }
        catch { }
    }

    private void OnLeft(PlayerLeftEventArgs ev) => Clear(ev.Player);
    private string Normalize(string tag) => tag.StartsWith(_config.TagPrefix, StringComparison.Ordinal) ? tag : _config.TagPrefix + tag;
    private bool Fail(string reason, bool log)
    {
        _available = false;
        if (log && !_loggedFailure)
        {
            _loggedFailure = true;
            Logger.Error("[StatsBots:Hints] " + reason + ". HUD entries will not be displayed.");
        }
        return false;
    }

    private sealed class ActiveHint
    {
        public ActiveHint(object display, object hint) { Display = display; Hint = hint; }
        public object Display { get; }
        public object Hint { get; }
        public int Generation { get; set; }
    }
}
