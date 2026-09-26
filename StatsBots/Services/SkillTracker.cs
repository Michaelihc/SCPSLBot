using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerStatsSystem;
using StatsBots.Config;
using StatsBots.Core;
using StatsBots.Integration;

namespace StatsBots.Services;

/// <summary>
/// Samples firearm combat against managed bots and persists it as exponentially decayed Warmup.Skill.* counters.
/// Scoring/titles stay in <see cref="StatsBotsRuntime"/>; this class owns only the skill rating.
/// </summary>
internal sealed class SkillTracker
{
    private readonly SkillRatingConfig _config;
    private readonly StatsSystemAdapter _stats;
    private readonly ScpslBotAdapter _bots;
    private readonly Func<Player?, bool> _isAuthenticatedReal;
    private readonly Dictionary<ReferenceHub, Session> _sessions = new();
    private double _nextFlush;
    private bool _enabled;

    public SkillTracker(SkillRatingConfig config, StatsSystemAdapter stats, ScpslBotAdapter bots, Func<Player?, bool> isAuthenticatedReal)
    {
        _config = config;
        _stats = stats;
        _bots = bots;
        _isAuthenticatedReal = isAuthenticatedReal;
    }

    public bool Enabled => _config.Enabled;

    public void Enable()
    {
        if (_enabled || !_config.Enabled) return;
        _enabled = true;
        _nextFlush = NowSeconds + _config.FlushSeconds;
        PlayerEvents.Hurt += OnHurt;
        PlayerEvents.ShotWeapon += OnShotWeapon;
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        PlayerEvents.ShotWeapon -= OnShotWeapon;
        PlayerEvents.Hurt -= OnHurt;
        foreach (Session session in _sessions.Values.ToArray()) Flush(session);
        _sessions.Clear();
    }

    /// <summary>Called by the runtime after its duplicate filter accepted a real-player → managed-bot kill.</summary>
    public void RecordKill(Player attacker, DamageHandlerBase handler)
    {
        if (!_enabled || handler is not FirearmDamageHandler || !attacker.IsHuman) return;
        SessionFor(attacker)?.Sampler.Kill(NowSeconds);
    }

    /// <summary>Called by the runtime after its duplicate filter accepted a managed-bot → real-player kill.</summary>
    public void RecordDeath(Player victim, RoleTypeId oldRole)
    {
        if (!_enabled || !oldRole.IsHuman()) return;
        SessionFor(victim)?.Sampler.Death(NowSeconds);
    }

    public void Tick()
    {
        if (!_enabled) return;
        double now = NowSeconds;
        if (now < _nextFlush) return;
        _nextFlush = now + _config.FlushSeconds;
        foreach (Session session in _sessions.Values.ToArray())
            if (session.Sampler.HasPending) Flush(session);
    }

    public void OnLeft(Player player)
    {
        if (player?.ReferenceHub == null || !_sessions.TryGetValue(player.ReferenceHub, out Session session)) return;
        Flush(session);
        _sessions.Remove(player.ReferenceHub);
    }

    /// <summary>Stored decayed totals plus this session's unsaved sample.</summary>
    public SkillResult Evaluate(Player? online, StatsRecord record) => SkillMath.Evaluate(Current(online, record, out _), _config);

    public SkillSample Current(Player? online, StatsRecord record, out SkillSample pending)
    {
        pending = default;
        if (online?.ReferenceHub != null && _sessions.TryGetValue(online.ReferenceHub, out Session session))
            pending = session.Sampler.Pending;
        return SkillKeys.Current(record.Counter, NowUnix, _config.HalfLifeSeconds).Add(pending);
    }

    public TierConfig Rank(SkillResult result) => TierCatalog.Resolve(_config.Ranks, result.Rating);

    private void OnHurt(PlayerHurtEventArgs ev)
    {
        try
        {
            Player victim = ev.Player;
            Player? attacker = ev.Attacker;
            if (victim?.ReferenceHub == null || attacker?.ReferenceHub == null || attacker.ReferenceHub == victim.ReferenceHub) return;
            double now = NowSeconds;
            bool attackerBot = _bots.IsManagedBot(attacker);
            bool victimBot = _bots.IsManagedBot(victim);

            if (!attackerBot && !victimBot)
            {
                if (IsHumanControlled(attacker) && IsHumanControlled(victim))
                {
                    SessionFor(attacker)?.Sampler.PlayerVersusPlayer(now);
                    SessionFor(victim)?.Sampler.PlayerVersusPlayer(now);
                }
                return;
            }

            if (victimBot && !attackerBot && ev.DamageHandler is FirearmDamageHandler && attacker.IsHuman)
            {
                Session? session = SessionFor(attacker);
                if (session == null) return;
                session.HitFrame = UnityEngine.Time.frameCount;
                session.Sampler.BotInteraction(now);
            }
            else if (attackerBot && !victimBot && victim.IsHuman)
            {
                SessionFor(victim)?.Sampler.BotInteraction(now);
            }
        }
        catch (Exception ex) { Logger.Warn("[StatsBots] Skill hurt sampling failed: " + ex.GetBaseException().Message); }
    }

    private void OnShotWeapon(PlayerShotWeaponEventArgs ev)
    {
        try
        {
            Player shooter = ev.Player;
            if (shooter == null || !shooter.IsHuman || ev.FirearmItem == null || ev.FirearmItem.Type == ItemType.ParticleDisruptor) return;
            Session? session = SessionFor(shooter);
            if (session == null) return;
            // Native hitscan applies damage (and raises Hurt) before ShotWeapon in the same call.
            bool hit = session.HitFrame == UnityEngine.Time.frameCount;
            session.HitFrame = -1;
            session.Sampler.Shot(NowSeconds, hit);
        }
        catch (Exception ex) { Logger.Warn("[StatsBots] Skill shot sampling failed: " + ex.GetBaseException().Message); }
    }

    private Session? SessionFor(Player player)
    {
        if (!_enabled || !_isAuthenticatedReal(player) || !AuthenticatedIdentity.TryNormalize(player.UserId, out string userId)) return null;
        if (_sessions.TryGetValue(player.ReferenceHub, out Session session) && session.UserId == userId) return session;
        if (session != null) Flush(session);
        session = new Session(userId, new CombatSampler(_config.CombatWindowSeconds, _config.CombatEpisodeSeconds));
        _sessions[player.ReferenceHub] = session;
        return session;
    }

    private void Flush(Session session)
    {
        if (!session.Sampler.HasPending) return;
        ProviderState state = _stats.TryRead(session.UserId, SkillKeys.All, out StatsRecord? record);
        if (state != ProviderState.Ready || record == null) return; // keep the pending sample for the next flush

        long now = NowUnix;
        SkillSample pending = session.Sampler.TakePending();
        SkillSample next = SkillKeys.Current(record.Counter, now, _config.HalfLifeSeconds).Add(pending);
        bool committed = true;
        foreach (KeyValuePair<string, long> entry in SkillKeys.Write(next, now))
            committed &= _stats.Set(session.UserId, entry.Key, entry.Value) == ProviderState.Ready;
        if (!committed)
            Logger.Error("[StatsBots] Skill sample write reported provider failure for " + session.UserId + "; it is not replayed because a partial write may exist.");
    }

    private static bool IsHumanControlled(Player player) => player.IsPlayer && !player.IsDummy && !player.IsHost;

    private static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class Session
    {
        public Session(string userId, CombatSampler sampler)
        {
            UserId = userId;
            Sampler = sampler;
        }

        public string UserId { get; }
        public CombatSampler Sampler { get; }
        public int HitFrame { get; set; } = -1;
    }
}
