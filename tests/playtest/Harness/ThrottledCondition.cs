using System;
using UnityEngine;

namespace SCPSLBot.PlaytestScenarios.Harness;

/// <summary>Prevents WaitUntil's per-frame pump from spamming externally dispatched status commands.</summary>
internal sealed class ThrottledCondition
{
    private readonly Func<bool> evaluate;
    private readonly float intervalSeconds;
    private float nextEvaluationAt;
    private bool lastResult;

    public ThrottledCondition(Func<bool> evaluate, float intervalSeconds = 0.25f)
    {
        this.evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        this.intervalSeconds = Mathf.Max(0.05f, intervalSeconds);
    }

    public bool Check()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextEvaluationAt)
        {
            return lastResult;
        }

        nextEvaluationAt = now + intervalSeconds;
        lastResult = evaluate();
        return lastResult;
    }
}
