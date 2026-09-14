using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;
using UserSettings.ServerSpecific;

namespace ServerKeybinds;

/// <summary>
/// One plugin's claimed 1000-wide id block. Build it fluently with headers, keybinds, dropdowns, and sliders,
/// then <see cref="Enable"/> in the plugin's Enable and <see cref="Disable"/> in its Disable. Local ids are
/// 0..999 within the block; 0 is conventionally the group header. Obtain one via
/// <see cref="KeybindRegistry.ClaimBlock"/>.
/// </summary>
public sealed class KeybindBlock
{
    internal readonly int BaseId;
    internal readonly string Owner;
    internal readonly List<HeaderEntry> Headers = new();
    internal readonly List<TextEntry> Texts = new();
    internal readonly Dictionary<int, Binding> Bindings = new();
    internal readonly Dictionary<int, ValueSetting> ValueSettings = new();
    internal Func<Player, bool>? VisibilityFilter;
    internal SettingsCategory Category = SettingsCategory.Other;
    internal int SortOrder;
    internal bool Active;

    internal KeybindBlock(int baseId, string owner)
    {
        BaseId = baseId;
        Owner = owner;
    }

    /// <summary>Adds the block's group header at local id 0 (shown above its settings in the SSS menu).</summary>
    public KeybindBlock Header(string groupName) => Header(0, groupName);

    /// <summary>
    /// Restricts this entire block to players accepted by <paramref name="predicate"/>. Hidden players
    /// receive no entries from the block and their forged responses are ignored server-side.
    /// </summary>
    public KeybindBlock VisibleTo(Func<Player, bool> predicate)
    {
        VisibilityFilter = predicate ?? throw new ArgumentNullException(nameof(predicate));
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Files this block under a <see cref="SettingsCategory"/>, which is what decides where it lands in the
    /// player's settings menu. Blocks are ordered by (category, base id), so a category's members are always
    /// adjacent and their order is stable regardless of plugin load order.
    ///
    /// Purely presentational: it changes no setting id, so a player's saved values survive re-categorising.
    /// </summary>
    public KeybindBlock InCategory(SettingsCategory category)
    {
        // A C# enum accepts ANY cast integer, and the category becomes a synthesised header id at
        // RegistryHeaders + (int)category. An undeclared value therefore invents a header the registry
        // does not know to strip on the next rebuild (duplicates accumulate), and a large or negative one
        // can land the header inside a plugin's own 1000-wide block. Fail at claim time instead.
        if (Array.IndexOf(SssIdBlocks.AllCategories, category) < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "Unknown SettingsCategory. Add it to SssIdBlocks.AllCategories before using it.");
        }

        Category = category;
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Sorts this block within its category. Lower comes first; the default 0 leaves a block ordered by its
    /// base id, which is arbitrary. Use a negative value to PIN the settings players reach for most to the
    /// top - "somewhere in the Gameplay section" is not good enough for a key nothing works without.
    /// </summary>
    public KeybindBlock Order(int sortOrder)
    {
        SortOrder = sortOrder;
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Adds a read-only block of text. It renders directly under the block's header, ABOVE its keybinds and
    /// settings, which is where an explanation is worth anything. <c>SSTextArea</c> has
    /// <c>UserResponseMode.None</c>, so it costs no client response and can never be forged back at us.
    /// </summary>
    public KeybindBlock AddTextArea(
        int local,
        string content,
        SSTextArea.FoldoutMode foldout = SSTextArea.FoldoutMode.NotCollapsable,
        string? collapsedText = null)
    {
        ValidateAvailableValueLocal(local);
        Texts.Add(new TextEntry(local, content, foldout, collapsedText));
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Adds a group header at an explicit local id (for a block that hosts several visual groups).</summary>
    public KeybindBlock Header(int local, string groupName)
    {
        ValidateLocal(local);
        Headers.Add(new HeaderEntry(local, groupName));
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Registers a keybind at <paramref name="local"/> (1..999). <paramref name="onPressed"/> fires on the
    /// rising edge; the optional <paramref name="onReleased"/> fires on the falling edge (for hold-to-act).
    /// </summary>
    public KeybindBlock Add(
        int local,
        string label,
        KeyCode defaultKey,
        string hint,
        Action<Player> onPressed,
        Action<Player>? onReleased = null,
        bool preventInteractionOnGui = true,
        bool allowSpectatorTrigger = false)
    {
        ValidateLocal(local);
        if (local == 0)
        {
            throw new ArgumentException("Local id 0 is reserved for the group header; use Header(name).", nameof(local));
        }

        if (IsLocalUsed(local))
        {
            throw new ArgumentException($"Local id {local} already used in block '{Owner}' ({BaseId}).", nameof(local));
        }

        Bindings[local] = new Binding(local, label, defaultKey, hint, onPressed, onReleased, preventInteractionOnGui, allowSpectatorTrigger);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Registers a shared-registry dropdown and invokes <paramref name="onChanged"/> with its validated index.</summary>
    public KeybindBlock AddDropdown(
        int local,
        string label,
        string[] options,
        int defaultIndex,
        string hint,
        Action<Player, int> onChanged,
        SSDropdownSetting.DropdownEntryType entryType = SSDropdownSetting.DropdownEntryType.ScrollableLoop)
    {
        ValidateAvailableValueLocal(local);
        if (options == null || options.Length == 0)
        {
            throw new ArgumentException("A dropdown must contain at least one option.", nameof(options));
        }

        ValueSettings[local] = new DropdownSetting(
            local, label, options, Mathf.Clamp(defaultIndex, 0, options.Length - 1), entryType, hint, onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Registers a personalized, regular (non-scrollable) dropdown. The resolver runs only while building a
    /// recipient's candidate view and must be cheap and side-effect free. Return <see cref="DropdownModel.Hidden"/>
    /// to omit the setting. Acquisition after every send generation becomes the baseline and never calls the
    /// action; only a later validated change invokes <paramref name="onChanged"/> once. A staging-only workflow
    /// may opt into <paramref name="onAcquired"/> so the value visibly selected by the client can be staged
    /// without being executed.
    ///
    /// The optional fallback exists only in the shared <c>DefinedSettings</c> array so the native server will
    /// prevalidate this id/type. Personalized recipients receive the resolver's model, never this fallback.
    /// </summary>
    public KeybindBlock AddDropdownForPlayer(
        int local,
        Func<Player, DropdownModel> modelForPlayer,
        Action<Player, DropdownSelection> onChanged,
        DropdownModel? fallbackModel = null,
        Action<Player, DropdownSelection>? onAcquired = null)
    {
        ValidateAvailableValueLocal(local);
        ValueSettings[local] = new PersonalizedDropdownSetting(
            local,
            modelForPlayer ?? throw new ArgumentNullException(nameof(modelForPlayer)),
            onChanged ?? throw new ArgumentNullException(nameof(onChanged)),
            fallbackModel,
            onAcquired);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Registers a personalized native button. The resolver controls its per-player label, caption,
    /// hold time, hint, and visibility. Button presses carry no client-authored choice value, so the
    /// callback must execute only server-side state that was previously staged and revalidated.
    /// </summary>
    public KeybindBlock AddButtonForPlayer(
        int local,
        Func<Player, ButtonModel> modelForPlayer,
        Action<Player> onPressed,
        ButtonModel? fallbackModel = null)
    {
        ValidateAvailableValueLocal(local);
        ValueSettings[local] = new PersonalizedButtonSetting(
            local,
            modelForPlayer ?? throw new ArgumentNullException(nameof(modelForPlayer)),
            onPressed ?? throw new ArgumentNullException(nameof(onPressed)),
            fallbackModel);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// Registers a native two-button toggle and invokes <paramref name="onChanged"/> with <c>true</c> when
    /// the player selects <paramref name="optionB"/>. This is the right control for an on/off switch — a
    /// two-option dropdown works but reads as a list the player has to open.
    ///
    /// NOTE the client's PlayerPrefs key for a setting is
    /// <c>SrvSp_&lt;server&gt;_&lt;typeCode&gt;_&lt;settingId&gt;</c>, and the TYPE CODE is part of it
    /// (<c>ServerSpecificSettingBase.GeneratePrefsKey</c>). Converting an existing dropdown to this type
    /// therefore resets it to <paramref name="defaultIsB"/> for every player who had already chosen a value.
    /// Do it deliberately, not as a drive-by tidy-up.
    /// </summary>
    public KeybindBlock AddTwoButtons(
        int local,
        string label,
        string optionA,
        string optionB,
        bool defaultIsB,
        string hint,
        Action<Player, bool> onChanged)
    {
        ValidateAvailableValueLocal(local);
        if (string.IsNullOrEmpty(optionA) || string.IsNullOrEmpty(optionB))
        {
            throw new ArgumentException("Both button captions must be non-empty.", nameof(optionA));
        }

        ValueSettings[local] = new TwoButtonsSetting(local, label, optionA, optionB, defaultIsB, hint, onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>
    /// A two-button toggle whose STARTING POSITION is decided per player.
    ///
    /// <c>DefaultIsB</c> is written into the entry by <c>SSTwoButtonsSetting.SerializeEntry</c>, and the
    /// registry rebuilds its entries for every personalised send, so each player can be given a different
    /// default. That is what lets a setting be "on for newcomers, off for everyone else" while still being
    /// a switch either of them can flip - which a fixed default cannot express.
    ///
    /// The resolver runs once per send per player, so keep it cheap and side-effect free. It is not called
    /// for the shared <c>DefinedSettings</c> array, which uses <paramref name="fallbackIsB"/>; players never
    /// receive that array (the registry suppresses the native join send and pushes a personalised one), so
    /// the fallback only shapes what other plugins see when they read the array.
    ///
    /// NOTE the client reports its value on ACQUISITION as well as on change, so a callback is not proof
    /// the player touched anything. To tell an explicit choice from an untouched default, compare the value
    /// you receive against the default you handed that player.
    /// </summary>
    public KeybindBlock AddTwoButtons(
        int local,
        string label,
        string optionA,
        string optionB,
        Func<Player, bool> defaultIsBFor,
        bool fallbackIsB,
        string hint,
        Action<Player, bool> onChanged)
    {
        ValidateAvailableValueLocal(local);
        if (string.IsNullOrEmpty(optionA) || string.IsNullOrEmpty(optionB))
        {
            throw new ArgumentException("Both button captions must be non-empty.", nameof(optionA));
        }

        ValueSettings[local] = new TwoButtonsSetting(
            local,
            label,
            optionA,
            optionB,
            fallbackIsB,
            defaultIsBFor ?? throw new ArgumentNullException(nameof(defaultIsBFor)),
            hint,
            onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>Registers a shared-registry slider and invokes <paramref name="onChanged"/> with its validated value.</summary>
    public KeybindBlock AddSlider(
        int local,
        string label,
        float minValue,
        float maxValue,
        float defaultValue,
        bool integer,
        string valueToStringFormat,
        string finalDisplayFormat,
        string hint,
        Action<Player, float> onChanged)
    {
        ValidateAvailableValueLocal(local);
        if (maxValue < minValue)
        {
            throw new ArgumentException("Slider maximum must be greater than or equal to its minimum.", nameof(maxValue));
        }

        ValueSettings[local] = new SliderSetting(
            local, label, minValue, maxValue, Mathf.Clamp(defaultValue, minValue, maxValue), integer,
            valueToStringFormat, finalDisplayFormat, hint, onChanged);
        KeybindRegistry.OnBlockChanged(this);
        return this;
    }

    /// <summary>The absolute SSS id for a local id (base + local) — e.g. for sibling services rendering key glyphs.</summary>
    public int SettingId(int local)
    {
        ValidateLocal(local);
        return BaseId + local;
    }

    /// <summary>Merges this block's headers and settings into the shared <c>DefinedSettings</c> and broadcasts.</summary>
    public void Enable() => KeybindRegistry.EnableBlock(this);

    /// <summary>Removes this block's settings from the shared <c>DefinedSettings</c> and broadcasts.</summary>
    public void Disable() => KeybindRegistry.DisableBlock(this);

    /// <param name="player">
    /// The recipient, when this is a personalised send. Null for the shared array, where any per-player
    /// default falls back to its fixed value.
    /// </param>
    internal IEnumerable<ServerSpecificSettingBase> BuildSettings(Player? player = null)
    {
        foreach (HeaderEntry header in Headers)
        {
            // reducedPadding: the registry always emits a category header immediately above this one, so a
            // block header is a SUB-heading now. Full padding under a category title reads as a gap, and the
            // native SSPagesExample uses the same flag for exactly this "subcategory" case.
            yield return new SSGroupHeader(BaseId + header.Local, header.Name, reducedPadding: true);
        }

        foreach (TextEntry text in Texts)
        {
            yield return new SSTextArea(BaseId + text.Local, text.Content, text.Foldout, text.CollapsedText);
        }

        foreach (Binding binding in Bindings.Values)
        {
            yield return new SSKeybindSetting(
                BaseId + binding.Local,
                binding.Label,
                binding.DefaultKey,
                preventInteractionOnGui: binding.PreventInteractionOnGui,
                allowSpectatorTrigger: binding.AllowSpectatorTrigger,
                hint: binding.Hint);
        }

        foreach (ValueSetting setting in ValueSettings.Values)
        {
            ServerSpecificSettingBase? built = setting.Build(BaseId + setting.Local, player);
            if (built != null)
            {
                yield return built;
            }
        }
    }

    internal IEnumerable<int> OwnedIds()
    {
        foreach (HeaderEntry header in Headers)
        {
            yield return BaseId + header.Local;
        }

        foreach (TextEntry text in Texts)
        {
            yield return BaseId + text.Local;
        }

        foreach (Binding binding in Bindings.Values)
        {
            yield return BaseId + binding.Local;
        }


        foreach (ValueSetting setting in ValueSettings.Values)
        {
            yield return BaseId + setting.Local;
        }
    }

    internal bool IsVisibleTo(Player player) => VisibilityFilter?.Invoke(player) != false;

    private bool IsLocalUsed(int local) =>
        Bindings.ContainsKey(local)
        || ValueSettings.ContainsKey(local)
        || Headers.Exists(h => h.Local == local)
        || Texts.Exists(t => t.Local == local);

    private void ValidateAvailableValueLocal(int local)
    {
        ValidateLocal(local);
        if (local == 0)
        {
            throw new ArgumentException("Local id 0 is reserved for the group header; use Header(name).", nameof(local));
        }

        if (IsLocalUsed(local))
        {
            throw new ArgumentException($"Local id {local} already used in block '{Owner}' ({BaseId}).", nameof(local));
        }
    }

    private static void ValidateLocal(int local)
    {
        if (local < 0 || local >= SssIdBlocks.BlockWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(local), local, $"Local id must be 0..{SssIdBlocks.BlockWidth - 1}.");
        }
    }

    internal readonly struct HeaderEntry
    {
        public HeaderEntry(int local, string name)
        {
            Local = local;
            Name = name;
        }

        public int Local { get; }

        public string Name { get; }
    }

    internal readonly struct TextEntry
    {
        public TextEntry(int local, string content, SSTextArea.FoldoutMode foldout, string? collapsedText)
        {
            Local = local;
            Content = content;
            Foldout = foldout;
            CollapsedText = collapsedText;
        }

        public int Local { get; }

        public string Content { get; }

        public SSTextArea.FoldoutMode Foldout { get; }

        public string? CollapsedText { get; }
    }

    internal sealed class Binding
    {
        public Binding(int local, string label, KeyCode defaultKey, string hint, Action<Player> onPressed, Action<Player>? onReleased, bool preventInteractionOnGui, bool allowSpectatorTrigger)
        {
            Local = local;
            Label = label;
            DefaultKey = defaultKey;
            Hint = hint;
            OnPressed = onPressed;
            OnReleased = onReleased;
            PreventInteractionOnGui = preventInteractionOnGui;
            AllowSpectatorTrigger = allowSpectatorTrigger;
        }

        public int Local { get; }

        public string Label { get; }

        public KeyCode DefaultKey { get; }

        public string Hint { get; }

        public Action<Player> OnPressed { get; }

        public Action<Player>? OnReleased { get; }

        public bool PreventInteractionOnGui { get; }

        public bool AllowSpectatorTrigger { get; }
    }

    internal abstract class ValueSetting
    {
        protected ValueSetting(int local, string label, string hint)
        {
            Local = local;
            Label = label;
            Hint = hint;
        }

        public int Local { get; }

        public string Label { get; }

        protected string Hint { get; }

        public abstract ServerSpecificSettingBase? Build(int absoluteId, Player? player);

        public abstract void Invoke(Player player, ServerSpecificSettingBase setting);
    }

    private sealed class DropdownSetting : ValueSetting
    {
        private readonly string[] _options;
        private readonly int _defaultIndex;
        private readonly SSDropdownSetting.DropdownEntryType _entryType;
        private readonly Action<Player, int> _onChanged;

        public DropdownSetting(int local, string label, string[] options, int defaultIndex, SSDropdownSetting.DropdownEntryType entryType, string hint, Action<Player, int> onChanged)
            : base(local, label, hint)
        {
            _options = options;
            _defaultIndex = defaultIndex;
            _entryType = entryType;
            _onChanged = onChanged;
        }

        public override ServerSpecificSettingBase Build(int absoluteId, Player? player) =>
            new SSDropdownSetting(absoluteId, Label, _options, _defaultIndex, _entryType, Hint);

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSDropdownSetting dropdown)
            {
                _onChanged(player, Mathf.Clamp(dropdown.SyncSelectionIndexValidated, 0, _options.Length - 1));
            }
        }
    }

    internal sealed class PersonalizedDropdownSetting : ValueSetting
    {
        private static readonly DropdownModel ValidationFallback = new(string.Empty, new[] { string.Empty });
        private readonly Func<Player, DropdownModel> _modelForPlayer;
        private readonly Action<Player, DropdownSelection> _onChanged;
        private readonly Action<Player, DropdownSelection>? _onAcquired;
        private readonly DropdownModel _fallback;

        public PersonalizedDropdownSetting(
            int local,
            Func<Player, DropdownModel> modelForPlayer,
            Action<Player, DropdownSelection> onChanged,
            DropdownModel? fallback,
            Action<Player, DropdownSelection>? onAcquired)
            : base(local, fallback?.Label ?? string.Empty, fallback?.Hint ?? string.Empty)
        {
            _modelForPlayer = modelForPlayer;
            _onChanged = onChanged;
            _onAcquired = onAcquired;
            _fallback = fallback is { Visible: true } ? fallback : ValidationFallback;
        }

        public override ServerSpecificSettingBase? Build(int absoluteId, Player? player)
        {
            DropdownModel model = player == null ? _fallback : Resolve(player);
            if (player != null && !model.Visible)
            {
                return null;
            }

            return new SSDropdownSetting(
                absoluteId,
                model.Label,
                System.Linq.Enumerable.ToArray(model.Options),
                model.DefaultIndex,
                SSDropdownSetting.DropdownEntryType.Regular,
                model.Hint);
        }

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSDropdownSetting dropdown
                && KeybindRegistry.TryTakePersonalizedDropdownResponse(
                    player,
                    dropdown.SettingId,
                    dropdown.SyncSelectionIndexRaw,
                    out DropdownSelection selection,
                    out PersonalizedDropdownResponseKind responseKind)
                && IsStillValid(player, selection))
            {
                if (responseKind == PersonalizedDropdownResponseKind.Acquisition)
                {
                    _onAcquired?.Invoke(player, selection);
                }
                else
                {
                    _onChanged(player, selection);
                }
            }
        }

        private bool IsStillValid(Player player, DropdownSelection selection)
        {
            DropdownModel current = Resolve(player);
            return current.Visible
                && selection.Index >= 0
                && selection.Index < current.Options.Count
                && string.Equals(current.Options[selection.Index], selection.Value, StringComparison.Ordinal);
        }

        private DropdownModel Resolve(Player player)
        {
            try
            {
                return _modelForPlayer(player) ?? DropdownModel.Hidden;
            }
            catch
            {
                // A presentation resolver cannot be allowed to cost the recipient their entire SSS pack.
                return DropdownModel.Hidden;
            }
        }
    }

    internal sealed class PersonalizedButtonSetting : ValueSetting
    {
        private static readonly ButtonModel ValidationFallback = new(string.Empty, string.Empty);
        private readonly Func<Player, ButtonModel> _modelForPlayer;
        private readonly Action<Player> _onPressed;
        private readonly ButtonModel _fallback;

        public PersonalizedButtonSetting(
            int local,
            Func<Player, ButtonModel> modelForPlayer,
            Action<Player> onPressed,
            ButtonModel? fallback)
            : base(local, fallback?.Label ?? string.Empty, fallback?.Hint ?? string.Empty)
        {
            _modelForPlayer = modelForPlayer;
            _onPressed = onPressed;
            _fallback = fallback is { Visible: true } ? fallback : ValidationFallback;
        }

        public override ServerSpecificSettingBase? Build(int absoluteId, Player? player)
        {
            ButtonModel model = player == null ? _fallback : Resolve(player);
            if (player != null && !model.Visible)
            {
                return null;
            }

            return new SSButton(
                absoluteId,
                model.Label,
                model.ButtonText,
                model.HoldTimeSeconds,
                model.Hint);
        }

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSButton && Resolve(player).Visible)
            {
                _onPressed(player);
            }
        }

        private ButtonModel Resolve(Player player)
        {
            try
            {
                return _modelForPlayer(player) ?? ButtonModel.Hidden;
            }
            catch
            {
                return ButtonModel.Hidden;
            }
        }
    }

    internal sealed class TwoButtonsSetting : ValueSetting
    {
        private readonly string _optionA;
        private readonly string _optionB;
        private readonly bool _defaultIsB;
        private readonly Action<Player, bool> _onChanged;

        private readonly Func<Player, bool>? _defaultIsBFor;

        public TwoButtonsSetting(int local, string label, string optionA, string optionB, bool defaultIsB, string hint, Action<Player, bool> onChanged)
            : this(local, label, optionA, optionB, defaultIsB, null, hint, onChanged)
        {
        }

        public TwoButtonsSetting(int local, string label, string optionA, string optionB, bool defaultIsB, Func<Player, bool>? defaultIsBFor, string hint, Action<Player, bool> onChanged)
            : base(local, label, hint)
        {
            _optionA = optionA;
            _optionB = optionB;
            _defaultIsB = defaultIsB;
            _defaultIsBFor = defaultIsBFor;
            _onChanged = onChanged;
        }

        /// <summary>The starting position this player should be sent, or the fixed one for the shared array.</summary>
        public bool DefaultFor(Player? player)
        {
            if (_defaultIsBFor == null || player == null)
            {
                return _defaultIsB;
            }

            try
            {
                return _defaultIsBFor(player);
            }
            catch
            {
                // A throwing resolver must not cost the player the whole settings array.
                return _defaultIsB;
            }
        }

        public override ServerSpecificSettingBase Build(int absoluteId, Player? player) =>
            new SSTwoButtonsSetting(absoluteId, Label, _optionA, _optionB, DefaultFor(player), Hint);

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSTwoButtonsSetting twoButtons)
            {
                _onChanged(player, twoButtons.SyncIsB);
            }
        }
    }

    private sealed class SliderSetting : ValueSetting
    {
        private readonly float _min;
        private readonly float _max;
        private readonly float _defaultValue;
        private readonly bool _integer;
        private readonly string _valueFormat;
        private readonly string _displayFormat;
        private readonly Action<Player, float> _onChanged;

        public SliderSetting(int local, string label, float min, float max, float defaultValue, bool integer, string valueFormat, string displayFormat, string hint, Action<Player, float> onChanged)
            : base(local, label, hint)
        {
            _min = min;
            _max = max;
            _defaultValue = defaultValue;
            _integer = integer;
            _valueFormat = valueFormat;
            _displayFormat = displayFormat;
            _onChanged = onChanged;
        }

        public override ServerSpecificSettingBase Build(int absoluteId, Player? player) =>
            new SSSliderSetting(absoluteId, Label, _min, _max, _defaultValue, _integer, _valueFormat, _displayFormat, Hint);

        public override void Invoke(Player player, ServerSpecificSettingBase setting)
        {
            if (setting is SSSliderSetting slider)
            {
                _onChanged(player, Mathf.Clamp(slider.SyncFloatValue, _min, _max));
            }
        }
    }
}
