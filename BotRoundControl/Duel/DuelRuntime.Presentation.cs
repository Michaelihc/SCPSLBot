#nullable enable
using System;
using System.Linq;
using System.Text;
using SCPSLBot.Presentation;
using UnityEngine;

namespace DuelArena;

internal sealed partial class DuelRuntime
{
    private static string ShortName(Participant participant)
    {
        string name = Safe(participant.Player.Nickname);
        return name.Length > 12 ? name.Substring(0, 12) + "…" : name;
    }
    private void RefreshHints()
    {
        if (display == null) return;
        foreach (Participant participant in participants.Values.Where(Valid))
        {
            var text = new StringBuilder(config.KeepWinnerInArena ? "<b>守擂 / 1v1</b>\n" : "<b>决斗 / 1v1</b>\n");
            if (IsFighting(participant.Player)) text.AppendLine("正在比赛 · .leave 认输");
            else
            {
                int rank = queue.FindIndex(pair => pair.Contains(participant));
                int kingRank = challengeQueue.IndexOf(participant);
                if (kingRank >= 0)
                {
                    text.AppendLine($"已加入守擂挑战 · 前面 {kingRank + (Fighting ? 1 : 0)} 人");
                    text.AppendLine(!Fighting && residentWinner == null ? "等待另一名挑战者开场 · .leave 退出" : "轮到时自动挑战胜者 · .leave 退出");
                }
                else if (rank >= 0)
                {
                    QueuedMatch pair = queue[rank];
                    Participant other = ReferenceEquals(pair.Left, participant) ? pair.Right : pair.Left;
                    text.AppendLine($"已配对：{ShortName(other)} · 前面 {rank + (Fighting ? 1 : 0)} 组");
                    text.AppendLine("轮到时自动进入 · .leave 取消并返回地表");
                }
                else
                {
                    InvitationOffer? outgoing = invitations.FirstOrDefault(offer => ReferenceEquals(offer.Sender, participant));
                    text.AppendLine(outgoing != null ? $"等待 {outgoing.Targets.Count} 人接受 · 剩余 {Mathf.CeilToInt(outgoing.Expires - Now)} 秒"
                        : ReferenceEquals(residentWinner, participant) ? "守擂者 · 等待挑战，.duel 玩家ID 邀请"
                        : config.KeepWinnerInArena ? "未排队 · .challenge 加入挑战，或邀请玩家"
                        : "未配对 · 按 E 选对手，或 .duel 玩家ID 邀请");
                    if (participant.Selected.Count > 0)
                        text.AppendLine("已选 ID：" + string.Join(",", participant.Selected.Values.Take(4).Select(selected => selected.Player.PlayerId))
                            + (participant.Selected.Count > 4 ? "…" : ""));
                    int[] incoming = invitations.Where(offer => offer.Targets.ContainsKey(participant.Hub)).Select(offer => offer.Sender.PlayerId)
                        .Concat(requests.Where(request => ReferenceEquals(request.Target, participant)).Select(request => request.Viewer.PlayerId)).Distinct().Take(3).ToArray();
                    if (incoming.Length > 0) text.AppendLine("邀请 ID：" + string.Join(",", incoming) + " · .accept ID 接受");
                }
            }
            text.AppendLine(Fighting ? $"擂台：{ShortName(challenger!)} vs {ShortName(opponent!)}"
                : residentWinner != null ? $"守擂者：{ShortName(residentWinner)}" : "擂台空闲");
            if (challengeQueue.Count > 0)
                text.AppendLine("挑战队列：" + string.Join(" → ", challengeQueue.Take(3).Select(ShortName))
                    + (challengeQueue.Count > 3 ? $" … 共 {challengeQueue.Count} 人" : ""));
            foreach (QueuedMatch pair in queue.Take(2)) text.AppendLine($"排队：{ShortName(pair.Left)} vs {ShortName(pair.Right)}");
            if (queue.Count > 2) text.AppendLine($"另有 {queue.Count - 2} 组等待");
            display.Show(participant.Player, new HintRequest("duel.status", text.ToString().TrimEnd(), 2.5f, 0f, 780f, 20));
        }
    }
}
