#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MEC;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.Presentation;
using UnityEngine;

namespace DuelArena;

internal sealed partial class DuelRuntime
{
    private readonly DuelConfig config;
    private readonly IHintDisplayProvider? display;
    private readonly ArenaModel arena = new();
    private readonly Dictionary<ReferenceHub, Participant> participants = new(HubComparer.Instance);
    private readonly List<InvitationOffer> invitations = new();
    private readonly List<ChallengeRequest> requests = new();
    private readonly List<QueuedMatch> queue = new();
    private readonly List<Participant> challengeQueue = new();
    private readonly HashSet<Participant> pendingDeaths = new();
    private Participant? challenger;
    private Participant? opponent;
    private Participant? residentWinner;
    private float expires;
    private float matchStart;
    private float nextRefresh;
    private CoroutineHandle pump;
    private bool enabled;
    private bool transitioning;
    public Func<Player, bool>? ReturnToSurface { get; set; }
    public Func<bool>? ModeActive { get; set; }

    public DuelRuntime(DuelConfig config, IHintDisplayProvider? display = null)
    { this.config = config; this.display = display; }
    private static float Now => Time.realtimeSinceStartup;
    private float InvitationLifetime => Mathf.Clamp(config.InvitationSeconds, 5f, 120f);
    private Vector3 ViewingPosition => arena.Origin + new Vector3(0, 3.5f, 18);
    private static readonly Vector3[] ViewingOffsets =
    {
        Vector3.zero, new(2, 0, 0), new(-2, 0, 0), new(4, 0, 0), new(-4, 0, 0),
        new(0, 0, 1.5f), new(2, 0, 1.5f), new(-2, 0, 1.5f), new(4, 0, 1.5f), new(-4, 0, 1.5f),
        new(0, 0, -1.5f), new(2, 0, -1.5f), new(-2, 0, -1.5f), new(4, 0, -1.5f), new(-4, 0, -1.5f),
    };
    private Vector3 ViewerPosition(Participant participant) => ViewingPosition + ViewingOffsets[participant.ViewingSlot % ViewingOffsets.Length];
    internal bool Fighting => opponent != null;
    internal Vector3 Origin => arena.Origin;
    internal string Status => $"ready={arena.Ready}; state={(Fighting ? "fighting" : "idle")}; "
        + $"challenger={challenger?.PlayerId ?? 0}; opponent={opponent?.PlayerId ?? 0}; "
        + $"invited={invitations.Sum(offer => offer.Targets.Count)}; viewers={participants.Values.Count(p => p.Viewer)}; "
        + $"requests={requests.Count}; queued={queue.Count}; resident={residentWinner?.PlayerId ?? 0}; "
        + $"queue={string.Join(",", queue.Select(pair => pair.Left.PlayerId + "+" + pair.Right.PlayerId))}; "
        + $"king={config.KeepWinnerInArena}; challengers={challengeQueue.Count}; "
        + $"challenge_queue={string.Join(",", challengeQueue.Select(p => p.PlayerId))}; origin={Origin}";

    private static bool Current(Player player, string userId) => player != null && !player.IsDestroyed
        && player.ReferenceHub != null && player.IsReady && player.IsPlayer && !player.IsDummy && !player.IsHost
        && player.Role != RoleTypeId.Tutorial && player.Role != RoleTypeId.Overwatch && player.Role != RoleTypeId.Scp079
        && string.Equals(player.UserId, userId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(userId);
    internal bool Available(Player player, string userId) => enabled && arena.Ready
        && (ModeActive?.Invoke() ?? true) && Current(player, userId);
    internal bool Owns(ReferenceHub hub) => !ReferenceEquals(hub, null) && participants.ContainsKey(hub);
    internal bool IsFighting(Player player) => Fighting
        && (ReferenceEquals(player, challenger?.Player) || ReferenceEquals(player, opponent?.Player));
    internal bool CanFight(ReferenceHub left, ReferenceHub right) => Fighting && Now >= matchStart
        && ((ReferenceEquals(left, challenger!.Hub) && ReferenceEquals(right, opponent!.Hub))
            || (ReferenceEquals(right, challenger!.Hub) && ReferenceEquals(left, opponent!.Hub)));
    private bool IsQueued(Participant participant) => queue.Any(pair => pair.Contains(participant)) || challengeQueue.Contains(participant);
    private bool Free(Participant participant) => Valid(participant) && participant.Player.IsAlive
        && !IsFighting(participant.Player) && !IsQueued(participant);
    private bool IsCandidate(Player player) => !participants.TryGetValue(player.ReferenceHub, out Participant participant) || Free(participant);
    private bool Valid(Participant participant) => Current(participant.Player, participant.UserId)
        && ReferenceEquals(participant.Player.ReferenceHub, participant.Hub)
        && participants.TryGetValue(participant.Hub, out Participant current) && ReferenceEquals(current, participant);

    internal void Enable()
    {
        enabled = true;
        display?.Enable();
        ServerEvents.MapGenerated += OnMapGenerated;
        ServerEvents.RoundRestarted += OnRestart;
        PlayerEvents.InteractedToy += OnInteracted;
        PlayerEvents.ShootingWeapon += OnShooting;
        PlayerEvents.Hurting += OnHurting;
        PlayerEvents.Death += OnDeath;
        PlayerEvents.ChangedRole += OnChangedRole;
        PlayerEvents.Left += OnLeft;
        OnMapGenerated();
        pump = Timing.RunCoroutine(Pump());
    }
    internal void Disable()
    {
        enabled = false;
        Timing.KillCoroutines(pump);
        ServerEvents.MapGenerated -= OnMapGenerated;
        ServerEvents.RoundRestarted -= OnRestart;
        PlayerEvents.InteractedToy -= OnInteracted;
        PlayerEvents.ShootingWeapon -= OnShooting;
        PlayerEvents.Hurting -= OnHurting;
        PlayerEvents.Death -= OnDeath;
        PlayerEvents.ChangedRole -= OnChangedRole;
        PlayerEvents.Left -= OnLeft;
        Reset(true);
        DestroyExtras();
        arena.Destroy();
        display?.Disable();
    }
    private void OnMapGenerated()
    {
        Reset(true);
        DestroyExtras();
        Room? surface = Room.List.FirstOrDefault(r => r.Zone == MapGeneration.FacilityZone.Surface);
        if (surface == null) return;
        arena.Spawn(surface.Position + Vector3.up * Mathf.Clamp(config.SurfaceHeightOffset, 100f, 400f));
        AddPrimitive(arena.Origin + new Vector3(0, 2.5f, 18f), new Vector3(12, 0.2f, 5), Color.gray);
        RefreshBoard();
        Logger.Info($"[DuelArena] Model spawned: {arena.PrimitiveCount} primitives, {arena.LightCount} lights; {Status}");
    }
    private void OnRestart() { Reset(false); DestroyExtras(); arena.Destroy(); }
    private void OnMapGenerated(MapGeneratedEventArgs ev) => OnMapGenerated();

    internal bool Enter(Player player, string userId)
    {
        if (!Available(player, userId)) return false;
        if (participants.ContainsKey(player.ReferenceHub)) return true;
        return JoinViewing(player, out _);
    }
    internal bool Watch(Player player, out string response)
    {
        response = "决斗观众席暂不可用";
        if (!Available(player, player.UserId)) return false;
        if (participants.TryGetValue(player.ReferenceHub, out Participant existing))
        {
            if (!existing.Viewer) { response = "请先输入 .leave 退出自己的决斗"; return false; }
            Release(existing, true, "玩家退出观众席");
            response = "已退出观战，返回地表";
            return true;
        }
        return JoinViewing(player, out response);
    }
    private bool JoinViewing(Player player, out string response)
    {
        response = "决斗观众席暂不可用";
        Participant viewer = Capture(player);
        try
        {
            PrepareViewer(viewer);
            Tell(player, "已进入观众席；按 E 选择对手并发送邀请，或输入 .duel 玩家ID\n配对后按顺序进场；.challenge 擂台玩家ID 申请挑战，.leave 返回地表");
            response = "已进入决斗观众席";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[DuelArena] Viewing entry failed: {ex}");
            Release(viewer, true, "进入观众席失败");
            return false;
        }
    }
    internal void Leave(Player player, string reason)
    {
        if (!participants.TryGetValue(player.ReferenceHub, out Participant participant)) return;
        bool fighting = IsFighting(player);
        Participant? winner = ReferenceEquals(participant, challenger) ? opponent : challenger;
        Release(participant, true, reason);
        if (fighting) Finish(reason, winner);
        else StartNextPair();
    }
    private Participant Capture(Player player)
    {
        // Authored poses on the 12 x 5 m deck; keep capsules away from its edges.
        // Reserve a pose through combat so returning fighters do not stack on viewers.
        int slot = Enumerable.Range(0, participants.Count + 1).First(index => participants.Values.All(p => p.ViewingSlot != index));
        var participant = new Participant(player, slot);
        participants.Add(participant.Hub, participant);
        return participant;
    }
    private void PrepareViewer(Participant participant)
    {
        participant.Viewer = true;
        Prepare(participant, RoleTypeId.NtfPrivate, ViewerPosition(participant));
        participant.Player.Rotation = Quaternion.Euler(0, 180, 0);
    }
    private void Prepare(Participant participant, RoleTypeId role, Vector3 position)
    {
        if (!Valid(participant)) throw new InvalidOperationException("Participant identity changed.");
        var encoded = new RelativePositioning.RelativePosition(position);
        if (encoded.OutOfRange || Vector3.Distance(encoded.Position, position) > 0.02f)
            throw new InvalidOperationException("Arena position cannot be synchronized by the native waypoint.");
        transitioning = true;
        try
        {
            Player player = participant.Player;
            player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            if (player.Role != role || !Valid(participant)) throw new InvalidOperationException("Role assignment rejected.");
            player.ClearInventory();
            if (!participant.Viewer)
            {
                if (player.AddItem(ItemType.GunE11SR) == null) throw new InvalidOperationException("Duel firearm grant failed.");
                player.SetAmmo(ItemType.Ammo556x45, 180);
            }
            player.Health = 100f;
            player.IsGodModeEnabled = false;
            player.ReferenceHub.playerEffectsController.DisableAllEffects();
            if (!player.ReferenceHub.TryOverridePosition(position)) throw new InvalidOperationException("Arena placement failed.");
        }
        finally { transitioning = false; }
    }
    private void Release(Participant participant, bool returnToSurface, string reason)
    {
        CancelCommitments(participant, reason);
        if (ReferenceEquals(residentWinner, participant)) residentWinner = null;
        bool current = Valid(participant);
        participants.Remove(participant.Hub);
        pendingDeaths.Remove(participant);
        if (!participant.Player.IsDestroyed && participant.Hub != null) display?.Remove(participant.Player, "duel.status");
        if (!current || !returnToSurface) return;
        transitioning = true;
        try
        {
            participant.Player.SetRole(RoleTypeId.Spectator);
            if (ReturnToSurface?.Invoke(participant.Player) != true)
            {
                participant.Player.SetRole(RoleTypeId.NtfPrivate);
                participant.Hub.TryOverridePosition(participant.ReturnPosition);
            }
        }
        finally { transitioning = false; }
    }
    private void Finish(string reason, Participant? winner)
    {
        Participant?[] fighters = { challenger, opponent };
        Logger.Info($"[DuelArena] Slot released: challenger={challenger?.PlayerId}; opponent={opponent?.PlayerId}; winner={winner?.PlayerId}; reason={reason}");
        challenger = null;
        opponent = null;
        expires = 0;
        string message = winner != null && Valid(winner) && winner.Player.IsAlive ? $"{Safe(winner.Player.Nickname)} 获胜！{reason}" : reason;
        foreach (Participant? participant in fighters)
        {
            if (participant == null || !Valid(participant)) continue;
            pendingDeaths.Remove(participant);
            try
            {
                if (config.KeepWinnerInArena && ReferenceEquals(participant, winner) && participant.Player.IsAlive)
                {
                    residentWinner = participant;
                    participant.Viewer = false;
                    Prepare(participant, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -8));
                }
                else PrepareViewer(participant);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DuelArena] Post-match transition failed: {ex}");
                Release(participant, true, "赛后返回失败");
            }
        }
        foreach (Participant participant in participants.Values.ToArray())
            if (Valid(participant)) Tell(participant.Player, message + (config.KeepWinnerInArena
                ? "\n胜者守擂；挑战者按顺序进场，.challenge 加入挑战队列"
                : "\n已配对的玩家按顺序进场；未配对的玩家可继续邀请"));
        PruneMatchmaking();
        foreach (ChallengeRequest request in requests)
        {
            request.Expires = Now + InvitationLifetime;
            Tell(request.Target.Player, $"观众 {Safe(request.Viewer.Player.Nickname)} 的挑战申请等待接受\n输入 .accept {request.Viewer.PlayerId} 配对排队（{Mathf.CeilToInt(InvitationLifetime)} 秒内）");
        }
        StartNextPair();
    }
    private void StartNextPair()
    {
        if (Fighting || transitioning || pendingDeaths.Count != 0 || !enabled || !arena.Ready || !(ModeActive?.Invoke() ?? true)) return;
        if (config.KeepWinnerInArena) { StartNextKingMatch(); return; }
        while (queue.Count != 0)
        {
            QueuedMatch pair = queue[0];
            queue.RemoveAt(0);
            if (!Free(pair.Left) || !Free(pair.Right)) { NotifyCancelledPair(pair, "配对玩家已离开或死亡"); continue; }
            try
            {
                if (residentWinner != null && Valid(residentWinner) && !pair.Contains(residentWinner)) PrepareViewer(residentWinner);
                residentWinner = null;
                BeginMatch(pair.Left, pair.Right);
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DuelArena] Match setup failed: {ex}");
                challenger = null;
                opponent = null;
                Release(pair.Left, true, "决斗准备失败");
                Release(pair.Right, true, "决斗准备失败");
            }
        }
    }
    private void BeginMatch(Participant left, Participant right)
    {
        // Claim before native role callbacks and before another acceptance can enter.
        challenger = left;
        opponent = right;
        challenger.Viewer = false;
        opponent.Viewer = false;
        matchStart = Now + 3f;
        expires = matchStart + Mathf.Clamp(config.MatchSeconds, 15f, 600f);
        Prepare(challenger, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -8));
        Prepare(opponent, RoleTypeId.ChaosConscript, arena.Origin + new Vector3(0, 1, 8));
        Tell(challenger.Player, $"已轮到你们！对手：{Safe(opponent.Player.Nickname)}；3 秒后开始决斗");
        Tell(opponent.Player, $"已轮到你们！对手：{Safe(challenger.Player.Nickname)}；3 秒后开始；.leave 认输");
        Logger.Info($"[DuelArena] Match started: {challenger.PlayerId} vs {opponent.PlayerId}; queued={queue.Count}; challengers={challengeQueue.Count}");
    }
    private void Reset(bool returnToSurface)
    {
        challenger = null;
        opponent = null;
        residentWinner = null;
        queue.Clear();
        challengeQueue.Clear();
        invitations.Clear();
        requests.Clear();
        pendingDeaths.Clear();
        foreach (Participant participant in participants.Values.ToArray())
        {
            try { Release(participant, returnToSurface, "决斗服务重置"); }
            catch (Exception ex) { Logger.Warn($"[DuelArena] Participant release failed: {ex}"); }
        }
        participants.Clear();
        DestroyBoard();
        page = 0;
        expires = 0;
    }
    private IEnumerator<float> Pump()
    {
        while (enabled)
        {
            try
            {
                if (ModeActive?.Invoke() == false && participants.Count != 0) Reset(true);
                // The native Death stack must unwind before either fighter changes role.
                Participant? deceasedFighter = pendingDeaths.FirstOrDefault(p => IsFighting(p.Player));
                if (deceasedFighter != null) Finish("决斗结束", ReferenceEquals(deceasedFighter, challenger) ? opponent : challenger);
                foreach (Participant deceased in pendingDeaths.ToArray())
                {
                    pendingDeaths.Remove(deceased);
                    if (ReferenceEquals(residentWinner, deceased)) residentWinner = null;
                    if (Valid(deceased) && !deceased.Player.IsAlive) PrepareViewer(deceased);
                }
                PruneMatchmaking();
                if (Fighting)
                {
                    if (!Valid(challenger!) || !Valid(opponent!)) Finish("玩家离开决斗", null);
                    else if (Now >= expires) Finish("决斗超时", null);
                    else if (!arena.Contains(challenger!.Player.Position)) Finish("离开决斗范围", opponent);
                    else if (!arena.Contains(opponent!.Player.Position)) Finish("离开决斗范围", challenger);
                }
                foreach (Participant viewer in participants.Values.ToArray())
                {
                    if (!Valid(viewer)) { Release(viewer, false, "玩家连接已失效"); continue; }
                    if (!viewer.Viewer) continue;
                    Vector3 delta = viewer.Player.Position - ViewingPosition;
                    if (Math.Abs(delta.x) > 5.5f || Math.Abs(delta.z) > 2f || Math.Abs(delta.y) > 2) viewer.Hub.TryOverridePosition(ViewerPosition(viewer));
                }
                if (residentWinner != null && (!Valid(residentWinner) || !arena.Contains(residentWinner.Player.Position)))
                {
                    Participant resident = residentWinner;
                    residentWinner = null;
                    if (Valid(resident)) PrepareViewer(resident);
                }
                StartNextPair();
                if (Now >= nextRefresh) { RefreshBoard(); RefreshHints(); nextRefresh = Now + 1f; }
            }
            catch (Exception ex) { Logger.Warn($"[DuelArena] Runtime recovery: {ex}"); Reset(true); }
            yield return Timing.WaitForSeconds(0.2f);
        }
    }
    private void OnLeft(PlayerLeftEventArgs ev)
    {
        foreach (InvitationOffer offer in invitations) offer.Targets.Remove(ev.Player.ReferenceHub);
        if (!participants.TryGetValue(ev.Player.ReferenceHub, out Participant departing)) return;
        bool fighting = IsFighting(ev.Player);
        Participant? winner = ReferenceEquals(departing, challenger) ? opponent : challenger;
        Release(departing, false, "玩家离开服务器");
        if (fighting) Finish("玩家离开服务器", winner);
    }
    private void OnDeath(PlayerDeathEventArgs ev)
    {
        if (!participants.TryGetValue(ev.Player.ReferenceHub, out Participant deceased)) return;
        CancelCommitments(deceased, "配对玩家死亡");
        pendingDeaths.Add(deceased);
    }
    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        if (ev.ChangeReason == RoleChangeReason.Died || transitioning || !participants.TryGetValue(ev.Player.ReferenceHub, out Participant participant)) return;
        bool fighting = IsFighting(ev.Player);
        Participant? winner = ReferenceEquals(participant, challenger) ? opponent : challenger;
        Release(participant, false, "角色发生变化");
        if (fighting) Finish("角色发生变化，决斗结束", winner);
    }
    private void OnHurting(PlayerHurtingEventArgs ev)
    {
        bool victim = Owns(ev.Player.ReferenceHub);
        bool attacker = ev.Attacker != null && Owns(ev.Attacker.ReferenceHub);
        if ((victim || attacker) && (ev.Attacker == null || !CanFight(ev.Attacker.ReferenceHub, ev.Player.ReferenceHub))) ev.IsAllowed = false;
    }
    private static void Tell(Player player, string text) { if (!player.IsDestroyed) player.SendBroadcast(text, 12); }
    private static string Safe(string name)
    {
        name = name ?? "玩家";
        if (name.Length > 28) name = name.Substring(0, 28) + "…";
        return name.Replace("<", "＜").Replace(">", "＞").Replace("{", "（").Replace("}", "）").Replace("\n", " ").Replace("\r", " ");
    }
    private sealed class Participant
    {
        public Participant(Player player, int viewingSlot)
        { Player = player; Hub = player.ReferenceHub; UserId = player.UserId; PlayerId = player.PlayerId; ReturnPosition = player.Position; ViewingSlot = viewingSlot; }
        public Player Player { get; }
        public ReferenceHub Hub { get; }
        public string UserId { get; }
        public int PlayerId { get; }
        public int ViewingSlot { get; }
        public Vector3 ReturnPosition { get; }
        public bool Viewer { get; set; } = true;
        public float LastClick { get; set; }
        public float SelectionExpires { get; set; }
        public Dictionary<ReferenceHub, SelectedPlayer> Selected { get; } = new(HubComparer.Instance);
    }
    private sealed class SelectedPlayer
    {
        public SelectedPlayer(Player player) { Player = player; UserId = player.UserId; }
        public Player Player { get; }
        public string UserId { get; }
    }
    private sealed class InvitationOffer
    {
        public InvitationOffer(Participant sender, IEnumerable<Player> targets, float expires)
        { Sender = sender; Expires = expires; foreach (Player target in targets) Targets.Add(target.ReferenceHub, new SelectedPlayer(target)); }
        public Participant Sender { get; }
        public float Expires { get; }
        public Dictionary<ReferenceHub, SelectedPlayer> Targets { get; } = new(HubComparer.Instance);
    }
    private sealed class ChallengeRequest
    {
        public ChallengeRequest(Participant viewer, Participant target, float expires) { Viewer = viewer; Target = target; Expires = expires; }
        public Participant Viewer { get; }
        public Participant Target { get; }
        public float Expires { get; set; }
    }
    private sealed class QueuedMatch
    {
        public QueuedMatch(Participant left, Participant right) { Left = left; Right = right; }
        public Participant Left { get; }
        public Participant Right { get; }
        public bool Contains(Participant participant) => ReferenceEquals(Left, participant) || ReferenceEquals(Right, participant);
    }
    private sealed class HubComparer : IEqualityComparer<ReferenceHub>
    {
        public static readonly HubComparer Instance = new();
        public bool Equals(ReferenceHub? left, ReferenceHub? right) => ReferenceEquals(left, right);
        public int GetHashCode(ReferenceHub hub) => RuntimeHelpers.GetHashCode(hub);
    }
}
