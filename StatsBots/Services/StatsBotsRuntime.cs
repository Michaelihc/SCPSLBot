using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Extensions;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using StatsBots.Config;
using StatsBots.Core;
using StatsBots.Integration;

namespace StatsBots.Services;

internal sealed class StatsBotsRuntime
{
    private const int PendingPerUserLimit = 64;
    private const int PendingGlobalLimit = 512;
    private const string GhostTail = "<color=#00000000><mspace=1em>                                             </mspace></color>";
    private static readonly string[] SnapshotKeys =
    {
        StatsKeys.BotKills, StatsKeys.BotDeaths, StatsKeys.Score, StatsKeys.CurrentStreak,
        StatsKeys.BestStreak, StatsKeys.SelectedTagCode,
    };

    private readonly StatsBotsConfig _config;
    private readonly StatsSystemAdapter _stats;
    private readonly ScpslBotAdapter _bots;
    private readonly IHintDisplayProvider _hints;
    private readonly Localization _text;
    private readonly PlayerPreferences _preferences;
    private readonly DeathEventDeduplicator _duplicates;
    private readonly Dictionary<ReferenceHub, double> _joinedAt = new();
    private readonly Dictionary<ReferenceHub, AnnouncementSession> _announcements = new();
    private readonly Dictionary<string, Queue<ScoreMutation>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<ReferenceHub, string> _lastHero = new();
    private readonly Dictionary<ReferenceHub, ProviderState> _providerStates = new();
    private readonly object _mutationGate = new();
    private CoroutineHandle _loop;
    private ServerKeybindsAdapter? _sss;
    private int _pendingCount;
    private bool _enabled;

    public StatsBotsRuntime(StatsBotsConfig config, StatsSystemAdapter stats, ScpslBotAdapter bots, IHintDisplayProvider hints,
        Localization text, PlayerPreferences preferences)
    {
        _config = config;
        _stats = stats;
        _bots = bots;
        _hints = hints;
        _text = text;
        _preferences = preferences;
        long window = (long)(Stopwatch.Frequency * (config.DuplicateEventWindowMilliseconds / 1000d));
        _duplicates = new DeathEventDeduplicator(window);
    }

    public PlayerPreferences Preferences => _preferences;
    public StatsSystemAdapter Stats => _stats;

    public void SetSss(ServerKeybindsAdapter sss) => _sss = sss;

    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        PlayerEvents.Joined += OnJoined;
        PlayerEvents.Left += OnLeft;
        PlayerEvents.Death += OnDeath;
        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundRestarted += OnRoundRestarted;
        foreach (Player player in Player.ReadyList.Where(IsAuthenticatedReal)) AddSession(player);
        _loop = Timing.RunCoroutine(Run());
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        try { Timing.KillCoroutines(_loop); }
        catch (Exception ex) { Logger.Warn("[StatsBots] Coroutine cleanup failed: " + ex.GetBaseException().Message); }
        try
        {
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            ServerEvents.RoundStarted -= OnRoundStarted;
            PlayerEvents.Death -= OnDeath;
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.Joined -= OnJoined;
        }
        catch (Exception ex) { Logger.Warn("[StatsBots] Event cleanup failed: " + ex.GetBaseException().Message); }
        try
        {
            foreach (Player player in Player.ReadyList.ToArray())
            {
                try { _hints.Clear(player); }
                catch (Exception ex) { Logger.Warn("[StatsBots] Player hint cleanup failed: " + ex.GetBaseException().Message); }
            }
        }
        catch (Exception ex) { Logger.Warn("[StatsBots] Player enumeration during cleanup failed: " + ex.GetBaseException().Message); }
        try { _stats.Flush(); }
        catch (Exception ex) { Logger.Warn("[StatsBots] Provider flush failed: " + ex.GetBaseException().Message); }
        finally
        {
            _joinedAt.Clear();
            _announcements.Clear();
            _pending.Clear();
            _pendingCount = 0;
            _lastHero.Clear();
            _providerStates.Clear();
            _preferences.Clear();
            _duplicates.Clear();
        }
    }

    public ProviderState TryGrant(string userId, string titleId, out string response)
    {
        if (!AuthenticatedIdentity.TryNormalize(userId, out userId))
        {
            response = "A full authenticated UserId such as 7656119...@steam is required.";
            return ProviderState.Unavailable;
        }
        TitleConfig? title = TitleCatalog.ById(_config.Titles, titleId);
        if (title == null)
        {
            response = "Unknown title id. Available: " + string.Join(", ", _config.Titles.Select(t => t.Id));
            return ProviderState.Unavailable;
        }
        ProviderState state = PrepareOfflineRecord(userId);
        if (state != ProviderState.Ready)
        {
            response = ProviderMessage(state);
            return state;
        }
        state = _stats.Set(userId, StatsKeys.TagUnlocked(title.Id), 1);
        response = state == ProviderState.Ready ? $"Granted '{title.Id}' to {userId}." : ProviderMessage(state);
        if (state == ProviderState.Ready) RefreshOnline(userId, "title-grant");
        return state;
    }

    public ProviderState TryRevoke(string userId, string titleId, out string response)
    {
        if (!AuthenticatedIdentity.TryNormalize(userId, out userId))
        {
            response = "A full authenticated UserId is required.";
            return ProviderState.Unavailable;
        }
        TitleConfig? title = TitleCatalog.ById(_config.Titles, titleId);
        if (title == null)
        {
            response = "Unknown title id. Available: " + string.Join(", ", _config.Titles.Select(t => t.Id));
            return ProviderState.Unavailable;
        }
        ProviderState state = PrepareOfflineRecord(userId);
        if (state != ProviderState.Ready)
        {
            response = ProviderMessage(state);
            return state;
        }
        state = _stats.Set(userId, StatsKeys.TagUnlocked(title.Id), -1);
        if (state != ProviderState.Ready)
        {
            response = ProviderMessage(state);
            return state;
        }
        ProviderState readState = ReadRecord(userId, out StatsRecord? record);
        if (readState != ProviderState.Ready)
        {
            response = $"Revoked '{title.Id}' from {userId}, but selected-title cleanup could not be verified: {ProviderMessage(readState)}";
            return readState;
        }
        if (record!.Counter(StatsKeys.SelectedTagCode) == title.Code)
        {
            ProviderState clearState = _stats.Set(userId, StatsKeys.SelectedTagCode, 0);
            if (clearState != ProviderState.Ready)
            {
                response = $"Revoked '{title.Id}' from {userId}, but the selected-title code could not be cleared: {ProviderMessage(clearState)}";
                return clearState;
            }
        }
        response = $"Revoked '{title.Id}' from {userId}. The operation is idempotent.";
        RefreshOnline(userId, "title-revoke");
        return ProviderState.Ready;
    }

    public ProviderState TrySelect(Player player, string titleId, out string response)
    {
        if (!IsAuthenticatedReal(player) || !AuthenticatedIdentity.TryNormalize(player.UserId, out string userId))
        {
            response = _text.Pick(player, "Authenticated real players only.", "仅限已认证的真实玩家。" );
            return ProviderState.Unavailable;
        }
        ProviderState state = ReadRecord(userId, out StatsRecord? record);
        if (state != ProviderState.Ready)
        {
            response = _text.Pick(player, "Stats are " + state.ToString().ToLowerInvariant() + ".", state == ProviderState.Loading ? "数据加载中。" : "数据提供器不可用。" );
            return state;
        }
        if (string.Equals(titleId, "none", StringComparison.OrdinalIgnoreCase) || titleId == "0")
        {
            state = _stats.Set(userId, StatsKeys.SelectedTagCode, 0);
            if (state != ProviderState.Ready)
            {
                response = _text.Pick(player,
                    state == ProviderState.Loading ? "Stats are still loading; the title was not changed." : "Stats are unavailable; the title was not changed.",
                    state == ProviderState.Loading ? "数据仍在加载，称号未更改。" : "数据提供器不可用，称号未更改。" );
                return state;
            }
            response = _text.Pick(player, "Warmup title hidden.", "已隐藏热身称号。" );
            RefreshOnline(userId, "title-select-none");
            return ProviderState.Ready;
        }
        TitleConfig? title = TitleCatalog.ById(_config.Titles, titleId);
        if (title == null || !TitleCatalog.IsUnlocked(title, record!.Counter(StatsKeys.Score), record.Counter(StatsKeys.TagUnlocked(title.Id))))
        {
            response = _text.Pick(player, "That title is locked or no longer exists.", "该称号尚未解锁或已从目录移除。" );
            return ProviderState.Unavailable;
        }
        state = _stats.Set(userId, StatsKeys.SelectedTagCode, title.Code);
        if (state != ProviderState.Ready)
        {
            response = _text.Pick(player,
                state == ProviderState.Loading ? "Stats are still loading; the title was not changed." : "Stats are unavailable; the title was not changed.",
                state == ProviderState.Loading ? "数据仍在加载，称号未更改。" : "数据提供器不可用，称号未更改。" );
            return state;
        }
        response = _text.Pick(player,
            "Selected title: " + Localization.EscapeRichText(title.English),
            "已选择称号：" + Localization.EscapeRichText(title.Chinese));
        RefreshOnline(userId, "title-select");
        return ProviderState.Ready;
    }

    public string TitleStatus(string userId)
    {
        if (!AuthenticatedIdentity.TryNormalize(userId, out userId)) return "A full authenticated UserId is required.";
        ProviderState hydrate = _stats.EnsureOfflineHydrated(userId);
        ProviderState state = ReadRecord(userId, out StatsRecord? record);
        if (state != ProviderState.Ready)
        {
            if (state == ProviderState.Loading && hydrate == ProviderState.Ready)
                return $"user={userId} provider=ready record=absent (no zero-value substitute was used)";
            return ProviderMessage(state == ProviderState.Loading ? hydrate : state);
        }
        long score = record!.Counter(StatsKeys.Score);
        long selected = record.Counter(StatsKeys.SelectedTagCode);
        TitleConfig? selectedTitle = TitleCatalog.ByCode(_config.Titles, selected);
        string selectedLabel = selected == 0 ? "none" : selectedTitle?.Id ?? $"removed-code:{selected}";
        string unlocked = string.Join(", ", _config.Titles
            .Where(title => TitleCatalog.IsUnlocked(title, score, record.Counter(StatsKeys.TagUnlocked(title.Id))))
            .Select(title => title.Id));
        return $"user={userId} provider=ready score={score} kills={record.Counter(StatsKeys.BotKills)} deaths={record.Counter(StatsKeys.BotDeaths)} streak={record.Counter(StatsKeys.CurrentStreak)} best={record.Counter(StatsKeys.BestStreak)} selected={selectedLabel} unlocked=[{unlocked}]";
    }

    public ProviderState TryGetUnlockedTitles(Player player, out IReadOnlyList<TitleConfig> titles, out long selectedCode)
    {
        selectedCode = 0;
        titles = Array.Empty<TitleConfig>();
        if (!IsAuthenticatedReal(player)) return ProviderState.Unavailable;
        ProviderState state = ReadRecord(player.UserId, out StatsRecord? record);
        if (state != ProviderState.Ready) return state;
        selectedCode = record!.Counter(StatsKeys.SelectedTagCode);
        long score = record.Counter(StatsKeys.Score);
        titles = _config.Titles.Where(title => TitleCatalog.IsUnlocked(title, score, record.Counter(StatsKeys.TagUnlocked(title.Id)))).ToArray();
        return ProviderState.Ready;
    }

    public string Localize(Player player, string english, string chinese) => _text.Pick(player, english, chinese);

    public void RefreshHud(Player player)
    {
        if (!IsAuthenticatedReal(player)) return;
        ProviderState state = ReadOrInitializeOnlineRecord(player, out StatsRecord? record);
        if (_providerStates.TryGetValue(player.ReferenceHub, out ProviderState previousState)
            && previousState != state)
        {
            _sss?.RequestRefresh(player, "stats-provider-state-changed");
        }
        _providerStates[player.ReferenceHub] = state;

        DisplayPreferences prefs = _preferences.For(player);
        if (!prefs.Hud)
        {
            _hints.Remove(player, "hero");
            _hints.Remove(player, "footer");
            _lastHero.Remove(player.ReferenceHub);
            return;
        }

        string hero;
        string footer;
        if (state == ProviderState.Loading)
        {
            hero = _text.Pick(player,
                "<color=#ffd24d>LOADING</color> · StatsSystem\nVerified values pending",
                "<color=#ffd24d>加载中</color> · StatsSystem\n等待已验证数据" );
            footer = _text.Pick(player, "WAIT · provider loading", "等待 · 数据加载中");
        }
        else if (state == ProviderState.Unavailable)
        {
            hero = _text.Pick(player,
                "<color=#ff5555>UNAVAILABLE</color> · StatsSystem\nNo unverified zero values",
                "<color=#ff5555>不可用</color> · StatsSystem\n不显示未经验证的零值" );
            footer = _text.Pick(player, "BLOCKED · provider offline", "受阻 · 数据提供器离线");
        }
        else
        {
            long score = Math.Max(0, record!.Counter(StatsKeys.Score));
            TierConfig tier = TierCatalog.Resolve(_config.Tiers, score);
            long? next = TierCatalog.NextThreshold(_config.Tiers, score);
            long selectedCode = record.Counter(StatsKeys.SelectedTagCode);
            TitleConfig? selected = TitleCatalog.ByCode(_config.Titles, selectedCode);
            bool selectedUnlocked = selected != null && TitleCatalog.IsUnlocked(selected, score, record.Counter(StatsKeys.TagUnlocked(selected.Id)));
            string progress = next.HasValue ? Compact(score) + "/" + Compact(next.Value) : Compact(score) + "/MAX";
            string difficulty = Localization.EscapeRichText(ShortLabel(_bots.Difficulty, 6));
            string tierShort = Localization.EscapeRichText(ShortLabel(_text.Chinese(player) ? tier.Chinese : tier.English, 8));
            string titleShort = prefs.Title && selectedUnlocked
                ? Localization.EscapeRichText(ShortLabel(_text.Chinese(player) ? selected!.Chinese : selected!.English, 8))
                : "—";
            string targets = _bots.LiveBotCount.HasValue ? Compact(_bots.LiveBotCount.Value) : "--";
            hero = _text.Pick(player,
                $"<color=#4fcbff>{tierShort}</color> · <color=#e7ecf3>{titleShort}</color> · {progress}\nK{Compact(record.Counter(StatsKeys.BotKills))} D{Compact(record.Counter(StatsKeys.BotDeaths))} · S{Compact(record.Counter(StatsKeys.CurrentStreak))}/{Compact(record.Counter(StatsKeys.BestStreak))} · B{targets} · {difficulty}",
                $"<color=#4fcbff>{tierShort}</color> · <color=#e7ecf3>{titleShort}</color> · {progress}\n杀{Compact(record.Counter(StatsKeys.BotKills))} 死{Compact(record.Counter(StatsKeys.BotDeaths))} · 连{Compact(record.Counter(StatsKeys.CurrentStreak))}/{Compact(record.Counter(StatsKeys.BestStreak))} · {targets}机 · {difficulty}");
            footer = _text.Pick(player, "SSS · Choose an unlocked title", "SSS · 选择已解锁称号" );
        }

        hero = PadRows(hero);
        footer = PadRows(footer);
        string snapshot = hero + "\n" + footer;
        if (!_lastHero.TryGetValue(player.ReferenceHub, out string prior) || prior != snapshot)
        {
            _hints.Show(player, "hero", _config.HintDisplay.DefaultX, _config.HintDisplay.HeroY, _config.HintDisplay.HeroTextSize, hero);
            _hints.Show(player, "footer", _config.HintDisplay.DefaultX, _config.HintDisplay.FooterY, _config.HintDisplay.FooterTextSize, footer);
            if (_hints.IsAvailable) _lastHero[player.ReferenceHub] = snapshot;
        }
    }

    private IEnumerator<float> Run()
    {
        while (true)
        {
            yield return Timing.WaitForSeconds(_config.HudPollSeconds);
            try
            {
                FlushPending();
                double now = NowSeconds;
                foreach (Player player in Player.ReadyList.Where(IsAuthenticatedReal).ToArray())
                {
                    RefreshHud(player);
                    TickAnnouncements(player, now);
                }
            }
            catch (Exception ex) { Logger.Error("[StatsBots] Runtime loop recovered from: " + ex); }
        }
    }

    private void OnJoined(PlayerJoinedEventArgs ev) => AddSession(ev.Player);

    private void AddSession(Player player)
    {
        if (!IsAuthenticatedReal(player)) return;
        double now = NowSeconds;
        _joinedAt[player.ReferenceHub] = now;
        _announcements[player.ReferenceHub] = new AnnouncementSession(player.UserId, now,
            new NoticeCadence(now, _config.SetupNoticeDelaySeconds, _config.TipIntervalSeconds, _config.CommunityIntervalSeconds),
            _config.Tips.Count);
        RefreshHud(player);
    }

    private void OnLeft(PlayerLeftEventArgs ev)
    {
        if (ev.Player?.ReferenceHub == null) return;
        _joinedAt.Remove(ev.Player.ReferenceHub);
        _announcements.Remove(ev.Player.ReferenceHub);
        _lastHero.Remove(ev.Player.ReferenceHub);
        _providerStates.Remove(ev.Player.ReferenceHub);
        _preferences.Remove(ev.Player);
        _hints.Clear(ev.Player);
    }

    private void OnRoundStarted()
    {
        foreach (AnnouncementSession session in _announcements.Values) session.Cadence.RequestCommunity();
    }

    private void OnRoundRestarted()
    {
        _duplicates.Clear();
        foreach (AnnouncementSession session in _announcements.Values) session.Cadence.RequestCommunity();
    }

    private void OnDeath(PlayerDeathEventArgs ev)
    {
        if (ev.Player == null || ev.DamageHandler == null) return;
        CombatActorKind attackerKind = Classify(ev.Attacker);
        CombatActorKind victimKind = Classify(ev.Player);
        bool self = ev.Attacker?.ReferenceHub != null && ev.Attacker.ReferenceHub == ev.Player.ReferenceHub;
        bool teamKill = ev.Attacker != null && ev.Attacker.Role.GetFaction() == ev.OldRole.GetFaction();
        ScoreMutation mutation = ScoringMatrix.Evaluate(new ScoringInput(attackerKind, victimKind, self, teamKill), _config.ScorePerBotKill);
        if (!mutation.HasChanges) return;
        var fingerprint = new DeathFingerprint(ev.Player.NetworkId, RuntimeHelpers.GetHashCode(ev.DamageHandler));
        if (!_duplicates.TryAccept(fingerprint, Stopwatch.GetTimestamp())) return;

        Player beneficiary = attackerKind == CombatActorKind.RealAuthenticated ? ev.Attacker! : ev.Player;
        if (!AuthenticatedIdentity.TryNormalize(beneficiary.UserId, out string userId)) return;
        MutationDisposition disposition = QueueOrApply(userId, mutation);
        DisplayPreferences prefs = _preferences.For(beneficiary);
        if (prefs.CombatNotices)
        {
            string flash = CombatFlash(beneficiary, mutation, disposition);
            _hints.Show(beneficiary, "flash", _config.HintDisplay.DefaultX, _config.HintDisplay.NoticeY, _config.HintDisplay.NoticeTextSize, PadRows(flash), 2.5f);
        }
        RefreshHud(beneficiary);
    }

    private MutationDisposition QueueOrApply(string userId, ScoreMutation mutation)
    {
        lock (_mutationGate)
        {
            MutationDisposition result = TryApply(userId, mutation);
            if (result != MutationDisposition.Deferred) return result;
            if (!_pending.TryGetValue(userId, out Queue<ScoreMutation> queue))
            {
                queue = new Queue<ScoreMutation>();
                _pending[userId] = queue;
            }
            if (queue.Count < PendingPerUserLimit && _pendingCount < PendingGlobalLimit)
            {
                queue.Enqueue(mutation);
                _pendingCount++;
                return MutationDisposition.Deferred;
            }
            if (queue.Count == 0) _pending.Remove(userId);
            Logger.Warn("[StatsBots] Pending scoring queue reached a bounded cap; newest event was dropped for " + userId);
            return MutationDisposition.Dropped;
        }
    }

    private void FlushPending()
    {
        lock (_mutationGate)
        {
            foreach (string userId in _pending.Keys.ToArray())
            {
                Queue<ScoreMutation> queue = _pending[userId];
                while (queue.Count > 0)
                {
                    MutationDisposition result = TryApply(userId, queue.Peek());
                    if (result == MutationDisposition.Deferred) break;
                    queue.Dequeue();
                    _pendingCount--;
                }
                if (queue.Count == 0) _pending.Remove(userId);
            }
        }
    }

    private MutationDisposition TryApply(string userId, ScoreMutation mutation)
    {
        ProviderState state = ReadRecord(userId, out StatsRecord? record);
        if (state == ProviderState.Loading && HydrationGraceElapsed(userId))
        {
            ProviderState hydration = _stats.EnsureOfflineHydrated(userId);
            if (hydration == ProviderState.Ready)
            {
                state = ReadRecord(userId, out record);
                if (state == ProviderState.Loading)
                {
                    state = _stats.TryEnsureRecord(userId);
                    if (state == ProviderState.Ready) state = ReadRecord(userId, out record);
                }
            }
            else
            {
                state = hydration;
            }
        }
        if (state != ProviderState.Ready || record == null) return MutationDisposition.Deferred;

        bool committed = true;
        if (mutation.BotKillsDelta != 0) committed &= _stats.Increment(userId, StatsKeys.BotKills, mutation.BotKillsDelta) == ProviderState.Ready;
        if (mutation.BotDeathsDelta != 0) committed &= _stats.Increment(userId, StatsKeys.BotDeaths, mutation.BotDeathsDelta) == ProviderState.Ready;
        if (mutation.ScoreDelta != 0)
        {
            long currentScore = Math.Max(0, record.Counter(StatsKeys.Score));
            long scoreDelta = ScoringMatrix.ClampScore(currentScore, mutation.ScoreDelta) - currentScore;
            if (scoreDelta != 0) committed &= _stats.Increment(userId, StatsKeys.Score, scoreDelta) == ProviderState.Ready;
        }
        long streak = mutation.ResetCurrentStreak
            ? 0
            : ScoringMatrix.ClampScore(record.Counter(StatsKeys.CurrentStreak), mutation.CurrentStreakDelta);
        committed &= _stats.Set(userId, StatsKeys.CurrentStreak, streak) == ProviderState.Ready;
        if (streak > record.Counter(StatsKeys.BestStreak))
            committed &= _stats.Set(userId, StatsKeys.BestStreak, streak) == ProviderState.Ready;
        if (!committed)
        {
            Logger.Error("[StatsBots] A multi-key score mutation reported provider failure for " + userId + ". It will not be replayed because the provider has no atomic receipt and a partial write may already exist.");
            return MutationDisposition.Failed;
        }
        return MutationDisposition.Applied;
    }

    private bool HydrationGraceElapsed(string userId)
    {
        Player? online = Player.Get(userId);
        if (online?.ReferenceHub == null || !_joinedAt.TryGetValue(online.ReferenceHub, out double joined)) return true;
        return NowSeconds - joined >= _config.ProviderHydrationGraceSeconds;
    }

    private string CombatFlash(Player player, ScoreMutation mutation, MutationDisposition disposition)
    {
        return disposition switch
        {
            MutationDisposition.Applied when mutation.BotKillsDelta > 0 => _text.Pick(player,
                $"<color=#5bff80>+{Compact(mutation.ScoreDelta)} SCORE</color> · BOT KILL",
                $"<color=#5bff80>+{Compact(mutation.ScoreDelta)} 积分</color> · 击杀机器人"),
            MutationDisposition.Applied => _text.Pick(player,
                "<color=#ff5555>STREAK 0</color> · BOT DEATH",
                "<color=#ff5555>连杀归零</color> · 被机器人击杀"),
            MutationDisposition.Deferred => _text.Pick(player,
                "<color=#ffd24d>PENDING</color> · stats provider",
                "<color=#ffd24d>待处理</color> · 数据提供器"),
            MutationDisposition.Dropped => _text.Pick(player,
                "<color=#ff5555>DROPPED</color> · queue full",
                "<color=#ff5555>未记录</color> · 队列已满"),
            _ => _text.Pick(player,
                "<color=#ff5555>FAILED</color> · write uncertain",
                "<color=#ff5555>失败</color> · 写入状态不确定"),
        };
    }

    private static string PadRows(string message)
        => string.Join("\n", (message ?? string.Empty).Split('\n').Select(static row => row + GhostTail));

    private static string ShortLabel(string? value, int maxCharacters)
    {
        string clean = (value ?? "--").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return "--";
        return clean.Length <= maxCharacters ? clean : clean.Substring(0, maxCharacters);
    }

    private static string Compact(long value)
    {
        value = Math.Max(0, value);
        if (value < 10_000) return value.ToString(CultureInfo.InvariantCulture);
        if (value < 1_000_000) return (value / 1_000).ToString(CultureInfo.InvariantCulture) + "K";
        if (value < 1_000_000_000) return (value / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M";
        if (value < 1_000_000_000_000) return (value / 1_000_000_000).ToString(CultureInfo.InvariantCulture) + "B";
        if (value < 1_000_000_000_000_000) return (value / 1_000_000_000_000).ToString(CultureInfo.InvariantCulture) + "T";
        if (value < 1_000_000_000_000_000_000) return (value / 1_000_000_000_000_000).ToString(CultureInfo.InvariantCulture) + "Q";
        return (value / 1_000_000_000_000_000_000).ToString(CultureInfo.InvariantCulture) + "E";
    }

    private void TickAnnouncements(Player player, double now)
    {
        if (!_announcements.TryGetValue(player.ReferenceHub, out AnnouncementSession session)) return;
        ProviderState state = _stats.TryRead(player.UserId, Array.Empty<string>(), out StatsRecord? record);
        bool beginner = false;
        if (state == ProviderState.Ready && record!.TotalPlayTime.HasValue)
        {
            TimeSpan effective = session.Playtime.Observe(record.TotalPlayTime.Value, now);
            beginner = BeginnerEligibility.IsEligible(effective, TimeSpan.Zero, TimeSpan.FromSeconds(_config.BeginnerThresholdSeconds));
        }
        DisplayPreferences prefs = _preferences.For(player);
        NoticeKind kind = session.Cadence.TakeNext(now, beginner, prefs.BeginnerTips, prefs.Community);
        if (kind == NoticeKind.None) return;

        string message;
        int duration;
        switch (kind)
        {
            case NoticeKind.Setup:
                message = _text.Setup(player);
                duration = _config.SetupNoticeDurationSeconds;
                break;
            case NoticeKind.Community:
                message = _text.Community(player);
                duration = _config.CommunityDurationSeconds;
                break;
            case NoticeKind.Tip:
                message = _text.Pick(player, _config.Tips[session.Tips.Next()]);
                duration = _config.TipDurationSeconds;
                break;
            default: return;
        }
        player.SendBroadcast(Localization.EscapeRichText(message), (ushort)duration, global::Broadcast.BroadcastFlags.Normal, shouldClearPrevious: false);
        session.Cadence.MarkOccupied(now, duration, _config.BroadcastGapSeconds);
    }

    private ProviderState ReadRecord(string userId, out StatsRecord? record)
    {
        IEnumerable<string> keys = SnapshotKeys.Concat(_config.Titles.Select(title => StatsKeys.TagUnlocked(title.Id)));
        return _stats.TryRead(userId, keys, out record);
    }

    private ProviderState ReadOrInitializeOnlineRecord(Player player, out StatsRecord? record)
    {
        ProviderState state = ReadRecord(player.UserId, out record);
        if (state != ProviderState.Loading)
        {
            return state;
        }

        ProviderState hydration = _stats.EnsureOfflineHydrated(player.UserId);
        if (hydration != ProviderState.Ready)
        {
            return hydration;
        }

        state = ReadRecord(player.UserId, out record);
        if (state != ProviderState.Loading)
        {
            return state;
        }

        state = _stats.TryEnsureRecord(player.UserId);
        return state == ProviderState.Ready
            ? ReadRecord(player.UserId, out record)
            : state;
    }

    private ProviderState PrepareOfflineRecord(string userId)
    {
        return _stats.EnsureOfflineHydrated(userId);
    }

    private void RefreshOnline(string userId, string reason)
    {
        Player? player = Player.Get(userId);
        if (player == null) return;
        _lastHero.Remove(player.ReferenceHub);
        RefreshHud(player);
        _sss?.RequestRefresh(player, reason);
    }

    private CombatActorKind Classify(Player? player)
    {
        if (player == null) return CombatActorKind.Other;
        if (_bots.IsManagedBot(player)) return CombatActorKind.ManagedBot;
        return IsAuthenticatedReal(player) ? CombatActorKind.RealAuthenticated : CombatActorKind.Other;
    }

    private static bool IsAuthenticatedReal(Player? player)
        => player is { IsPlayer: true, IsReady: true, IsDummy: false, IsHost: false, DoNotTrack: false }
           && AuthenticatedIdentity.IsFullUserId(player.UserId);

    private string ProviderMessage(ProviderState state) => state == ProviderState.Loading
        ? "StatsSystem data is still loading; no zero-value substitute was used."
        : "StatsSystem provider is unavailable: " + _stats.LastFailure;

    private static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private sealed class AnnouncementSession
    {
        public AnnouncementSession(string userId, double joinedAt, NoticeCadence cadence, int tipCount)
        {
            UserId = userId;
            JoinedAt = joinedAt;
            Cadence = cadence;
            Playtime = new VerifiedPlaytimeTracker(joinedAt);
            Tips = new TipShuffle(userId, tipCount);
        }
        public string UserId { get; }
        public double JoinedAt { get; }
        public NoticeCadence Cadence { get; }
        public VerifiedPlaytimeTracker Playtime { get; }
        public TipShuffle Tips { get; }
    }

    private enum MutationDisposition
    {
        Applied,
        Deferred,
        Dropped,
        Failed,
    }
}
