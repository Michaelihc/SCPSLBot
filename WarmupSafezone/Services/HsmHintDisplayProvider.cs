using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;

namespace ScpslPluginStarter.Services;

// Reflection-backed adaptation of the repository HintDisplayProvider template. HSM stays optional.
internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
{
    private const string AssemblyName = "HintServiceMeow";
    private readonly HintDisplayConfig _config;
    private readonly Dictionary<(int PlayerId, string Tag), ActiveHint> _active = new();
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
    private PropertyInfo? _fontSize;
    private PropertyInfo? _lineHeight;
    private PropertyInfo? _alignment;
    private PropertyInfo? _verticalAlignment;
    private PropertyInfo? _syncSpeed;
    private object? _center;
    private object? _middle;
    private object? _fast;
    private bool _available;
    private int _generation;
    private bool _loggedInvocationFailure;

    public HsmHintDisplayProvider(HintDisplayConfig config) => _config = config;

    public bool TryInitialize(bool logResult = true)
    {
        if (_available)
        {
            return true;
        }

        try
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(item => string.Equals(item.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                return false;
            }

            _displayType = assembly.GetType("HintServiceMeow.Core.Utilities.PlayerDisplay");
            _hintType = assembly.GetType("HintServiceMeow.Core.Models.Hints.Hint");
            _abstractHintType = assembly.GetType("HintServiceMeow.Core.Models.Hints.AbstractHint");
            Type? horizontalType = assembly.GetType("HintServiceMeow.Core.Enum.HintAlignment");
            Type? verticalType = assembly.GetType("HintServiceMeow.Core.Enum.HintVerticalAlign");
            Type? syncType = assembly.GetType("HintServiceMeow.Core.Enum.HintSyncSpeed");
            if (_displayType == null || _hintType == null || _abstractHintType == null
                || horizontalType == null || verticalType == null || syncType == null)
            {
                return false;
            }

            _hintConstructor = _hintType.GetConstructor(Type.EmptyTypes);
            _getDisplay = _displayType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null);
            _addHint = _displayType.GetMethod("AddHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { _abstractHintType, typeof(string) }, null);
            _removeHint = _displayType.GetMethod("RemoveHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { _abstractHintType, typeof(string) }, null);
            _forceUpdate = _displayType.GetMethod("ForceUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            _id = _abstractHintType.GetProperty("Id");
            _text = _abstractHintType.GetProperty("Text");
            _fontSize = _abstractHintType.GetProperty("FontSize");
            _lineHeight = _abstractHintType.GetProperty("LineHeight");
            _syncSpeed = _abstractHintType.GetProperty("SyncSpeed");
            _x = _hintType.GetProperty("XCoordinate");
            _y = _hintType.GetProperty("YCoordinate");
            _alignment = _hintType.GetProperty("Alignment");
            _verticalAlignment = _hintType.GetProperty("YCoordinateAlign");
            _center = Enum.Parse(horizontalType, "Center");
            _middle = Enum.Parse(verticalType, "Middle");
            _fast = Enum.Parse(syncType, "Fast");

            _available = _hintConstructor != null && _getDisplay != null && _addHint != null && _removeHint != null
                && _id != null && _text != null && _fontSize != null && _lineHeight != null && _syncSpeed != null
                && _x != null && _y != null && _alignment != null && _verticalAlignment != null;
            if (_available && logResult)
            {
                Logger.Info("[WarmupSafezone:Hints] HintServiceMeow detected; stable tagged hints enabled.");
            }

            return _available;
        }
        catch (Exception exception)
        {
            if (logResult)
            {
                Logger.Error($"[WarmupSafezone:Hints] HSM initialization failed: {exception.GetBaseException().Message}");
            }

            return false;
        }
    }

    public void Enable()
    {
        if (TryInitialize())
        {
            PlayerEvents.Left += OnPlayerLeft;
        }
    }

    public void Disable()
    {
        _generation++;
        PlayerEvents.Left -= OnPlayerLeft;
        foreach ((int playerId, string tag) in _active.Keys.ToArray())
        {
            Remove(playerId, tag);
        }
    }

    public void ShowNotice(Player player, string message, float duration) =>
        ShowPrompt(player, "notice", _config.NoticeY, message, duration);

    public void ShowPrompt(Player player, string tagId, float y, string message, float duration)
    {
        try
        {
            ShowPromptCore(player, tagId, y, message, duration);
        }
        catch (Exception exception)
        {
            LogInvocationFailure(exception);
        }
    }

    private void ShowPromptCore(Player player, string tagId, float y, string message, float duration)
    {
        if (!_available || player?.ReferenceHub == null)
        {
            return;
        }

        string tag = Normalize(tagId);
        (int PlayerId, string Tag) key = (player.PlayerId, tag);
        object? display = _getDisplay!.Invoke(null, new object[] { player });
        if (display == null)
        {
            return;
        }

        if (!_active.TryGetValue(key, out ActiveHint? active))
        {
            object hint = _hintConstructor!.Invoke(Array.Empty<object>());
            _id!.SetValue(hint, tag);
            _alignment!.SetValue(hint, _center);
            _verticalAlignment!.SetValue(hint, _middle);
            _syncSpeed!.SetValue(hint, _fast);
            active = new ActiveHint(display, hint);
            _active[key] = active;
            _addHint!.Invoke(display, new[] { hint, GroupName });
        }

        active.Display = display;
        active.Version++;
        _text!.SetValue(active.Hint, AddGhostTailToRows(message ?? string.Empty));
        _x!.SetValue(active.Hint, _config.DefaultX);
        _y!.SetValue(active.Hint, y);
        _fontSize!.SetValue(active.Hint, Math.Max(6, _config.PromptTextSize));
        _lineHeight!.SetValue(active.Hint, Math.Max(0f, _config.LineHeight));
        ForceUpdate(display);

        int version = active.Version;
        int generation = _generation;
        ActiveHint expected = active;
        Timing.CallDelayed(Math.Max(0.01f, duration), () =>
        {
            if (generation == _generation
                && _active.TryGetValue(key, out ActiveHint? current)
                && ReferenceEquals(current, expected)
                && current.Version == version)
            {
                Remove(key.PlayerId, key.Tag);
            }
        });
    }

    public void Remove(Player player, string tagId)
    {
        if (player?.ReferenceHub != null)
        {
            Remove(player.PlayerId, Normalize(tagId));
        }
    }

    public void Clear(Player player)
    {
        if (player?.ReferenceHub == null)
        {
            return;
        }

        foreach (string tag in _active.Keys.Where(key => key.PlayerId == player.PlayerId).Select(key => key.Tag).ToArray())
        {
            Remove(player.PlayerId, tag);
        }
    }

    private void Remove(int playerId, string tag)
    {
        try
        {
            RemoveCore(playerId, tag);
        }
        catch (Exception exception)
        {
            LogInvocationFailure(exception);
        }
    }

    private void RemoveCore(int playerId, string tag)
    {
        if (!_active.TryGetValue((playerId, tag), out ActiveHint? active))
        {
            return;
        }

        _active.Remove((playerId, tag));
        _removeHint!.Invoke(active.Display, new[] { active.Hint, GroupName });
        ForceUpdate(active.Display);
    }

    private void ForceUpdate(object display)
    {
        if (_config.ForceFastUpdates && _forceUpdate != null)
        {
            _forceUpdate.Invoke(display, new object[] { true });
        }
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev) => Clear(ev.Player);

    private string AddGhostTailToRows(string message)
    {
        int columns = Math.Max(0, Math.Min(80, _config.GhostTailColumns));
        if (columns == 0 || string.IsNullOrEmpty(message))
        {
            return message;
        }

        string ghostTail = $"<color=#00000000><mspace=1em>{new string(' ', columns)}</mspace></color>";
        return string.Join("\n", message.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(new[] { '\n' }, StringSplitOptions.None)
            .Select(row => row + ghostTail));
    }

    private string Normalize(string tag)
    {
        string prefix = string.IsNullOrWhiteSpace(_config.TagPrefix) ? "warmupsafezone." : _config.TagPrefix;
        return tag.StartsWith(prefix, StringComparison.Ordinal) ? tag : prefix + tag;
    }
    private string GroupName => string.IsNullOrWhiteSpace(_config.GroupName) ? "warmupsafezone.hints" : _config.GroupName;

    private void LogInvocationFailure(Exception exception)
    {
        if (_loggedInvocationFailure)
        {
            return;
        }

        _loggedInvocationFailure = true;
        Logger.Error($"[WarmupSafezone:Hints] HSM invocation failed; later gameplay processing will continue: {exception.GetBaseException().Message}");
    }

    private sealed class ActiveHint
    {
        public ActiveHint(object display, object hint)
        {
            Display = display;
            Hint = hint;
        }

        public object Display { get; set; }
        public object Hint { get; }
        public int Version { get; set; }
    }
}
