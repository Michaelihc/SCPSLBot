using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using StatsBots.Config;
using StatsBots.Services;

namespace StatsBots.Integration;

/// <summary>Late-bound consumer of the compatibility fork; StatsBots has no assembly dependency on it.</summary>
internal sealed class ServerKeybindsAdapter
{
    private readonly StatsBotsConfig _config;
    private readonly StatsBotsRuntime _runtime;
    private readonly Localization _text;
    private readonly PlayerPreferences _preferences;
    private readonly HashSet<(ReferenceHub Hub, int Local)> _toggleBaselines = new();
    private object? _block;
    private MethodInfo? _requestRefresh;
    private bool _enabled;
    private bool _loggedUnavailable;

    public ServerKeybindsAdapter(StatsBotsConfig config, StatsBotsRuntime runtime, Localization text, PlayerPreferences preferences)
    {
        _config = config;
        _runtime = runtime;
        _text = text;
        _preferences = preferences;
    }

    public bool HasClaim => _block != null;

    public bool Enable()
    {
        if (_enabled) return true;
        if (_block != null && !TryDisableBlock())
            return Unavailable("a prior partial SSS registration could not be released");
        try
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static asm => string.Equals(asm.GetName().Name, "ServerKeybinds", StringComparison.OrdinalIgnoreCase));
            Type? registryType = assembly?.GetType("ServerKeybinds.KeybindRegistry", false);
            Type? blockType = assembly?.GetType("ServerKeybinds.KeybindBlock", false);
            Type? categoryType = assembly?.GetType("ServerKeybinds.SettingsCategory", false);
            Type? modelType = assembly?.GetType("ServerKeybinds.DropdownModel", false);
            Type? selectionType = assembly?.GetType("ServerKeybinds.DropdownSelection", false);
            MethodInfo? addDropdown = blockType?.GetMethods()
                .Where(m => m.Name == "AddDropdownForPlayer")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault(m => m.GetParameters().Length is 4 or 5);
            if (registryType == null || blockType == null || categoryType == null || modelType == null || selectionType == null || addDropdown == null)
                return Unavailable("the personalized dropdown compatibility API is missing");

            MethodInfo? claim = registryType.GetMethod("ClaimBlock", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(string) }, null);
            _requestRefresh = registryType.GetMethod("RequestPlayerRefresh", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player), typeof(string) }, null);
            _block = claim?.Invoke(null, new object[] { _config.SssBaseId, "StatsBots" });
            if (_block == null || _requestRefresh == null) return Unavailable("claim/refresh methods are incompatible");

            object displayCategory = Enum.Parse(categoryType, "Display");
            InvokeFluent("InCategory", displayCategory);
            bool chinese = !string.Equals(_config.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
            InvokeFluent("Header", chinese ? "热身数据与显示" : "Warmup stats & display");

            Delegate modelDelegate = BuildModelDelegate(addDropdown.GetParameters()[1].ParameterType, modelType);
            Delegate selectionDelegate = BuildSelectionDelegate(addDropdown.GetParameters()[2].ParameterType, selectionType);
            object? fallback = modelType.GetProperty("Hidden", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object?[] dropdownArguments = addDropdown.GetParameters().Length == 5
                ? new object?[] { 1, modelDelegate, selectionDelegate, fallback, null }
                : new object?[] { 1, modelDelegate, selectionDelegate, fallback };
            addDropdown.Invoke(_block, dropdownArguments);

            AddToggle(2, chinese ? "热身 HUD" : "Warmup HUD", p => !_preferences.For(p).Hud,
                (p, off) => { _preferences.For(p).Hud = !off; _runtime.RefreshHud(p); });
            AddToggle(3, chinese ? "热身称号" : "Warmup title", p => !_preferences.For(p).Title,
                (p, off) => { _preferences.For(p).Title = !off; _runtime.RefreshHud(p); });
            AddToggle(4, chinese ? "战斗提示" : "Combat notices", p => !_preferences.For(p).CombatNotices,
                (p, off) => _preferences.For(p).CombatNotices = !off);
            AddToggle(5, chinese ? "新手提示" : "Beginner tips", p => !_preferences.For(p).BeginnerTips,
                (p, off) => _preferences.For(p).BeginnerTips = !off);
            AddToggle(6, chinese ? "QQ 社区通知" : "QQ community line", p => !_preferences.For(p).Community,
                (p, off) => _preferences.For(p).Community = !off);

            MethodInfo enable = blockType.GetMethod("Enable", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(blockType.FullName, "Enable");
            enable.Invoke(_block, Array.Empty<object>());
            _enabled = true;
            Logger.Info("[StatsBots:SSS] Registered personalized title selector and five display controls.");
            return true;
        }
        catch (Exception ex)
        {
            bool released = TryDisableBlock();
            if (released) _requestRefresh = null;
            return Unavailable(ex.GetBaseException().Message + (released ? string.Empty : "; rollback could not release the claimed block"));
        }
    }

    public void Disable()
    {
        if (_block == null)
        {
            _enabled = false;
            _requestRefresh = null;
            _toggleBaselines.Clear();
            return;
        }
        if (TryDisableBlock())
        {
            _requestRefresh = null;
            _toggleBaselines.Clear();
        }
    }

    public void RequestRefresh(Player player, string reason)
    {
        if (_requestRefresh == null || player == null) return;
        try { _requestRefresh.Invoke(null, new object[] { player, "statsbots:" + reason }); }
        catch (Exception ex) { Logger.Warn("[StatsBots:SSS] Player refresh failed: " + ex.GetBaseException().Message); }
    }

    private Delegate BuildModelDelegate(Type delegateType, Type modelType)
    {
        ConstructorInfo? constructor = modelType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(ctor => ctor.GetParameters().Length == 5);
        if (constructor == null) throw new MissingMethodException(modelType.FullName, ".ctor(label, options, defaultIndex, hint, visible)");

        ParameterExpression player = Expression.Parameter(typeof(Player), "player");
        ParameterExpression descriptor = Expression.Variable(typeof(DropdownDescriptor), "descriptor");
        MethodInfo builder = GetType().GetMethod(nameof(BuildDescriptor), BindingFlags.NonPublic | BindingFlags.Instance)!;
        NewExpression result = Expression.New(constructor,
            Expression.Property(descriptor, nameof(DropdownDescriptor.Label)),
            Expression.Convert(Expression.Property(descriptor, nameof(DropdownDescriptor.Options)), constructor.GetParameters()[1].ParameterType),
            Expression.Property(descriptor, nameof(DropdownDescriptor.DefaultIndex)),
            Expression.Property(descriptor, nameof(DropdownDescriptor.Hint)),
            Expression.Property(descriptor, nameof(DropdownDescriptor.Visible)));
        BlockExpression body = Expression.Block(new[] { descriptor }, Expression.Assign(descriptor, Expression.Call(Expression.Constant(this), builder, player)), result);
        return Expression.Lambda(delegateType, body, player).Compile();
    }

    private Delegate BuildSelectionDelegate(Type delegateType, Type selectionType)
    {
        ParameterExpression player = Expression.Parameter(typeof(Player), "player");
        ParameterExpression selection = Expression.Parameter(selectionType, "selection");
        PropertyInfo value = selectionType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(selectionType.FullName, "Value");
        MethodInfo handler = GetType().GetMethod(nameof(OnTitleSelection), BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodCallExpression body = Expression.Call(Expression.Constant(this), handler, player, Expression.Property(selection, value));
        return Expression.Lambda(delegateType, body, player, selection).Compile();
    }

    private DropdownDescriptor BuildDescriptor(Player player)
    {
        bool chinese = _text.Chinese(player);
        ProviderState state = _runtime.TryGetUnlockedTitles(player, out IReadOnlyList<TitleConfig> unlocked, out long selectedCode);
        if (state != ProviderState.Ready)
        {
            string stateLabel = state == ProviderState.Loading
                ? (chinese ? "数据加载中…" : "Stats loading…")
                : (chinese ? "数据不可用" : "Stats unavailable");
            return new DropdownDescriptor(
                chinese ? "选择热身称号" : "Warmup title",
                new[] { stateLabel },
                0,
                chinese ? "数据验证完成前不会显示虚假选项。" : "No unverified options are shown.",
                true);
        }
        var options = new List<string> { chinese ? "无称号" : "No title" };
        options.AddRange(unlocked.Select(title => FormatOption(title, chinese)));
        int selected = 0;
        for (int i = 0; i < unlocked.Count; i++) if (unlocked[i].Code == selectedCode) selected = i + 1;
        return new DropdownDescriptor(
            chinese ? "选择热身称号" : "Warmup title",
            options.ToArray(),
            selected,
            chinese ? "仅显示已解锁称号；选择后立即保存。" : "Only unlocked titles are listed; a deliberate change saves immediately.",
            true);
    }

    private void OnTitleSelection(Player player, string value)
    {
        if (player == null || string.IsNullOrWhiteSpace(value)) return;
        string none = _text.Chinese(player) ? "无称号" : "No title";
        if (string.Equals(value, none, StringComparison.Ordinal))
        {
            _runtime.TrySelect(player, "none", out _);
            return;
        }

        if (_runtime.TryGetUnlockedTitles(player, out IReadOnlyList<TitleConfig> unlocked, out _) != ProviderState.Ready)
        {
            RequestRefresh(player, "title-provider-not-ready");
            return;
        }
        TitleConfig? selected = unlocked.FirstOrDefault(title => string.Equals(FormatOption(title, _text.Chinese(player)), value, StringComparison.Ordinal));
        if (selected == null)
        {
            Logger.Warn("[StatsBots:SSS] Rejected forged, stale, or locked title selection from " + player.UserId);
            RequestRefresh(player, "rejected-title");
            return;
        }
        if (_runtime.TrySelect(player, selected.Id, out string response) != ProviderState.Ready)
            Logger.Warn("[StatsBots:SSS] Title selection rejected for " + player.UserId + ": " + response);
    }

    private static string FormatOption(TitleConfig title, bool chinese)
        => Localization.EscapeRichText(chinese ? title.Chinese : title.English) + " [" + title.Id + "]";

    private void AddToggle(int local, string label, Func<Player, bool> isOff, Action<Player, bool> onChanged)
    {
        MethodInfo? method = _block!.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "AddTwoButtons" && m.GetParameters().Length == 8);
        if (method == null) throw new MissingMethodException("ServerKeybinds.KeybindBlock", "AddTwoButtons per-player default overload");
        bool chinese = !string.Equals(_config.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase);
        Action<Player, bool> baselineSafe = (player, isB) =>
        {
            if (player?.ReferenceHub == null || _toggleBaselines.Add((player.ReferenceHub, local))) return;
            onChanged(player, isB);
        };
        method.Invoke(_block, new object[]
        {
            local, label, chinese ? "开启" : "On", chinese ? "关闭" : "Off", isOff, false,
            chinese ? "更改后立即生效。" : "Changes apply immediately.", baselineSafe,
        });
    }

    private void InvokeFluent(string methodName, object value)
    {
        MethodInfo? method = _block!.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1);
        if (method == null) throw new MissingMethodException(_block.GetType().FullName, methodName);
        method.Invoke(_block, new[] { value });
    }

    private bool Unavailable(string reason)
    {
        if (!_loggedUnavailable)
        {
            _loggedUnavailable = true;
            Logger.Warn("[StatsBots:SSS] " + reason + ". The HUD/broadcast/RA features remain available; title selection falls back to the player command.");
        }
        return false;
    }

    private bool TryDisableBlock()
    {
        if (_block == null)
        {
            _enabled = false;
            return true;
        }
        try
        {
            MethodInfo disable = _block.GetType().GetMethod("Disable", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(_block.GetType().FullName, "Disable");
            disable.Invoke(_block, Array.Empty<object>());
            _block = null;
            _enabled = false;
            return true;
        }
        catch (Exception ex)
        {
            _enabled = false;
            Logger.Warn("[StatsBots:SSS] Disable/rollback failed; the block handle is retained for retry: " + ex.GetBaseException().Message);
            return false;
        }
    }

    private sealed class DropdownDescriptor
    {
        public DropdownDescriptor(string label, string[] options, int defaultIndex, string hint, bool visible)
        {
            Label = label; Options = options; DefaultIndex = defaultIndex; Hint = hint; Visible = visible;
        }
        public string Label { get; }
        public string[] Options { get; }
        public int DefaultIndex { get; }
        public string Hint { get; }
        public bool Visible { get; }
    }
}
