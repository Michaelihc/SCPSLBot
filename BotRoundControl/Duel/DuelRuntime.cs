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
    private readonly List<Row> rows = new();
    private Participant? challenger;
    private Participant? opponent;
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
    internal bool Fighting => opponent != null;
    internal Vector3 Origin => arena.Origin;
    internal string Status => $"ready={arena.Ready}; state={(Fighting ? "fighting" : challenger != null ? "lobby" : "idle")}; "
        + $"challenger={challenger?.Player.PlayerId ?? 0}; opponent={opponent?.Player.PlayerId ?? 0}; invited={invited.Count}; "
        + $"viewers={participants.Values.Count(p => p.Viewer)}; origin={Origin}";

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
        if (ReferenceEquals(challenger?.Player, player)) return true;
        if (challenger != null || Owns(player.ReferenceHub))
        {
            Tell(player, "决斗台正在使用，请稍后再试；输入 .spectate 可观战");
            return false;
        }
        challenger = Capture(player, false);
        expires = Now + Mathf.Clamp(config.LobbySeconds, 15f, 600f);
        invited.Clear();
        selected.Clear();
        try
        {
            Prepare(challenger, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -12));
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
    internal bool Accept(Player player, out string response)
    {
        response = "没有可接受的决斗邀请，或邀请已过期";
        if (!Available(player, player.UserId) || challenger == null || Fighting || Now >= expires
            || !invited.TryGetValue(player.ReferenceHub, out string userId) || !Current(player, userId)
            || Owns(player.ReferenceHub) || !Valid(challenger)) return false;
        // Claim synchronously before any native role callbacks. Later accepts see Fighting immediately.
        opponent = Capture(player, false);
        invited.Clear();
        selected.Clear();
        DestroyBoard();
        matchStart = Now + 3f;
        expires = matchStart + Mathf.Clamp(config.MatchSeconds, 15f, 600f);
        try
        {
            Prepare(challenger, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 1, -8));
            Prepare(opponent, RoleTypeId.ChaosConscript, arena.Origin + new Vector3(0, 1, 8));
            Tell(challenger.Player, $"{Safe(player.Nickname)} 已接受！3 秒后开始决斗");
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
        response = "当前没有可观战的决斗";
        if (!Available(player, player.UserId)) return false;
        if (participants.TryGetValue(player.ReferenceHub, out Participant existing))
        {
            if (!existing.Viewer) { response = "请先输入 .leave 退出自己的决斗"; return false; }
            Release(existing, true);
            response = "已退出观战，返回地表";
            return true;
        }
        if (!Fighting) return false;
        Participant viewer = Capture(player, true);
        try
        {
            Prepare(viewer, RoleTypeId.NtfPrivate, arena.Origin + new Vector3(0, 3.5f, 18));
            player.ClearInventory();
            player.Rotation = Quaternion.Euler(0, 180, 0);
            Tell(player, "观战中；再次输入 .spectate / .view / .watch 返回地表");
            response = "已进入决斗观战区";
            return true;
        }
        catch { Release(viewer, true); throw; }
    }
    internal void Leave(Player player, string reason)
    {
        if (!participants.TryGetValue(player.ReferenceHub, out Participant participant)) return;
        if (participant.Viewer) Release(participant, true);
        else Finish(reason, ReferenceEquals(participant, challenger) ? opponent : challenger);
    }

    private Participant Capture(Player player, bool viewer)
    {
        var participant = new Participant(player, viewer);
        participants.Add(participant.Hub, participant);
        return participant;
    }
    private static bool Valid(Participant participant) => Current(participant.Player, participant.UserId)
        && ReferenceEquals(participant.Player.ReferenceHub, participant.Hub);
    private void Prepare(Participant participant, RoleTypeId role, Vector3 position)
    {
        if (!Valid(participant)) throw new InvalidOperationException("Participant identity changed.");
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
        foreach (Participant participant in participants.Values.ToArray())
            if (Valid(participant)) Tell(participant.Player, winner == null ? reason : $"{Safe(winner.Player.Nickname)} 获胜！{reason}");
        Reset(true);
    }
    private void Reset(bool returnToSurface)
    {
        challenger = null;
        opponent = null;
        invited.Clear();
        selected.Clear();
        foreach (Participant participant in participants.Values.ToArray())
        {
            try { Release(participant, returnToSurface); }
            catch (Exception ex) { Logger.Warn($"[DuelArena] Participant release failed: {ex}"); }
        }
        participants.Clear();
        DestroyBoard();
        expires = 0;
        page = 0;
    }
    private IEnumerator<float> Pump()
    {
        while (enabled)
        {
            try
            {
                if (ModeActive?.Invoke() == false && participants.Count != 0) Reset(true);
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
                    Vector3 delta = viewer.Player.Position - (arena.Origin + new Vector3(0, 3.5f, 18));
                    if (Math.Abs(delta.x) > 6 || Math.Abs(delta.z) > 2.5f || Math.Abs(delta.y) > 2)
                        viewer.Hub.TryOverridePosition(arena.Origin + new Vector3(0, 3.5f, 18));
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
        Leave(ev.Player, "玩家离开服务器");
    }
    private void OnDeath(PlayerDeathEventArgs ev) => Leave(ev.Player, "决斗结束");
    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        if (ev.ChangeReason == RoleChangeReason.Died) return;
        if (!transitioning && Owns(ev.Player.ReferenceHub)) Leave(ev.Player, "角色发生变化，决斗结束");
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
        else if (row.Candidate != null && Current(row.Candidate, row.UserId) && !Owns(row.Candidate.ReferenceHub))
        {
            if (!selected.Add(row.Candidate.ReferenceHub)) selected.Remove(row.Candidate.ReferenceHub);
        }
        rosterKey = "";
        RefreshBoard();
    }
    private Player[] Candidates() => Player.ReadyList.Where(p => Eligible(p) && !Owns(p.ReferenceHub))
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
            Tell(target, $"{Safe(challenger.Player.Nickname)} 邀请你进行 1v1 决斗\n在控制台输入 .accept 或 .yes；首位接受者进入（{Mathf.CeilToInt(expires - Now)} 秒）");
        }
        Tell(challenger.Player, $"已邀请 {targets.Length} 名玩家；等待首位接受者");
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
        AddRow(new Vector3(0, 2.8f, -10.5f), "<b>决斗台 · 选择对手</b>", "header", null);
        int index = 0;
        foreach (Player candidate in candidates.Skip(page * 8).Take(8))
        {
            float x = index % 2 == 0 ? -3f : 3f;
            float y = 2.25f - index / 2 * 0.45f;
            AddRow(new Vector3(x, y, -10.5f), $"{(selected.Contains(candidate.ReferenceHub) ? "●" : "○")} {Safe(candidate.Nickname)}", "player", candidate);
            index++;
        }
        AddRow(new Vector3(-3, 0.4f, -10.5f), "选择所有玩家", "all", null);
        AddRow(new Vector3(3, 0.4f, -10.5f), invited.Count == 0 ? "发送邀请" : "等待接受…", "invite", null);
        if (pages > 1)
        {
            AddRow(new Vector3(-3, 3.3f, -10.5f), $"上一页  {page + 1}/{pages}", "previous", null);
            AddRow(new Vector3(3, 3.3f, -10.5f), "下一页", "next", null);
        }
    }
    private void AddRow(Vector3 local, string label, string action, Player? candidate)
    {
        Vector3 position = arena.Origin + local;
        var background = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(position + new Vector3(0, 0, 0.04f), Quaternion.identity,
            new Vector3(5.15f, 0.42f, 0.04f)));
        background.Type = PrimitiveType.Cube;
        background.Flags = AdminToys.PrimitiveFlags.Visible;
        background.Color = new Color(0.05f, 0.08f, 0.12f);
        background.IsStatic = true;
        background.Spawn();
        var text = TextToy.Get(ToyFactory.Create<AdminToys.TextToy>(position, Quaternion.identity, Vector3.one * 0.9f));
        text.DisplaySize = new Vector2(110, 9);
        text.TextFormat = "<align=center><size=7>" + label + "</size></align>";
        text.IsStatic = true;
        text.Spawn();
        var interactable = InteractableToy.Get(ToyFactory.Create<AdminToys.InvisibleInteractableToy>(position, Quaternion.identity, new Vector3(5f, 0.38f, 0.1f)));
        interactable.IsStatic = true;
        interactable.InteractionDuration = 0;
        interactable.Spawn();
        rows.Add(new Row(text, interactable, background, new Bounds(position, new Vector3(5f, 0.38f, 0.1f)), action, candidate));
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
            Player = player; Hub = player.ReferenceHub; UserId = player.UserId; Viewer = viewer; ReturnPosition = player.Position;
        }
        public Player Player { get; }
        public ReferenceHub Hub { get; }
        public string UserId { get; }
        public bool Viewer { get; }
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
    private sealed class HubComparer : IEqualityComparer<ReferenceHub>
    {
        public static readonly HubComparer Instance = new();
        public bool Equals(ReferenceHub? left, ReferenceHub? right) => ReferenceEquals(left, right);
        public int GetHashCode(ReferenceHub hub) => RuntimeHelpers.GetHashCode(hub);
    }
}
