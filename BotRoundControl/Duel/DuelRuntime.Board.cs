#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace DuelArena;

internal sealed partial class DuelRuntime
{
    private readonly List<Row> rows = new();
    private readonly List<PrimitiveObjectToy> extras = new();
    private string rosterKey = "";
    private int page;

    private void OnShooting(PlayerShootingWeaponEventArgs ev)
    {
        if (!Owns(ev.Player.ReferenceHub) || (IsFighting(ev.Player) && Now >= matchStart)) return;
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
        if (!participants.TryGetValue(player.ReferenceHub, out Participant sender) || !sender.Viewer || !Free(sender)
            || Now - sender.LastClick < 0.2f) return;
        sender.LastClick = Now;
        if (row.Action == "invite")
        {
            Player[] targets = sender.Selected.Values.Where(target => Current(target.Player, target.UserId) && CanInvite(sender, target.Player))
                .Select(target => target.Player).ToArray();
            if (!SendInvitations(sender, targets, out string response)) Tell(player, response);
            return;
        }
        if (row.Action == "next") { page++; rosterKey = ""; RefreshBoard(); return; }
        if (row.Action == "previous") { page = Math.Max(0, page - 1); rosterKey = ""; RefreshBoard(); return; }
        if (row.Action == "all")
        {
            Player[] pool = Candidates().Where(target => CanInvite(sender, target)).ToArray();
            bool clear = pool.All(target => sender.Selected.ContainsKey(target.ReferenceHub));
            sender.Selected.Clear();
            if (!clear) foreach (Player target in pool) sender.Selected[target.ReferenceHub] = new SelectedPlayer(target);
        }
        else if (row.Candidate != null && Current(row.Candidate, row.UserId) && CanInvite(sender, row.Candidate))
        {
            if (!sender.Selected.Remove(row.Candidate.ReferenceHub)) sender.Selected[row.Candidate.ReferenceHub] = new SelectedPlayer(row.Candidate);
        }
        sender.SelectionExpires = Now + Mathf.Clamp(config.LobbySeconds, 15f, 600f);
        RefreshHints();
    }
    private Player[] Candidates() => Player.ReadyList.Where(player => Available(player, player.UserId) && IsCandidate(player))
        .OrderBy(player => player.PlayerId).ToArray();
    private void RefreshBoard()
    {
        if (!arena.Ready) return;
        Player[] candidates = Candidates();
        int pages = Math.Max(1, (candidates.Length + 7) / 8);
        page = Math.Min(page, pages - 1);
        string key = page + ":" + string.Join("|", candidates.Select(p => p.PlayerId + ":" + p.UserId + ":" + p.Nickname));
        if (key == rosterKey) return;
        DestroyBoard();
        rosterKey = key;
        AddRow(new Vector3(0, 5f, 15.5f), "<b>观众席 · 选择对手</b>", "header", null);
        int index = 0;
        foreach (Player candidate in candidates.Skip(page * 8).Take(8))
        {
            float x = index % 2 == 0 ? -1.15f : 1.15f;
            float y = 4.65f - index / 2 * 0.33f;
            AddRow(new Vector3(x, y, 15.5f), $"{candidate.PlayerId} · {Safe(candidate.Nickname)}", "player", candidate);
            index++;
        }
        AddRow(new Vector3(-1.15f, 3.2f, 15.5f), "选择所有玩家", "all", null);
        AddRow(new Vector3(1.15f, 3.2f, 15.5f), "发送邀请", "invite", null);
        if (pages > 1)
        {
            AddRow(new Vector3(-1.15f, 5.45f, 15.5f), $"上一页  {page + 1}/{pages}", "previous", null);
            AddRow(new Vector3(1.15f, 5.45f, 15.5f), "下一页", "next", null);
        }
    }
    private void AddRow(Vector3 local, string label, string action, Player? candidate)
    {
        Vector3 position = arena.Origin + local;
        Quaternion rotation = Quaternion.Euler(0, 180, 0);
        float width = action == "header" ? 4.5f : 2.2f;
        float glyphWidth = label.Sum(c => c < 128 ? 0.55f : 1f);
        float fontSize = Math.Min(2.2f, width / (0.09f * Math.Max(1f, glyphWidth)));
        var background = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(position - new Vector3(0, 0, 0.04f), rotation,
            new Vector3(width, 0.3f, 0.04f)));
        background.Type = PrimitiveType.Cube;
        background.Flags = AdminToys.PrimitiveFlags.Visible;
        background.Color = new Color(0.05f, 0.08f, 0.12f);
        background.IsStatic = true;
        background.Spawn();
        var text = TextToy.Get(ToyFactory.Create<AdminToys.TextToy>(position, rotation, Vector3.one * 0.9f));
        text.DisplaySize = new Vector2(width / 0.09f, 3.4f);
        text.TextFormat = "<align=center><size=" + fontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ">" + label + "</size></align>";
        text.IsStatic = true;
        text.Spawn();
        var interactable = InteractableToy.Get(ToyFactory.Create<AdminToys.InvisibleInteractableToy>(position, rotation, new Vector3(width, 0.3f, 0.1f)));
        interactable.IsStatic = true;
        interactable.InteractionDuration = 0;
        interactable.Spawn();
        rows.Add(new Row(text, interactable, background, new Bounds(position, new Vector3(width, 0.3f, 0.1f)), action, candidate));
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
}
