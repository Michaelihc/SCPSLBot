using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SCPSLBot.Presentation
{
    /// <summary>
    /// Reflection-backed HintServiceMeow adapter. HSM is optional at assembly-load time, while all
    /// HSM/Mirror operations and expiry callbacks stay on the Unity main thread through MEC.
    /// </summary>
    internal sealed class HsmHintDisplayProvider : IHintDisplayProvider
    {
        private const string HsmAssemblyName = "HintServiceMeow";
        private const string PlayerDisplayTypeName = "HintServiceMeow.Core.Utilities.PlayerDisplay";
        private const string HintTypeName = "HintServiceMeow.Core.Models.Hints.Hint";
        private const string AbstractHintTypeName = "HintServiceMeow.Core.Models.Hints.AbstractHint";
        private const string AlignmentTypeName = "HintServiceMeow.Core.Enum.HintAlignment";
        private const string VerticalAlignmentTypeName = "HintServiceMeow.Core.Enum.HintVerticalAlign";
        private const string SyncSpeedTypeName = "HintServiceMeow.Core.Enum.HintSyncSpeed";
        private const string LogPrefix = "[SCPSLBot:Hints]";

        private readonly HintDisplayConfig config;
        private readonly Dictionary<(ReferenceHub Hub, string TagId), ActiveHint> activeHints = new();

        private ConstructorInfo hintConstructor;
        private MethodInfo getDisplayMethod;
        private MethodInfo addHintMethod;
        private MethodInfo removeHintMethod;
        private MethodInfo forceUpdateMethod;
        private PropertyInfo idProperty;
        private PropertyInfo textProperty;
        private PropertyInfo xProperty;
        private PropertyInfo yProperty;
        private PropertyInfo fontSizeProperty;
        private PropertyInfo lineHeightProperty;
        private PropertyInfo alignmentProperty;
        private PropertyInfo verticalAlignmentProperty;
        private PropertyInfo syncSpeedProperty;
        private object centerAlignment;
        private object middleAlignment;
        private object fastSync;
        private bool available;
        private bool initialized;
        private bool eventsRegistered;
        private bool loggedUnavailable;
        private bool loggedAvailable;
        private string unavailableReason;

        public HsmHintDisplayProvider(HintDisplayConfig config)
        {
            this.config = config;
        }

        public string Name => "hsm";

        public void Enable()
        {
            if (!TryInitialize())
            {
                return;
            }

            if (!eventsRegistered)
            {
                PlayerEvents.Left += OnPlayerLeft;
                eventsRegistered = true;
            }
        }

        public void Disable()
        {
            if (eventsRegistered)
            {
                PlayerEvents.Left -= OnPlayerLeft;
                eventsRegistered = false;
            }

            foreach ((ReferenceHub hub, string tagId) in activeHints.Keys.ToArray())
            {
                Remove(hub, tagId, false);
            }

            activeHints.Clear();
        }

        public void Show(Player player, in HintRequest request)
        {
            if (!available || !IsDisplayable(player))
            {
                return;
            }

            if (request.Duration <= 0f)
            {
                Remove(player, request.TagId);
                return;
            }

            object display = GetDisplay(player);
            if (display == null)
            {
                return;
            }

            string tagId = NormalizeTag(request.TagId);
            (ReferenceHub Hub, string TagId) key = (player.ReferenceHub, tagId);
            bool created = !activeHints.TryGetValue(key, out ActiveHint active);
            if (created)
            {
                object hint = CreateHint(tagId);
                if (hint == null)
                {
                    return;
                }

                active = new ActiveHint(display, hint);
                activeHints.Add(key, active);
            }
            else
            {
                active.Display = display;
                active.CancelRemoval();
            }

            UpdateHint(active.Hint, request);
            if (created && !Invoke(() => addHintMethod.Invoke(display, new[] { active.Hint, GroupName })))
            {
                activeHints.Remove(key);
                return;
            }

            ScheduleRemoval(key, active, request.Duration);
            ForceUpdate(display, config.ForceFastUpdates);
        }

        public void Remove(Player player, string tagId)
        {
            if (player?.ReferenceHub != null)
            {
                Remove(player.ReferenceHub, NormalizeTag(tagId), true);
            }
        }

        public void Clear(Player player)
        {
            if (player?.ReferenceHub == null)
            {
                return;
            }

            ReferenceHub hub = player.ReferenceHub;
            object display = null;
            string[] tags = activeHints.Keys
                .Where(key => key.Hub == hub)
                .Select(key => key.TagId)
                .ToArray();

            foreach (string tag in tags)
            {
                if (activeHints.TryGetValue((hub, tag), out ActiveHint active))
                {
                    display = active.Display;
                }

                Remove(hub, tag, false);
            }

            ForceUpdate(display, true);
        }

        public bool TryInitialize(bool logResult = true)
        {
            if (available)
            {
                LogAvailable(logResult);
                return true;
            }

            if (initialized)
            {
                if (logResult && unavailableReason != null)
                {
                    LogUnavailable(unavailableReason);
                }

                return false;
            }

            initialized = true;
            try
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, HsmAssemblyName, StringComparison.OrdinalIgnoreCase));
                if (assembly == null)
                {
                    return Fail("HintServiceMeow.dll is not loaded.", logResult);
                }

                Type displayType = assembly.GetType(PlayerDisplayTypeName);
                Type hintType = assembly.GetType(HintTypeName);
                Type abstractHintType = assembly.GetType(AbstractHintTypeName);
                Type alignmentType = assembly.GetType(AlignmentTypeName);
                Type verticalType = assembly.GetType(VerticalAlignmentTypeName);
                Type syncType = assembly.GetType(SyncSpeedTypeName);
                if (displayType == null || hintType == null || abstractHintType == null ||
                    alignmentType == null || verticalType == null || syncType == null)
                {
                    return Fail("HintServiceMeow API types are incompatible.", logResult);
                }

                hintConstructor = hintType.GetConstructor(Type.EmptyTypes);
                getDisplayMethod = displayType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null);
                addHintMethod = displayType.GetMethod("AddHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { abstractHintType, typeof(string) }, null);
                removeHintMethod = displayType.GetMethod("RemoveHint", BindingFlags.Public | BindingFlags.Instance, null, new[] { abstractHintType, typeof(string) }, null);
                forceUpdateMethod = displayType.GetMethod("ForceUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                idProperty = abstractHintType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                textProperty = abstractHintType.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                fontSizeProperty = abstractHintType.GetProperty("FontSize", BindingFlags.Public | BindingFlags.Instance);
                lineHeightProperty = abstractHintType.GetProperty("LineHeight", BindingFlags.Public | BindingFlags.Instance);
                syncSpeedProperty = abstractHintType.GetProperty("SyncSpeed", BindingFlags.Public | BindingFlags.Instance);
                xProperty = hintType.GetProperty("XCoordinate", BindingFlags.Public | BindingFlags.Instance);
                yProperty = hintType.GetProperty("YCoordinate", BindingFlags.Public | BindingFlags.Instance);
                alignmentProperty = hintType.GetProperty("Alignment", BindingFlags.Public | BindingFlags.Instance);
                verticalAlignmentProperty = hintType.GetProperty("YCoordinateAlign", BindingFlags.Public | BindingFlags.Instance);
                centerAlignment = Enum.Parse(alignmentType, "Center");
                middleAlignment = Enum.Parse(verticalType, "Middle");
                fastSync = Enum.Parse(syncType, "Fast");

                if (hintConstructor == null || getDisplayMethod == null || addHintMethod == null ||
                    removeHintMethod == null || forceUpdateMethod == null || idProperty == null ||
                    textProperty == null || fontSizeProperty == null || lineHeightProperty == null ||
                    syncSpeedProperty == null || xProperty == null || yProperty == null ||
                    alignmentProperty == null || verticalAlignmentProperty == null)
                {
                    return Fail("HintServiceMeow API members are incompatible.", logResult);
                }

                available = true;
                LogAvailable(logResult);
                return true;
            }
            catch (Exception exception)
            {
                return Fail($"HintServiceMeow initialization failed: {exception.GetBaseException().Message}", logResult);
            }
        }

        private object CreateHint(string tagId)
        {
            try
            {
                object hint = hintConstructor.Invoke(Array.Empty<object>());
                idProperty.SetValue(hint, tagId);
                alignmentProperty.SetValue(hint, centerAlignment);
                verticalAlignmentProperty.SetValue(hint, middleAlignment);
                syncSpeedProperty.SetValue(hint, fastSync);
                return hint;
            }
            catch (Exception exception)
            {
                Logger.Error($"{LogPrefix} Failed to construct an HSM hint: {exception.GetBaseException().Message}");
                return null;
            }
        }

        private void UpdateHint(object hint, in HintRequest request)
        {
            textProperty.SetValue(hint, request.Message ?? string.Empty);
            xProperty.SetValue(hint, request.X);
            yProperty.SetValue(hint, request.Y);
            fontSizeProperty.SetValue(hint, Math.Max(6, request.TextSize));
            lineHeightProperty.SetValue(hint, 12f);
        }

        private object GetDisplay(Player player)
        {
            try
            {
                return getDisplayMethod.Invoke(null, new object[] { player });
            }
            catch (Exception exception)
            {
                Logger.Error($"{LogPrefix} Failed to resolve the HSM player display: {exception.GetBaseException().Message}");
                return null;
            }
        }

        private void ScheduleRemoval((ReferenceHub Hub, string TagId) key, ActiveHint expected, float duration)
        {
            CoroutineHandle handle = Timing.CallDelayed(Math.Max(0f, duration), () =>
            {
                if (activeHints.TryGetValue(key, out ActiveHint current) && ReferenceEquals(current, expected))
                {
                    Remove(key.Hub, key.TagId, true);
                }
            });
            expected.SetRemoval(handle);
        }

        private void Remove(ReferenceHub hub, string tagId, bool forceUpdate)
        {
            if (!activeHints.TryGetValue((hub, tagId), out ActiveHint active))
            {
                return;
            }

            activeHints.Remove((hub, tagId));
            active.CancelRemoval();
            Invoke(() => removeHintMethod.Invoke(active.Display, new[] { active.Hint, GroupName }));
            if (forceUpdate)
            {
                ForceUpdate(active.Display, true);
            }
        }

        private void ForceUpdate(object display, bool fast)
        {
            if (!config.ForceFastUpdates || display == null)
            {
                return;
            }

            Invoke(() => forceUpdateMethod.Invoke(display, new object[] { fast }));
        }

        private void OnPlayerLeft(PlayerLeftEventArgs ev) => Clear(ev.Player);

        private string NormalizeTag(string tagId)
        {
            string prefix = string.IsNullOrWhiteSpace(config.TagPrefix) ? "scpslbot." : config.TagPrefix;
            string value = tagId ?? string.Empty;
            return value.StartsWith(prefix, StringComparison.Ordinal) ? value : prefix + value;
        }

        private string GroupName => string.IsNullOrWhiteSpace(config.GroupName) ? "scpslbot.hints" : config.GroupName;

        private bool Fail(string reason, bool logResult)
        {
            unavailableReason = reason;
            if (logResult)
            {
                LogUnavailable(reason);
            }

            return false;
        }

        private void LogUnavailable(string message)
        {
            if (loggedUnavailable)
            {
                return;
            }

            loggedUnavailable = true;
            Logger.Warn($"{LogPrefix} {message}");
        }

        private void LogAvailable(bool logResult)
        {
            if (!logResult || loggedAvailable)
            {
                return;
            }

            loggedAvailable = true;
            Logger.Info($"{LogPrefix} HintServiceMeow detected; hints use stable HSM tags.");
        }

        private static bool IsDisplayable(Player player) => player?.ReferenceHub != null &&
            !player.IsDestroyed && (player.IsDummy || (player.IsPlayer && player.IsReady));

        private static bool Invoke(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error($"{LogPrefix} HSM invocation failed: {exception.GetBaseException().Message}");
                return false;
            }
        }

        private sealed class ActiveHint
        {
            private CoroutineHandle removal;

            public ActiveHint(object display, object hint)
            {
                Display = display;
                Hint = hint;
            }

            public object Display { get; set; }

            public object Hint { get; }

            public void SetRemoval(CoroutineHandle handle)
            {
                CancelRemoval();
                removal = handle;
            }

            public void CancelRemoval()
            {
                if (removal.IsRunning)
                {
                    Timing.KillCoroutines(removal);
                }

                removal = default;
            }
        }
    }
}
