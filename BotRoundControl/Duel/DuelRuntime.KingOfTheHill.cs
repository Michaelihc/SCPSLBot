#nullable enable
using System;
using LabApi.Features.Console;

namespace DuelArena;

internal sealed partial class DuelRuntime
{
    private bool JoinKingQueue(Participant participant, out string response)
    {
        response = "你已在比赛或挑战队列中";
        if (!Free(participant)) return false;
        CancelCommitments(participant, "已加入守擂挑战队列");
        AddKingChallenger(participant);
        int ahead = challengeQueue.IndexOf(participant) + (Fighting ? 1 : 0);
        response = $"已加入守擂挑战队列；前面 {ahead} 人，轮到时自动进场";
        Tell(participant.Player, response);
        StartNextPair();
        RefreshHints();
        return true;
    }

    private void AddKingChallenger(Participant participant)
    {
        if (ReferenceEquals(residentWinner, participant)) return;
        challengeQueue.Add(participant);
        Logger.Info($"[DuelArena] King challenger queued: player={participant.PlayerId}; position={challengeQueue.Count}");
    }

    private void StartNextKingMatch()
    {
        if (residentWinner != null && (!Valid(residentWinner) || !residentWinner.Player.IsAlive)) residentWinner = null;
        challengeQueue.RemoveAll(participant => !Valid(participant) || !participant.Player.IsAlive);
        while (challengeQueue.Count >= (residentWinner == null ? 2 : 1))
        {
            Participant left;
            if (residentWinner != null) left = residentWinner;
            else { left = challengeQueue[0]; challengeQueue.RemoveAt(0); }
            Participant right = challengeQueue[0];
            challengeQueue.RemoveAt(0);
            if (!Free(left) || !Free(right)) continue;
            residentWinner = null;
            try { BeginMatch(left, right); return; }
            catch (Exception ex)
            {
                Logger.Warn($"[DuelArena] King match setup failed: {ex}");
                challenger = null;
                opponent = null;
                Release(left, true, "守擂准备失败");
                Release(right, true, "守擂准备失败");
            }
        }
    }
}
