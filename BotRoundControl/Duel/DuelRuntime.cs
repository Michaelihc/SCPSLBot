#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CustomPlayerEffects;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using Logger = LabApi.Features.Console.Logger;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace DuelArena;

internal sealed class DuelRuntime
{
    private readonly DuelConfig config;
    private readonly ArenaModel arena = new();
    private readonly Dictionary<ReferenceHub, Participant> participants = new(HubComparer.Instance);
    private readonly HashSet<ReferenceHub> selected = new(HubComparer.Instance);
    private readonly Dictionary<ReferenceHub, string> invited = new(HubComparer.Instance);
    private readonly List<ChallengeRequest> requests = new();
    private readonly List<Row> rows = new();
    private Participant? challenger;
    private Participant? opponent;
    private Participant? pendingDeath;
    private float expires;
    private float matchStart;
    private float lastClick;
    private float nextRefresh;
    private string rosterKey = "";
    private CoroutineHandle pump;
    private bool enabled;
    private bool transitioning;
    public Func<Player, bool>? ReturnToSurface { get; set; }
    public Func<bool>? ModeActive { get; set; }

    public DuelRuntime(DuelConfig config) => this.config = config;
    private static float Now => Time.realtimeSinceStartup;
    private Vector3 ViewingPosition => arena.Origin + new Vector3(0, 3.5f, 18);
    internal bool Fighting => opponent != null;
    internal Vector3 Origin => arena.Origin;
    internal string Status => $"ready={arena.Ready}; state={(Fighting ? "fighting" : challenger != null ? "lobby" : "idle")}; "
        + $"challenger={challenger?.PlayerId ?? 0}; opponent={opponent?.PlayerId ?? 0}; invited={invited.Count}; "
        + $"viewers={participants.Values.Count(p => p.Viewer)}; requests={requests.Count}; origin={Origin}";

    private static bool Current(Player player, string userId) => player != null && !player.IsDestroyed
        && player.ReferenceHub != null && player.IsReady && player.IsPlayer && !player.IsDummy && !player.IsHost
        && player.Role != RoleTypeId.Tutorial && player.Role != RoleTypeId.Overwatch && player.Role != RoleTypeId.Scp079
        && string.Equals(player.UserId, userId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(userId);
    private static bool Eligible(Player player) => Current(player, player.UserId);
    internal bool Available(Player player, string userId) => enabled && arena.Ready
        && (ModeActive?.Invoke() ?? true) && Current(player, userId);
    internal bool Owns(ReferenceHub hub) => !ReferenceEquals(hub, null) && participants.ContainsKey(hub);
    internal bool IsFighting(Player player) => Fighting && (ReferenceEquals(player, challenger?.Player) || ReferenceEquals(player, opponent?.Player));
    internal bool CanFight(ReferenceHub left, ReferenceHub right) => Fighting && Now >= matchStart
        && ((ReferenceEquals(left, challenger!.Hub) && ReferenceEquals(right, opponent!.Hub))
            || (ReferenceEquals(right, challenger!.Hub) && ReferenceEquals(left, opponent!.Hub)));

    internal void Enable()
    {
        enabled = true;
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
    }
    private void OnMapGenerated()
    {
        Reset(true);
        DestroyExtras();
        Room? surface = Room.List.FirstOrDefault(r => r.Zone == MapGeneration.FacilityZone.Surface);
        if (surface == null) return;
        arena.Spawn(surface.Position + Vector3.up * Mathf.Clamp(config.SurfaceHeightOffset, 100f, 400f));
        // A separate protected viewing deck keeps observers out of the match and its hitboxes.
        AddPrimitive(arena.Origin + new Vector3(0, 2.5f, 18f), new Vector3(12, 0.2f, 5), Color.gray);
        Logger.Info($"[DuelArena] Model spawned: {arena.PrimitiveCount} primitives, {arena.LightCount} lights; {Status}");
    }
    private void OnRestart() { Reset(false); DestroyExtras(); arena.Destroy(); }
    private void OnMapGenerated(MapGeneratedEventArgs ev) => OnMapGenerated();

    internal bool Enter(Player player, string userId)
    {
        if (!Available(player, userId)) return false;
        if (ReferenceEquals(challenger?.Player, player) || IsFighting(player)) return true;
        if (challenger != null)
        {
            if (participants.TryGetValue(player.ReferenceHub, out Participant existing) && existing.Viewer)
            {
                Tell(player, "决斗台正在使用，你可以留在观众席等待下一场");
                return true;
            }
            return JoinViewing(player, out _);
        }
        challenger = ClaimFighter(player);
        expires = Now + Mathf.Clamp(config.LobbySeconds, 15f, 600f);
        invited.Clear();
        selected.Clear();
        try
        {
            Prepare(challenger, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -13));
            Logger.Info($"[DuelArena] Lobby entered: player={player.PlayerId}; life={player.LifeId}; position={player.Position}");
            RefreshBoard();
            Tell(player, "射击或按 E 选择名字（可多选），然后选择「发送邀请」；首位输入 .accept 或 .yes 的玩家进入决斗");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[DuelArena] Entry failed: {ex}");
            Reset(true);
            return false;
        }
    }
    internal bool RequestChallenge(Player player, int? targetId, out string response)
    {
        response = "请先进入观众席，再输入 .challenge [擂台玩家 ID] 申请挑战";
        if (!Available(player, player.UserId) || !participants.TryGetValue(player.ReferenceHub, out Participant viewer)
            || !viewer.Viewer || !Valid(viewer) || !player.IsAlive) return false;
        Participant? target = targetId.HasValue
            ? new[] { challenger, opponent }.FirstOrDefault(p => p != null && p.PlayerId == targetId.Value)
            : Fighting ? null : challenger;
        if (target == null || !Valid(target) || !target.Player.IsAlive)
        {
            response = Fighting
                ? $"输入 .challenge {challenger!.PlayerId} 或 .challenge {opponent!.PlayerId} 选择擂台上的玩家"
                : "当前没有可挑战的擂台玩家；输入 .duel 发起决斗";
            return false;
        }
        requests.RemoveAll(request => ReferenceEquals(request.Viewer, viewer));
        float lifetime = Mathf.Clamp(config.InvitationSeconds, 5f, 120f);
        requests.Add(new ChallengeRequest(viewer, target, Now + lifetime + (Fighting ? Mathf.Clamp(config.MatchSeconds, 15f, 600f) + 3f : 0f)));
        Tell(target.Player, $"观众 {Safe(player.Nickname)}（ID {player.PlayerId}）申请挑战你\n{(Fighting ? "本场结束后" : "")}输入 .accept {player.PlayerId} 接受；挑战仍为 1v1");
        response = Fighting ? "挑战申请已发送，等待本场结束后由对方接受" : "挑战申请已发送，等待对方接受";
        return true;
    }
    internal bool Accept(Player player, out string response, int? requesterId = null)
    {
        if (!enabled || !arena.Ready || !(ModeActive?.Invoke() ?? true))
            return RejectAccept(player, requesterId, "unavailable", "决斗功能当前未开放，请稍后再试", out response);
        if (!Current(player, player.UserId))
            return RejectAccept(player, requesterId, "ineligible", "当前角色或连接状态无法参加决斗", out response);
        PruneRequests();
        ChallengeRequest? request = requests.FirstOrDefault(r => ReferenceEquals(r.Target.Player, player)
            && (!requesterId.HasValue || r.Viewer.PlayerId == requesterId.Value));
        if (request != null)
        {
            if (Fighting)
                return RejectAccept(player, requesterId, "request-during-match", "请在本场决斗结束后接受观众的挑战申请", out response);
            if (challenger != null && !ReferenceEquals(challenger, request.Target))
                return RejectAccept(player, requesterId, "slot-reserved", "决斗台正在使用，请等待空闲后再接受挑战", out response);
            challenger = request.Target;
            challenger.Viewer = false;
            return BeginMatch(request.Viewer.Player, out response);
        }
        if (Fighting)
            return RejectAccept(player, requesterId, "match-started", "本场决斗已经开始，无法再接受邀请；输入 .spectate 可观战", out response);
        if (challenger == null)
            return RejectAccept(player, requesterId, "no-lobby", "当前没有等待中的决斗邀请或挑战申请，请让对方重新发送", out response);
        if (!invited.TryGetValue(player.ReferenceHub, out string userId) || !Current(player, userId))
            return RejectAccept(player, requesterId, "not-invited", "没有发给你的决斗邀请或对应挑战申请，请让对方重新发送", out response);
        if (requesterId.HasValue && requesterId.Value != challenger.PlayerId)
            return RejectAccept(player, requesterId, "wrong-inviter", $"邀请者 ID 不匹配；输入 .accept {challenger.PlayerId} 或 .accept 接受", out response);
        if (Now >= expires)
            return RejectAccept(player, requesterId, "expired", "决斗邀请已过期，请让对方重新发送", out response);
        if (!Valid(challenger) || !challenger.Player.IsAlive)
            return RejectAccept(player, requesterId, "inviter-unavailable", "邀请者已离开或死亡，请等待新的邀请", out response);
        if (participants.TryGetValue(player.ReferenceHub, out Participant existing) && !existing.Viewer)
            return RejectAccept(player, requesterId, "already-participating", "你已在决斗台上，无法接受这份邀请", out response);
        return BeginMatch(player, out response);
    }
    private bool RejectAccept(Player player, int? requesterId, string reason, string message, out string response)
    {
        Logger.Info($"[DuelArena] Acceptance rejected: player={player.PlayerId}; requester={requesterId?.ToString() ?? "auto"}; "
            + $"reason={reason}; remaining={Math.Max(0f, expires - Now):0.0}; {Status}");
        response = message;
        return false;
    }
    private bool BeginMatch(Player player, out string response)
    {
        // Claim synchronously before any native role callbacks. Later accepts see Fighting immediately.
        opponent = ClaimFighter(player);
        requests.Clear();
        invited.Clear();
        selected.Clear();
        DestroyBoard();
        matchStart = Now + 3f;
        expires = matchStart + Mathf.Clamp(config.MatchSeconds, 15f, 600f);
        try
        {
            Prepare(challenger!, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -8));
            Prepare(opponent, RoleTypeId.ChaosConscript, arena.Origin + new Vector3(0, 1, 8));
            Tell(challenger!.Player, $"对手：{Safe(player.Nickname)}；3 秒后开始决斗");
            Tell(player, "已接受决斗！3 秒后开始；输入 .leave 可认输");
            Logger.Info($"[DuelArena] Accepted: {challenger.UserId} vs {opponent.UserId}");
            response = "已进入决斗，3 秒后开始";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[DuelArena] Match setup failed: {ex}");
            Reset(true);
            response = "决斗准备失败，已返回地表";
            return false;
        }
    }
    internal bool Watch(Player player, out string response)
    {
        response = "决斗观众席暂不可用";
        if (!Available(player, player.UserId)) return false;
        if (participants.TryGetValue(player.ReferenceHub, out Participant existing))
        {
            if (!existing.Viewer) { response = "请先输入 .leave 退出自己的决斗"; return false; }
            Release(existing, true);
            response = "已退出观战，返回地表";
            return true;
        }
        return JoinViewing(player, out response);
    }
    private bool JoinViewing(Player player, out string response)
    {
        response = "决斗观众席暂不可用";
        Participant viewer = Capture(player, true);
        try
        {
            PrepareViewer(viewer);
            Tell(player, "已进入观众席；输入 .challenge [擂台玩家 ID] 申请挑战，或接受邀请\n空闲时 .duel 发起决斗；.leave 或再次 .spectate 返回地表");
            response = "已进入决斗观战区";
            return true;
        }
        catch { Release(viewer, true); throw; }
    }
    internal void Leave(Player player, string reason)
    {
        if (!participants.TryGetValue(player.ReferenceHub, out Participant participant)) return;
        bool viewer = participant.Viewer;
        Participant? winner = ReferenceEquals(participant, challenger) ? opponent : challenger;
        Release(participant, true);
        if (!viewer) Finish(reason, winner);
    }

    private Participant Capture(Player player, bool viewer)
    {
        var participant = new Participant(player, viewer);
        participants.Add(participant.Hub, participant);
        return participant;
    }
    private Participant ClaimFighter(Player player)
    {
        Participant participant = participants.TryGetValue(player.ReferenceHub, out Participant existing)
            ? existing : Capture(player, false);
        participant.Viewer = false;
        requests.RemoveAll(request => ReferenceEquals(request.Viewer, participant));
        return participant;
    }
    private void PrepareViewer(Participant participant)
    {
        participant.Viewer = true;
        Prepare(participant, RoleTypeId.NtfPrivate, ViewingPosition);
        participant.Player.Rotation = Quaternion.Euler(0, 180, 0);
    }
    private static bool Valid(Participant participant) => Current(participant.Player, participant.UserId)
        && ReferenceEquals(participant.Player.ReferenceHub, participant.Hub);
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
            participant.LifeId = player.LifeId;
        }
        finally { transitioning = false; }
    }
    private void Release(Participant participant, bool returnToSurface)
    {
        participants.Remove(participant.Hub);
        RemoveRequests(participant);
        if (!Valid(participant) || !returnToSurface) return;
        transitioning = true;
        try
        {
            // Release ownership before warmup's native spawn routing reclaims this player.
            participant.Player.SetRole(RoleTypeId.Spectator);
            if (ReturnToSurface?.Invoke(participant.Player) != true)
            {
                participant.Player.SetRole(RoleTypeId.NtfPrivate);
                participant.Player.ReferenceHub.TryOverridePosition(participant.ReturnPosition);
            }
        }
        finally { transitioning = false; }
    }
    private void Finish(string reason, Participant? winner)
    {
        Logger.Info($"[DuelArena] Slot released: challenger={challenger?.PlayerId}; "
            + $"opponent={opponent?.PlayerId}; winner={winner?.PlayerId}; reason={reason}");
        Participant[] recipients = participants.Values.ToArray();
        Participant?[] fighters = { challenger, opponent };
        string message = winner != null && Valid(winner) ? $"{Safe(winner.Player.Nickname)} 获胜！{reason}" : reason;
        ClearMatch();
        foreach (Participant? participant in fighters)
        {
            if (participant == null || !participants.ContainsKey(participant.Hub)) continue;
            if (!Valid(participant)) { participants.Remove(participant.Hub); continue; }
            try { PrepareViewer(participant); }
            catch (Exception ex)
            {
                Logger.Warn($"[DuelArena] Viewing transition failed: {ex}");
                Release(participant, true);
            }
        }
        foreach (Participant participant in recipients)
            if (Valid(participant)) Tell(participant.Player, message + "\n留在观众席等待下一场；输入 .duel 发起决斗，.leave 返回地表");
        PruneRequests();
        foreach (ChallengeRequest request in requests)
        {
            request.Expires = Now + Mathf.Clamp(config.InvitationSeconds, 5f, 120f);
            Tell(request.Target.Player, $"观众 {Safe(request.Viewer.Player.Nickname)} 的挑战申请等待接受\n输入 .accept {request.Viewer.PlayerId} 接受（{Mathf.CeilToInt(request.Expires - Now)} 秒内）");
        }
    }
    private void ClearMatch()
    {
        pendingDeath = null;
        challenger = null;
        opponent = null;
        invited.Clear();
        selected.Clear();
        DestroyBoard();
        expires = 0;
        page = 0;
    }
    private void Reset(bool returnToSurface)
    {
        ClearMatch();
        requests.Clear();
        foreach (Participant participant in participants.Values.ToArray())
        {
            try { Release(participant, returnToSurface); }
            catch (Exception ex) { Logger.Warn($"[DuelArena] Participant release failed: {ex}"); }
        }
        participants.Clear();
    }
    private IEnumerator<float> Pump()
    {
        while (enabled)
        {
            try
            {
                // Finish after the native damage/role stack has unwound. Changing
                // either role from inside Death can leave FPC at its previous position.
                Participant? deceased = pendingDeath;
                pendingDeath = null;
                if (deceased != null && participants.ContainsKey(deceased.Hub))
                {
                    if (deceased.Viewer) PrepareViewer(deceased);
                    else Finish("决斗结束", ReferenceEquals(deceased, challenger) ? opponent : challenger);
                }
                if (ModeActive?.Invoke() == false && participants.Count != 0) Reset(true);
                PruneRequests();
                if (challenger != null)
                {
                    if (!Valid(challenger) || (Fighting && !Valid(opponent!))) Finish("玩家离开决斗", null);
                    else if (Now >= expires) Finish(Fighting ? "决斗超时" : "邀请或等待已超时", null);
                    else if (!arena.Contains(challenger.Player.Position)) Finish("离开决斗范围", opponent);
                    else if (Fighting && !arena.Contains(opponent!.Player.Position)) Finish("离开决斗范围", challenger);
                    else if (!Fighting && Now >= nextRefresh) { RefreshBoard(); nextRefresh = Now + 1f; }
                }
                foreach (Participant viewer in participants.Values.Where(p => p.Viewer).ToArray())
                {
                    if (!Valid(viewer)) { participants.Remove(viewer.Hub); continue; }
                    Vector3 delta = viewer.Player.Position - ViewingPosition;
                    if (Math.Abs(delta.x) > 6 || Math.Abs(delta.z) > 2.5f || Math.Abs(delta.y) > 2)
                        viewer.Hub.TryOverridePosition(ViewingPosition);
                }
            }
            catch (Exception ex) { Logger.Warn($"[DuelArena] Runtime recovery: {ex}"); Reset(true); }
            yield return Timing.WaitForSeconds(0.2f);
        }
    }
    private void OnLeft(PlayerLeftEventArgs ev)
    {
        invited.Remove(ev.Player.ReferenceHub);
        selected.Remove(ev.Player.ReferenceHub);
        // The departing hub is already being destroyed. Never route or respawn it.
        if (!participants.TryGetValue(ev.Player.ReferenceHub, out Participant departing)) return;
        participants.Remove(departing.Hub);
        RemoveRequests(departing);
        if (!departing.Viewer)
            Finish("玩家离开服务器", ReferenceEquals(departing, challenger) ? opponent : challenger);
    }
    private void OnDeath(PlayerDeathEventArgs ev)
    {
        if (participants.TryGetValue(ev.Player.ReferenceHub, out Participant deceased))
        {
            RemoveRequests(deceased);
            pendingDeath = deceased;
        }
    }
    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        if (ev.ChangeReason == RoleChangeReason.Died) return;
        if (!transitioning && Owns(ev.Player.ReferenceHub))
        {
            Logger.Info($"[DuelArena] External role change: player={ev.Player.PlayerId}; role={ev.Player.Role}; reason={ev.ChangeReason}");
            Participant participant = participants[ev.Player.ReferenceHub];
            Release(participant, false);
            if (!participant.Viewer)
                Finish("角色发生变化，决斗结束", ReferenceEquals(participant, challenger) ? opponent : challenger);
        }
    }
    private void OnHurting(PlayerHurtingEventArgs ev)
    {
        bool victim = Owns(ev.Player.ReferenceHub);
        bool attacker = ev.Attacker != null && Owns(ev.Attacker.ReferenceHub);
        if ((victim || attacker) && (ev.Attacker == null || !CanFight(ev.Attacker.ReferenceHub, ev.Player.ReferenceHub)))
            ev.IsAllowed = false;
    }
    private void OnShooting(PlayerShootingWeaponEventArgs ev)
    {
        if (!Owns(ev.Player.ReferenceHub)) return;
        if (!ReferenceEquals(ev.Player, challenger?.Player) || Fighting)
        {
            if (!IsFighting(ev.Player) || Now < matchStart) ev.IsAllowed = false;
            return;
        }
        Ray ray = new(ev.Player.Camera.position, ev.Player.Camera.forward);
        Row? target = rows.Select(row => (row, distance: row.Bounds.IntersectRay(ray, out float d) ? d : float.PositiveInfinity))
            .Where(entry => entry.distance < 5f).OrderBy(entry => entry.distance).Select(entry => entry.row).FirstOrDefault();
        if (target != null) SelectRow(ev.Player, target);
        ev.IsAllowed = false;
    }
    private void OnInteracted(PlayerInteractedToyEventArgs ev)
    {
        Row? row = rows.FirstOrDefault(r => ReferenceEquals(r.Interactable, ev.Interactable));
        if (row != null && Vector3.Distance(ev.Player.Camera.position, row.Bounds.center) < 4f) SelectRow(ev.Player, row);
    }
    private void SelectRow(Player player, Row row)
    {
        if (!ReferenceEquals(player, challenger?.Player) || Fighting || !Valid(challenger!) || Now - lastClick < 0.2f) return;
        lastClick = Now;
        if (row.Action == "invite") { Invite(); return; }
        if (row.Action == "next") { page++; rosterKey = ""; RefreshBoard(); return; }
        if (row.Action == "previous") { page = Math.Max(0, page - 1); rosterKey = ""; RefreshBoard(); return; }
        if (row.Action == "all")
        {
            Player[] pool = Candidates();
            bool clear = pool.All(p => selected.Contains(p.ReferenceHub));
            selected.Clear();
            if (!clear) foreach (Player candidate in pool) selected.Add(candidate.ReferenceHub);
        }
        else if (row.Candidate != null && Current(row.Candidate, row.UserId) && IsCandidate(row.Candidate))
        {
            if (!selected.Add(row.Candidate.ReferenceHub)) selected.Remove(row.Candidate.ReferenceHub);
        }
        rosterKey = "";
        RefreshBoard();
    }
    private bool IsCandidate(Player player) => !participants.TryGetValue(player.ReferenceHub, out Participant participant) || participant.Viewer;
    private void RemoveRequests(Participant participant) => requests.RemoveAll(request =>
        ReferenceEquals(request.Viewer, participant) || ReferenceEquals(request.Target, participant));
    private void PruneRequests() => requests.RemoveAll(request => Now >= request.Expires
        || !Valid(request.Viewer) || !Valid(request.Target) || !request.Viewer.Viewer
        || !participants.TryGetValue(request.Viewer.Hub, out Participant viewer) || !ReferenceEquals(viewer, request.Viewer)
        || !participants.TryGetValue(request.Target.Hub, out Participant target) || !ReferenceEquals(target, request.Target)
        || !request.Viewer.Player.IsAlive || !request.Target.Player.IsAlive);
    private Player[] Candidates() => Player.ReadyList.Where(p => Eligible(p) && IsCandidate(p))
        .OrderBy(p => p.PlayerId).ToArray();
    private void Invite()
    {
        if (challenger == null || invited.Count > 0) return;
        Player[] targets = Candidates().Where(p => selected.Contains(p.ReferenceHub)).ToArray();
        if (targets.Length == 0) { Tell(challenger.Player, "请先选择至少一名玩家"); return; }
        expires = Now + Mathf.Clamp(config.InvitationSeconds, 5f, 120f);
        foreach (Player target in targets)
        {
            invited[target.ReferenceHub] = target.UserId;
            Tell(target, $"{Safe(challenger.Player.Nickname)}（ID {challenger.PlayerId}）邀请你进行 1v1 决斗\n在控制台输入 .accept、.yes 或 .accept {challenger.PlayerId}；首位接受者进入（{Mathf.CeilToInt(expires - Now)} 秒）");
        }
        Tell(challenger.Player, $"已邀请 {targets.Length} 名玩家；等待首位接受者");
        Logger.Info($"[DuelArena] Invitations sent: challenger={challenger.PlayerId}; "
            + $"targets={string.Join(",", targets.Select(target => target.PlayerId))}; seconds={Mathf.CeilToInt(expires - Now)}");
        rosterKey = "";
        RefreshBoard();
    }
    private int page;
    private void RefreshBoard()
    {
        if (challenger == null || Fighting) return;
        Player[] candidates = Candidates();
        selected.RemoveWhere(hub => !candidates.Any(p => ReferenceEquals(p.ReferenceHub, hub)));
        int pages = Math.Max(1, (candidates.Length + 7) / 8);
        page = Math.Min(page, pages - 1);
        string key = page + ":" + invited.Count + ":" + string.Join("|", candidates.Select(p => p.PlayerId + ":" + p.UserId + ":" + p.Nickname + ":" + selected.Contains(p.ReferenceHub)));
        if (key == rosterKey) return;
        DestroyBoard();
        rosterKey = key;
        AddRow(new Vector3(0, 2.4f, -10.5f), "<b>决斗台 · 选择对手</b>", "header", null);
        int index = 0;
        foreach (Player candidate in candidates.Skip(page * 8).Take(8))
        {
            float x = index % 2 == 0 ? -1.15f : 1.15f;
            float y = 2.05f - index / 2 * 0.33f;
            AddRow(new Vector3(x, y, -10.5f), $"{(selected.Contains(candidate.ReferenceHub) ? "●" : "○")} {Safe(candidate.Nickname)}", "player", candidate);
            index++;
        }
        AddRow(new Vector3(-1.15f, 0.7f, -10.5f), "选择所有玩家", "all", null);
        AddRow(new Vector3(1.15f, 0.7f, -10.5f), invited.Count == 0 ? "发送邀请" : "等待接受…", "invite", null);
        if (pages > 1)
        {
            AddRow(new Vector3(-1.15f, 2.85f, -10.5f), $"上一页  {page + 1}/{pages}", "previous", null);
            AddRow(new Vector3(1.15f, 2.85f, -10.5f), "下一页", "next", null);
        }
    }
    private void AddRow(Vector3 local, string label, string action, Player? candidate)
    {
        Vector3 position = arena.Origin + local;
        float width = action == "header" ? 4.5f : 2.2f;
        float glyphWidth = label.Sum(c => c < 128 ? 0.55f : 1f);
        float fontSize = Math.Min(2.2f, width / (0.09f * Math.Max(1f, glyphWidth)));
        var background = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(position + new Vector3(0, 0, 0.04f), Quaternion.identity,
            new Vector3(width, 0.3f, 0.04f)));
        background.Type = PrimitiveType.Cube;
        background.Flags = AdminToys.PrimitiveFlags.Visible;
        background.Color = new Color(0.05f, 0.08f, 0.12f);
        background.IsStatic = true;
        background.Spawn();
        var text = TextToy.Get(ToyFactory.Create<AdminToys.TextToy>(position, Quaternion.identity, Vector3.one * 0.9f));
        text.DisplaySize = new Vector2(width / 0.09f, 3.4f);
        text.TextFormat = "<align=center><size=" + fontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ">" + label + "</size></align>";
        text.IsStatic = true;
        text.Spawn();
        var interactable = InteractableToy.Get(ToyFactory.Create<AdminToys.InvisibleInteractableToy>(position, Quaternion.identity, new Vector3(width, 0.3f, 0.1f)));
        interactable.IsStatic = true;
        interactable.InteractionDuration = 0;
        interactable.Spawn();
        rows.Add(new Row(text, interactable, background, new Bounds(position, new Vector3(width, 0.3f, 0.1f)), action, candidate));
    }
    private readonly List<PrimitiveObjectToy> extras = new();
    private void DestroyExtras()
    {
        foreach (PrimitiveObjectToy toy in extras) if (toy.Base != null) toy.Destroy();
        extras.Clear();
    }
    private void AddPrimitive(Vector3 position, Vector3 scale, Color color)
    {
        var toy = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(position, Quaternion.identity, scale));
        toy.Type = PrimitiveType.Cube;
        toy.Flags = AdminToys.PrimitiveFlags.Visible | AdminToys.PrimitiveFlags.Collidable;
        toy.Color = color;
        toy.IsStatic = true;
        toy.Spawn();
        extras.Add(toy);
    }
    private void DestroyBoard()
    {
        foreach (Row row in rows)
        {
            if (row.Text.Base != null) row.Text.Destroy();
            if (row.Interactable.Base != null) row.Interactable.Destroy();
            if (row.Background.Base != null) row.Background.Destroy();
        }
        rows.Clear();
        rosterKey = "";
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
        public Participant(Player player, bool viewer)
        {
            Player = player; Hub = player.ReferenceHub; UserId = player.UserId; PlayerId = player.PlayerId;
            Viewer = viewer; ReturnPosition = player.Position;
        }
        public Player Player { get; }
        public ReferenceHub Hub { get; }
        public string UserId { get; }
        public int PlayerId { get; }
        public bool Viewer { get; set; }
        public Vector3 ReturnPosition { get; }
        public int LifeId { get; set; }
    }
    private sealed class Row
    {
        public Row(TextToy text, InteractableToy interactable, PrimitiveObjectToy background, Bounds bounds, string action, Player? candidate)
        { Text = text; Interactable = interactable; Background = background; Bounds = bounds; Action = action; Candidate = candidate; UserId = candidate?.UserId ?? ""; }
        public TextToy Text { get; }
        public InteractableToy Interactable { get; }
        public PrimitiveObjectToy Background { get; }
        public Bounds Bounds { get; }
        public string Action { get; }
        public Player? Candidate { get; }
        public string UserId { get; }
    }
    private sealed class ChallengeRequest
    {
        public ChallengeRequest(Participant viewer, Participant target, float expires)
        { Viewer = viewer; Target = target; Expires = expires; }
        public Participant Viewer { get; }
        public Participant Target { get; }
        public float Expires { get; set; }
    }
    private sealed class HubComparer : IEqualityComparer<ReferenceHub>
    {
        public static readonly HubComparer Instance = new();
        public bool Equals(ReferenceHub? left, ReferenceHub? right) => ReferenceEquals(left, right);
        public int GetHashCode(ReferenceHub hub) => RuntimeHelpers.GetHashCode(hub);
    }
}
