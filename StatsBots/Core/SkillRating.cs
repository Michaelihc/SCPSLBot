using System;
using System.Collections.Generic;
using StatsBots.Config;

namespace StatsBots.Core;

/// <summary>Combat totals. Persisted values are exponentially decayed toward zero over time.</summary>
internal readonly record struct SkillSample(double Kills, double Deaths, double Hits, double Shots, double CombatSeconds)
{
    public SkillSample Add(SkillSample other)
        => new(Kills + other.Kills, Deaths + other.Deaths, Hits + other.Hits, Shots + other.Shots, CombatSeconds + other.CombatSeconds);

    public SkillSample Scale(double factor)
        => new(Kills * factor, Deaths * factor, Hits * factor, Shots * factor, CombatSeconds * factor);
}

internal readonly record struct SkillResult(
    bool Ranked,
    int Rating,
    double Accuracy,
    double KillShare,
    double KillsPerMinute,
    double Confidence,
    double PlacementProgress);

internal static class SkillMath
{
    /// <summary>Stored counters are fixed-point: one unit is 1/1000 of a kill, shot or combat second.</summary>
    public const double FixedPointScale = 1000d;

    public static SkillSample Decay(SkillSample sample, double elapsedSeconds, double halfLifeSeconds)
    {
        if (elapsedSeconds <= 0 || halfLifeSeconds <= 0) return sample;
        return sample.Scale(Math.Pow(0.5, elapsedSeconds / halfLifeSeconds));
    }

    public static SkillResult Evaluate(SkillSample s, SkillRatingConfig config)
    {
        double events = s.Kills + s.Deaths;
        double accuracy = s.Shots > 0 ? Math.Min(1d, s.Hits / s.Shots) : 0d;
        double killShare = events > 0 ? s.Kills / events : 0d;
        double minutes = s.CombatSeconds / 60d;
        double kpm = minutes > 0 ? s.Kills / minutes : 0d;
        double rateScore = kpm / (kpm + config.KillRateReference);

        double weights = config.AccuracyWeight + config.KillShareWeight + config.KillRateWeight;
        double skill = weights > 0
            ? (config.AccuracyWeight * accuracy + config.KillShareWeight * killShare + config.KillRateWeight * rateScore) / weights
            : 0d;
        double confidence = 1d - Math.Exp(-events / config.ConfidenceEvents);

        double placement = Math.Min(1d, Math.Min(s.Shots / config.MinimumShots, events / config.MinimumEvents));
        bool ranked = placement >= 1d;
        int rating = ranked ? (int)Math.Round(1000d * skill * confidence) : 0;
        return new SkillResult(ranked, rating, accuracy, killShare, kpm, confidence, placement);
    }

    public static long ToFixed(double value) => (long)Math.Round(Math.Max(0d, value) * FixedPointScale);

    public static double FromFixed(long value) => Math.Max(0L, value) / FixedPointScale;
}

/// <summary>
/// Per-player combat sampler. Only engagements with managed bots are sampled: a missed shot counts only
/// when a confirmed bot interaction (hit, hurt by a bot, kill or death) happens within the combat window
/// around it, so idling, safe-zone time and stray shots never lower the rating. Any player-versus-player
/// damage suspends sampling for one window.
/// </summary>
internal sealed class CombatSampler
{
    private readonly double _window;
    private readonly double _episodeSeconds;
    private double _lastConfirmed = double.NegativeInfinity;
    private double _lastActivity = double.NegativeInfinity;
    private double _pvpUntil = double.NegativeInfinity;
    private int _unconfirmedShots;
    private double _unconfirmedFirst;
    private double _unconfirmedLast;
    private double _kills, _deaths, _hits, _shots, _combat;

    public CombatSampler(double windowSeconds, double episodeSeconds)
    {
        _window = windowSeconds;
        _episodeSeconds = episodeSeconds;
    }

    public bool HasPending => _kills != 0 || _deaths != 0 || _hits != 0 || _shots != 0 || _combat != 0;

    public SkillSample Pending => new(_kills, _deaths, _hits, _shots, _combat);

    public SkillSample TakePending()
    {
        SkillSample pending = Pending;
        _kills = _deaths = _hits = _shots = _combat = 0;
        return pending;
    }

    public void Shot(double now, bool hit)
    {
        if (InPvp(now)) return;
        if (hit)
        {
            Confirm(now);
            _shots++;
            _hits++;
            return;
        }
        if (now - _lastConfirmed <= _window)
        {
            _shots++;
            Accrue(now);
            return;
        }
        if (_unconfirmedShots > 0 && now - _unconfirmedLast > _window) _unconfirmedShots = 0;
        if (_unconfirmedShots == 0) _unconfirmedFirst = now;
        _unconfirmedShots++;
        _unconfirmedLast = now;
    }

    public void BotInteraction(double now) => Confirm(now);

    public void Kill(double now)
    {
        if (InPvp(now)) return;
        Confirm(now);
        _kills++;
    }

    public void Death(double now)
    {
        if (InPvp(now)) return;
        Confirm(now);
        _deaths++;
    }

    public void PlayerVersusPlayer(double now)
    {
        _pvpUntil = now + _window;
        _unconfirmedShots = 0;
    }

    private bool InPvp(double now) => now <= _pvpUntil;

    private void Confirm(double now)
    {
        if (InPvp(now)) return;
        if (_unconfirmedShots > 0)
        {
            if (now - _unconfirmedLast <= _window)
            {
                _shots += _unconfirmedShots;
                Accrue(_unconfirmedFirst);
            }
            _unconfirmedShots = 0;
        }
        Accrue(now);
        _lastConfirmed = now;
    }

    private void Accrue(double now)
    {
        double gap = now - _lastActivity;
        _combat += gap >= 0 && gap <= _window ? gap : _episodeSeconds;
        _lastActivity = Math.Max(_lastActivity, now);
    }
}

internal static class SkillKeys
{
    public const string Kills = "Warmup.Skill.Kills";
    public const string Deaths = "Warmup.Skill.Deaths";
    public const string Hits = "Warmup.Skill.Hits";
    public const string Shots = "Warmup.Skill.Shots";
    public const string CombatSeconds = "Warmup.Skill.CombatSeconds";
    public const string UpdatedAtUnix = "Warmup.Skill.UpdatedAt";

    public static readonly string[] All = { Kills, Deaths, Hits, Shots, CombatSeconds, UpdatedAtUnix };

    public static SkillSample Read(Func<string, long> counter)
        => new(SkillMath.FromFixed(counter(Kills)), SkillMath.FromFixed(counter(Deaths)), SkillMath.FromFixed(counter(Hits)),
            SkillMath.FromFixed(counter(Shots)), SkillMath.FromFixed(counter(CombatSeconds)));

    public static IEnumerable<KeyValuePair<string, long>> Write(SkillSample sample, long updatedAtUnix)
    {
        yield return new(Kills, SkillMath.ToFixed(sample.Kills));
        yield return new(Deaths, SkillMath.ToFixed(sample.Deaths));
        yield return new(Hits, SkillMath.ToFixed(sample.Hits));
        yield return new(Shots, SkillMath.ToFixed(sample.Shots));
        yield return new(CombatSeconds, SkillMath.ToFixed(sample.CombatSeconds));
        yield return new(UpdatedAtUnix, updatedAtUnix);
    }

    /// <summary>Stored totals decayed to <paramref name="nowUnix"/>.</summary>
    public static SkillSample Current(Func<string, long> counter, long nowUnix, double halfLifeSeconds)
    {
        long updated = counter(UpdatedAtUnix);
        SkillSample stored = Read(counter);
        return updated <= 0 ? stored : SkillMath.Decay(stored, nowUnix - updated, halfLifeSeconds);
    }
}
