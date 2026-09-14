using PlaytestHarness.Core;
using SCPSLBot.PlaytestScenarios.Harness;
using System;
using System.Collections.Generic;

namespace SCPSLBot.PlaytestScenarios.Scenarios;

/// <summary>
/// Intentionally by-name-only: bot_add has no public decrement companion, so proving the cap leaves
/// the isolated playtest port configured at ten bots. Reset warmup_bot_count after this acceptance.
/// </summary>
public sealed class BotAddMaintainedCapScenario : Scenario
{
    public override string Name => "scpslbot-bot-add-cap";
    public override string[] Aliases => ["bot-add-cap"];
    public override string[] Suites => ["scpslbot-mutating"];
    public override string Description => "ISOLATED PORT: proves bot_add raises maintained desired count, survives off/on, and rejects above cap 10.";
    public override FidelityRange Supported => FidelityRange.Only(Fidelity.Standard);
    public override bool IncludeInRunAll => false;
    public override float TimeoutSeconds => 180f;

    public override IEnumerator<float> Run(ScenarioContext ctx)
    {
        BotStatusSnapshot initial = BotStatusSnapshot.Read();
        string originalMode = initial.Mode;

        try
        {
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "enable Standard warmup");
            ThrottledCondition initialPopulation = new(IsExact);
            yield return ctx.WaitUntil(initialPopulation.Check, 12f, "initial maintained population ready");

            int previousDesired = BotStatusSnapshot.Read().Desired;
            while (previousDesired < 10)
            {
                NativeCommandResult add = NativeCommandAdapter.RemoteAdmin("bot_add");
                WarmupPopulationRecoveryScenario.RequireCommand(ctx, add,
                    $"raise maintained population from {previousDesired} to {previousDesired + 1}");
                int expected = previousDesired + 1;
                ThrottledCondition raisedPopulation = new(() => IsExactAt(expected));
                yield return ctx.WaitUntil(raisedPopulation.Check, 12f,
                    $"bot_add maintained exact desired/live/world count {expected}");
                previousDesired = expected;
            }

            NativeCommandResult capped = NativeCommandAdapter.RemoteAdmin("bot_add");
            ctx.Require(!capped.Success, "bot_add rejects an eleventh maintained bot");
            ctx.Require(capped.CombinedText.IndexOf("cap", StringComparison.OrdinalIgnoreCase) >= 0
                        && capped.CombinedText.Contains("10"),
                $"cap rejection is explicit and bounded at 10: {capped.CombinedText}");
            ctx.Require(IsExactAt(10), "cap rejection leaves exact maintained population at 10");

            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup none"), "disable cap population");
            ThrottledCondition capOff = new(() => WarmupBotWorld.Snapshot().Length == 0
                                                    && BotStatusSnapshot.Read().Owned == 0);
            yield return ctx.WaitUntil(capOff.Check, 5f,
                "cap population fully despawned in None mode");
            WarmupPopulationRecoveryScenario.RequireCommand(ctx,
                NativeCommandAdapter.RemoteAdmin("bot_warmup standard"), "re-enable cap population");
            ThrottledCondition recoveredCap = new(() => IsExactAt(10));
            yield return ctx.WaitUntil(recoveredCap.Check, 12f,
                "bot_add changed maintained count rather than spawning temporary bots");
            WarmupBotWorld.AssertExactInitializedPopulation(ctx, BotStatusSnapshot.Read());
            ctx.Info("ISOLATED PORT MUTATION: warmup_bot_count is now persisted at 10; reset it after this acceptance run.");
        }
        finally
        {
            NativeCommandAdapter.RemoteAdmin($"bot_warmup {originalMode.ToLowerInvariant()}");
        }
    }

    private static bool IsExact()
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return IsExactAt(status.Desired);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactAt(int expected)
    {
        try
        {
            BotStatusSnapshot status = BotStatusSnapshot.Read();
            return status.Desired == expected
                   && status.Owned == expected
                   && status.Live == expected
                   && WarmupBotWorld.Snapshot().Length == expected;
        }
        catch
        {
            return false;
        }
    }
}
