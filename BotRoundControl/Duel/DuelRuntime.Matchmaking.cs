#nullable enable
using System;
using System.Linq;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using UnityEngine;

namespace DuelArena;

internal sealed partial class DuelRuntime
{
    internal bool Invite(Player player, int[] targetIds, out string response)
    {
        response = "决斗台暂不可用";
        if (!Enter(player, player.UserId) || !participants.TryGetValue(player.ReferenceHub, out Participant sender)) return false;
        if (!Free(sender)) { response = "你已配对；请等待比赛，或输入 .leave 取消"; return false; }
        Player[] targets = Player.ReadyList.Where(target => targetIds.Contains(target.PlayerId) && CanInvite(sender, target)).ToArray();
        if (targets.Select(target => target.PlayerId).Distinct().Count() != targetIds.Distinct().Count())
        { response = "对手不存在、不可邀请或已经配对"; return false; }
        return SendInvitations(sender, targets, out response);
    }

    private bool CanInvite(Participant sender, Player target) => !ReferenceEquals(sender.Player, target)
        && Available(target, target.UserId) && IsCandidate(target);

    private bool SendInvitations(Participant sender, Player[] targets, out string response)
    {
        response = "你已配对，不能继续邀请";
        if (!Free(sender)) return false;
        targets = targets.Where(target => CanInvite(sender, target)).Distinct().ToArray();
        if (targets.Length == 0) { response = "请先选择至少一名可邀请的玩家"; return false; }
        invitations.RemoveAll(offer => ReferenceEquals(offer.Sender, sender));
        var offer = new InvitationOffer(sender, targets, Now + InvitationLifetime);
        invitations.Add(offer);
        sender.Selected.Clear();
        foreach (Player target in targets)
            Tell(target, $"{Safe(sender.Player.Nickname)}（ID {sender.PlayerId}）邀请你" + (config.KeepWinnerInArena ? "参加守擂挑战" : "进行 1v1 决斗")
                + $"\n输入 .accept、.yes 或 .accept {sender.PlayerId}；"
                + (config.KeepWinnerInArena ? "首位接受者与邀请者依次加入个人挑战队列" : "首位接受者配对排队")
                + $"（{Mathf.CeilToInt(InvitationLifetime)} 秒）");
        response = $"已邀请 {targets.Length} 名玩家；等待首位接受者" + (config.KeepWinnerInArena ? "加入守擂挑战" : "配对排队");
        Tell(sender.Player, response);
        Logger.Info($"[DuelArena] Invitations sent: challenger={sender.PlayerId}; targets={string.Join(",", targets.Select(target => target.PlayerId))}; seconds={Mathf.CeilToInt(InvitationLifetime)}");
        RefreshHints();
        return true;
    }

    internal bool RequestChallenge(Player player, int? targetId, out string response)
    {
        response = "请先进入观众席，再向擂台上的玩家申请挑战";
        if (!Available(player, player.UserId) || !participants.TryGetValue(player.ReferenceHub, out Participant viewer) || !viewer.Viewer) return false;
        if (!Free(viewer)) { response = "你已在比赛或队列中；输入 .leave 退出"; return false; }
        Participant[] fighters = new[] { challenger, opponent, residentWinner }.Where(p => p != null && Valid(p!) && p!.Player.IsAlive)
            .Cast<Participant>().Distinct().ToArray();
        if (config.KeepWinnerInArena)
        {
            if (targetId.HasValue && !fighters.Any(p => p.PlayerId == targetId.Value))
            { response = "目标不在擂台上存活；输入 .challenge 加入守擂挑战队列"; return false; }
            return JoinKingQueue(viewer, out response);
        }
        Participant? target = targetId.HasValue ? fighters.FirstOrDefault(p => p.PlayerId == targetId.Value)
            : fighters.Length == 1 ? fighters[0] : null;
        if (target == null) { response = "用法：.challenge <擂台玩家 ID>；目标必须在擂台上存活"; return false; }
        requests.RemoveAll(request => ReferenceEquals(request.Viewer, viewer));
        requests.Add(new ChallengeRequest(viewer, target, Now + InvitationLifetime + (Fighting ? Mathf.Clamp(config.MatchSeconds, 15f, 600f) + 3f : 0f)));
        Tell(target.Player, $"观众 {Safe(viewer.Player.Nickname)}（ID {viewer.PlayerId}）申请挑战\n本场结束后输入 .accept {viewer.PlayerId} 接受；配对后按顺序排队");
        response = "挑战申请已发送；等待对方本场结束后接受";
        RefreshHints();
        return true;
    }

    internal bool Accept(Player player, out string response, int? senderId = null)
    {
        if (!Available(player, player.UserId)) return RejectAccept(player, senderId, "决斗台暂不可用", out response);
        PruneMatchmaking();
        if (participants.TryGetValue(player.ReferenceHub, out Participant recipient) && !Free(recipient))
            return RejectAccept(player, senderId, "你已配对；本场结束后再接受邀请", out response);
        InvitationOffer? offer = invitations.FirstOrDefault(item => (!senderId.HasValue || item.Sender.PlayerId == senderId.Value)
            && Free(item.Sender) && item.Targets.TryGetValue(player.ReferenceHub, out SelectedPlayer target)
            && ReferenceEquals(target.Player, player) && Current(target.Player, target.UserId));
        if (offer != null) return Pair(offer.Sender, player, out response);
        ChallengeRequest? request = requests.FirstOrDefault(item => ReferenceEquals(item.Target.Player, player)
            && (!senderId.HasValue || item.Viewer.PlayerId == senderId.Value) && Free(item.Viewer) && Valid(item.Target));
        if (request != null) return Pair(request.Viewer, player, out response);
        return RejectAccept(player, senderId, "没有可接受的决斗邀请；邀请可能已过期、取消或被其他人接受", out response);
    }

    private bool Pair(Participant left, Player rightPlayer, out string response)
    {
        response = "对手已配对或暂不可用";
        if (!Free(left) || !Available(rightPlayer, rightPlayer.UserId) || !IsCandidate(rightPlayer)
            || ReferenceEquals(left.Player, rightPlayer)) return false;
        if (!participants.TryGetValue(rightPlayer.ReferenceHub, out Participant right))
        {
            if (!JoinViewing(rightPlayer, out response)) return false;
            right = participants[rightPlayer.ReferenceHub];
        }
        if (!Free(left) || !Free(right)) return false;
        CancelCommitments(left, "已接受其他邀请");
        CancelCommitments(right, "已接受其他邀请");
        if (config.KeepWinnerInArena)
        {
            AddKingChallenger(left);
            AddKingChallenger(right);
            response = "已接受守擂邀请；你们分别按顺序挑战胜者";
            Tell(left.Player, response);
            Tell(right.Player, response);
            StartNextPair();
            RefreshHints();
            return true;
        }
        queue.Add(new QueuedMatch(left, right));
        int ahead = queue.Count - 1 + (Fighting ? 1 : 0);
        response = $"已配对；前面 {ahead} 组，轮到时自动进入擂台";
        Tell(left.Player, response);
        Tell(right.Player, response);
        Logger.Info($"[DuelArena] Pair queued: left={left.PlayerId}; right={right.PlayerId}; ahead={ahead}");
        StartNextPair();
        RefreshHints();
        return true;
    }

    private bool RejectAccept(Player player, int? senderId, string reason, out string response)
    {
        response = reason;
        Logger.Info($"[DuelArena] Acceptance rejected: player={player.PlayerId}; sender={senderId?.ToString() ?? "any"}; reason={reason}; {Status}");
        return false;
    }

    private void CancelCommitments(Participant participant, string reason)
    {
        invitations.RemoveAll(offer => ReferenceEquals(offer.Sender, participant));
        foreach (InvitationOffer offer in invitations) offer.Targets.Remove(participant.Hub);
        requests.RemoveAll(request => ReferenceEquals(request.Viewer, participant) || ReferenceEquals(request.Target, participant));
        participant.Selected.Clear();
        challengeQueue.Remove(participant);
        foreach (QueuedMatch pair in queue.Where(pair => pair.Contains(participant)).ToArray())
        { queue.Remove(pair); NotifyCancelledPair(pair, reason); }
    }

    private void NotifyCancelledPair(QueuedMatch pair, string reason)
    {
        foreach (Participant participant in new[] { pair.Left, pair.Right })
            if (Valid(participant)) Tell(participant.Player, "配对已取消：" + reason + "；可重新邀请对手");
    }

    private void PruneMatchmaking()
    {
        invitations.RemoveAll(offer => Now >= offer.Expires || !Free(offer.Sender));
        foreach (InvitationOffer offer in invitations)
            foreach (var target in offer.Targets.ToArray())
                if (!Current(target.Value.Player, target.Value.UserId) || !IsCandidate(target.Value.Player)) offer.Targets.Remove(target.Key);
        invitations.RemoveAll(offer => offer.Targets.Count == 0);
        challengeQueue.RemoveAll(participant => !Valid(participant) || !participant.Player.IsAlive);
        requests.RemoveAll(request => Now >= request.Expires || !Free(request.Viewer) || !Valid(request.Target)
            || !request.Target.Player.IsAlive || IsQueued(request.Target));
        foreach (QueuedMatch pair in queue.Where(pair => !Valid(pair.Left) || !Valid(pair.Right)
                     || !pair.Left.Player.IsAlive || !pair.Right.Player.IsAlive).ToArray())
        { queue.Remove(pair); NotifyCancelledPair(pair, "对方离开或角色发生变化"); }
        foreach (Participant participant in participants.Values)
        {
            if (Now >= participant.SelectionExpires) participant.Selected.Clear();
            foreach (var selected in participant.Selected.ToArray())
                if (!Current(selected.Value.Player, selected.Value.UserId) || !CanInvite(participant, selected.Value.Player))
                    participant.Selected.Remove(selected.Key);
        }
    }
}
