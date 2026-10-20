#!/usr/bin/env python3
"""Render a SCPSLBot round trace (JSONL) as one self-contained top-down HTML map.

Usage: render_round_map.py <trace.jsonl> -o <out.html>

Stdlib only. The output embeds the data, inline SVG and vanilla JS; it needs no network.
"""
import argparse
import html
import json
import math
import os
import sys

ZONE_ORDER = ["LightContainment", "HeavyContainment", "Entrance", "Surface", "Other"]
ZONE_LABELS = {
    "LightContainment": "Light Containment",
    "HeavyContainment": "Heavy Containment",
    "Entrance": "Entrance Zone",
    "Surface": "Surface",
    "Other": "Other",
}
ZONE_PAD_METERS = 8.0
ZONE_CELL = 5.0  # meters; cache granularity for point -> zone lookups
NAV_CELL = 3.0

# Team colors (the legend in the page lists them). Tutorial and facility guards are told apart by role.
COLOR_SCP = "#ef4444"
COLOR_CLASSD = "#fb923c"
COLOR_SCIENTIST = "#facc15"
COLOR_GUARD = "#94a3b8"
COLOR_NTF = "#3b82f6"
COLOR_CHAOS = "#22c55e"
COLOR_TUTORIAL = "#f472b6"
COLOR_FLAMINGO = "#22d3ee"
COLOR_OTHER = "#cbd5e1"


def role_color(role_name, team):
    if role_name == "Tutorial":
        return COLOR_TUTORIAL
    if team == "SCPs":
        return COLOR_SCP
    if team == "ClassD":
        return COLOR_CLASSD
    if team == "Scientists":
        return COLOR_SCIENTIST
    if team == "FoundationForces":
        return COLOR_GUARD if role_name == "FacilityGuard" else COLOR_NTF
    if team == "ChaosInsurgency":
        return COLOR_CHAOS
    if team == "Flamingos":
        return COLOR_FLAMINGO
    return COLOR_OTHER


class Room:
    __slots__ = ("name", "zone", "shape", "rot", "cx", "cy", "cz", "sx", "sy", "sz", "volume")

    def __init__(self, raw):
        self.name = raw.get("name", "?")
        self.zone = raw.get("zone", "Other")
        if self.zone not in ZONE_LABELS:
            self.zone = "Other"
        self.shape = raw.get("shape", "")
        self.rot = raw.get("rotY", 0.0)
        bounds = raw.get("bounds") or {}
        center = bounds.get("center") or raw.get("pos") or [0, 0, 0]
        size = bounds.get("size") or [0, 0, 0]
        self.cx, self.cy, self.cz = center
        self.sx, self.sy, self.sz = size
        self.volume = max(self.sx, 0.1) * max(self.sy, 0.1) * max(self.sz, 0.1)

    def distance(self, x, y, z, slack_y):
        dx = max(abs(x - self.cx) - self.sx / 2.0, 0.0)
        dy = max(abs(y - self.cy) - self.sy / 2.0 - slack_y, 0.0)
        dz = max(abs(z - self.cz) - self.sz / 2.0, 0.0)
        return math.sqrt(dx * dx + dy * dy + dz * dz)


class ZoneLocator:
    """Assigns a world position to a zone using the room bounds from the header."""

    def __init__(self, rooms):
        self.rooms = rooms
        self.cache = {}

    def locate(self, x, y, z, cell=ZONE_CELL):
        if not self.rooms:
            return "Map"
        key = (int(math.floor(x / cell)), int(math.floor(y / cell)), int(math.floor(z / cell)))
        zone = self.cache.get(key)
        if zone is not None:
            return zone
        best = None
        for room in self.rooms:
            if room.distance(x, y, z, 2.0) <= 0.0 and (best is None or room.volume < best.volume):
                best = room
        if best is None:
            best_distance = None
            for room in self.rooms:
                distance = room.distance(x, y, z, 0.0)
                if best_distance is None or distance < best_distance:
                    best, best_distance = room, distance
        zone = best.zone
        self.cache[key] = zone
        return zone


class Entity:
    def __init__(self, index, pid, nick, is_bot, managed, role_id, role, team, time):
        self.index = index
        self.pid = pid
        self.nick = nick
        self.is_bot = is_bot
        self.managed = managed
        self.roles = []
        self.samples = []  # (time, x, y, z, role_id)
        self.left = None
        if role is not None:
            self.roles.append({"t": time, "role": role, "roleId": role_id, "team": team, "reason": ""})


def read_trace(path):
    header = None
    navs = []
    lines = []
    with open(path, "r", encoding="utf-8") as handle:
        for number, raw in enumerate(handle, 1):
            raw = raw.strip()
            if not raw:
                continue
            try:
                record = json.loads(raw)
            except ValueError:
                # The last line of a live or crashed trace can be cut off.
                print("warning: skipped unreadable line %d" % number, file=sys.stderr)
                continue
            kind = record.get("t")
            if kind == "header":
                header = record
            elif kind == "nav":
                navs.append(record)
            else:
                lines.append(record)
    return header, navs, lines


def build(path):
    header, navs, records = read_trace(path)
    header = header or {}
    roles_table = {int(k): v for k, v in (header.get("roles") or {}).items()}
    rooms = [Room(r) for r in header.get("rooms") or []]
    locator = ZoneLocator(rooms)
    interval = float(header.get("interval") or 1.0)

    entities = []
    current = {}

    def role_info(role_id, fallback_name=None, fallback_team=None):
        entry = roles_table.get(role_id)
        if entry:
            return entry[0], entry[1]
        return fallback_name or str(role_id), fallback_team or ""

    def ensure(pid, time, nick=None):
        entity = current.get(pid)
        if entity is None:
            entity = Entity(len(entities), pid, nick or "Player %d" % pid, False, False, None, None, None, time)
            entities.append(entity)
            current[pid] = entity
        return entity

    raw_markers = []
    max_time = 0.0
    round_end = None
    for record in records:
        kind = record.get("t")
        time = float(record.get("time", 0.0))
        max_time = max(max_time, time)
        if kind == "player":
            entity = Entity(len(entities), record["id"], record.get("nick") or "Player %d" % record["id"],
                            bool(record.get("isBot")), bool(record.get("managedBot")),
                            record.get("roleId"), record.get("role"), record.get("team"), time)
            entities.append(entity)
            current[record["id"]] = entity
        elif kind == "s":
            for item in record.get("p", []):
                pid, x, y, z, role_id = item
                entity = ensure(pid, time)
                entity.samples.append((time, x, y, z, role_id))
        elif kind == "role":
            entity = ensure(record["id"], time)
            role_id = record.get("roleId")
            entry = {"t": time, "role": record.get("role", "?"), "roleId": role_id,
                     "team": record.get("team", ""), "reason": record.get("reason", "")}
            if entity.roles and entity.roles[-1]["roleId"] == role_id and entity.roles[-1]["t"] >= time - 0.5:
                entity.roles[-1]["reason"] = entry["reason"]
            else:
                entity.roles.append(entry)
            reason = entry["reason"]
            if reason in ("Revived", "Escaped"):
                raw_markers.append({"kind": "revive" if reason == "Revived" else "escape", "t": time,
                                    "entity": entity, "pos": record.get("pos"), "role": entry["role"]})
        elif kind == "death":
            entity = ensure(record["id"], time)
            raw_markers.append({"kind": "death", "t": time, "entity": entity, "pos": record.get("pos"),
                                "role": record.get("role", "?"), "attacker": record.get("attackerId", -1),
                                "cause": record.get("cause", "")})
        elif kind == "left":
            entity = current.pop(record["id"], None)
            if entity is not None:
                entity.left = time
        elif kind == "round_end":
            round_end = time

    for entity in entities:
        entity.samples.sort(key=lambda s: s[0])

    def nick_at(pid, time):
        best = None
        for entity in entities:
            if entity.pid != pid:
                continue
            first = entity.roles[0]["t"] if entity.roles else (entity.samples[0][0] if entity.samples else 0.0)
            if first <= time + 1.0 and (entity.left is None or entity.left >= time - 1.0):
                best = entity
        return best.nick if best else ("Player %d" % pid if pid >= 0 else None)

    def nearest_sample(entity, time):
        best = None
        for sample in entity.samples:
            if best is None or abs(sample[0] - time) < abs(best[0] - time):
                best = sample
        return best

    # Zones: rooms define them; samples/markers fall back to the single "Map" zone without a header.
    zone_names = [z for z in ZONE_ORDER if any(r.zone == z for r in rooms)]
    if not rooms:
        zone_names = ["Map"]
        ZONE_LABELS["Map"] = "Map"

    zones = {}

    def zone_entry(name):
        entry = zones.get(name)
        if entry is None:
            entry = {"name": name, "label": ZONE_LABELS.get(name, name), "rooms": [], "bounds": None, "navTris": 0,
                     "samples": 0}
            zones[name] = entry
        return entry

    def grow(entry, x0, z0, x1, z1):
        b = entry["bounds"]
        if b is None:
            entry["bounds"] = [x0, z0, x1, z1]
        else:
            b[0], b[1], b[2], b[3] = min(b[0], x0), min(b[1], z0), max(b[2], x1), max(b[3], z1)

    for room in rooms:
        entry = zone_entry(room.zone)
        entry["rooms"].append({"n": room.name, "x0": round(room.cx - room.sx / 2, 1), "z0": round(room.cz - room.sz / 2, 1),
                               "x1": round(room.cx + room.sx / 2, 1), "z1": round(room.cz + room.sz / 2, 1),
                               "shape": room.shape, "ry": round(room.rot, 1)})
        grow(entry, room.cx - room.sx / 2, room.cz - room.sz / 2, room.cx + room.sx / 2, room.cz + room.sz / 2)

    def locate(x, y, z, cell=ZONE_CELL):
        return "Map" if not rooms else locator.locate(x, y, z, cell)

    # Navmesh triangles -> one SVG path per zone (x, -z so that +z points up on screen).
    nav_paths = {}
    for nav in navs:
        scale = float(nav.get("scale", 10))
        flat = nav.get("v") or []
        indices = nav.get("i") or []
        verts = [(flat[i] / scale, flat[i + 1] / scale, flat[i + 2] / scale) for i in range(0, len(flat) - 2, 3)]
        for t in range(0, len(indices) - 2, 3):
            a, b, c = verts[indices[t]], verts[indices[t + 1]], verts[indices[t + 2]]
            cx, cy, cz = (a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3, (a[2] + b[2] + c[2]) / 3
            zone = locate(cx, cy, cz, NAV_CELL)
            entry = zone_entry(zone)
            entry["navTris"] += 1
            parts = nav_paths.setdefault(zone, [])
            parts.append("M%.1f %.1fL%.1f %.1fL%.1f %.1fZ" % (a[0], -a[2], b[0], -b[2], c[0], -c[2]))
            for v in (a, b, c):
                grow(entry, v[0], v[2], v[0], v[2])

    # Samples -> zone per point.
    sample_zone_index = {}
    for entity in entities:
        for sample in entity.samples:
            zone = locate(sample[1], sample[2], sample[3])
            entry = zone_entry(zone)
            entry["samples"] += 1
            grow(entry, sample[1], sample[3], sample[1], sample[3])
            sample_zone_index[(entity.index, sample[0], sample[1], sample[3])] = zone

    # Markers.
    markers = []
    for marker in raw_markers:
        entity = marker["entity"]
        pos = marker["pos"]
        if not pos:
            near = nearest_sample(entity, marker["t"])
            if near is None:
                continue
            pos = [near[1], near[2], near[3]]
        zone = locate(pos[0], pos[1], pos[2])
        entry = zone_entry(zone)
        grow(entry, pos[0], pos[2], pos[0], pos[2])
        if marker["kind"] == "death":
            killer = nick_at(marker["attacker"], marker["t"]) if marker["attacker"] is not None and marker["attacker"] >= 0 else None
            text = "%s died as %s" % (entity.nick, marker["role"])
            if killer and marker["attacker"] != entity.pid:
                text += ", killed by %s" % killer
            elif marker["attacker"] == entity.pid:
                text += " (suicide)"
            if marker["cause"]:
                text += " [%s]" % marker["cause"]
        elif marker["kind"] == "revive":
            text = "%s revived as %s" % (entity.nick, marker["role"])
        else:
            text = "%s escaped as %s" % (entity.nick, marker["role"])
        markers.append({"kind": marker["kind"], "t": marker["t"], "p": entity.index, "z": zone,
                        "x": round(pos[0], 1), "zz": round(pos[2], 1), "text": text})
    markers.sort(key=lambda m: m["t"])

    # Final zone list (skip empty zones), padded bounds.
    ordered = [n for n in ZONE_ORDER + ["Map"] if n in zones]
    zone_list = []
    zone_indices = {}
    for name in ordered:
        entry = zones[name]
        if entry["bounds"] is None:
            continue
        if not entry["rooms"] and not entry["navTris"] and not entry["samples"]:
            continue
        x0, z0, x1, z1 = entry["bounds"]
        if x1 - x0 < 20:
            mid = (x0 + x1) / 2
            x0, x1 = mid - 10, mid + 10
        if z1 - z0 < 20:
            mid = (z0 + z1) / 2
            z0, z1 = mid - 10, mid + 10
        entry["bounds"] = [round(x0 - ZONE_PAD_METERS, 1), round(z0 - ZONE_PAD_METERS, 1),
                           round(x1 + ZONE_PAD_METERS, 1), round(z1 + ZONE_PAD_METERS, 1)]
        entry["nav"] = "".join(nav_paths.get(name, [])) or None
        zone_indices[name] = len(zone_list)
        zone_list.append(entry)

    # Players -> compact arrays.
    colors = {}
    players = []
    for entity in entities:
        times, zone_idx, xs, ys, zs, rs = [], [], [], [], [], []
        for sample in entity.samples:
            zone = sample_zone_index[(entity.index, sample[0], sample[1], sample[3])]
            times.append(round(sample[0], 2))
            zone_idx.append(zone_indices.get(zone, 0))
            xs.append(round(sample[1], 1))
            ys.append(round(sample[2], 1))
            zs.append(round(sample[3], 1))
            rs.append(sample[4])
            if sample[4] not in colors:
                name, team = role_info(sample[4])
                colors[sample[4]] = role_color(name, team)
        for role in entity.roles:
            if role["roleId"] is not None and role["roleId"] not in colors:
                colors[role["roleId"]] = role_color(role["role"], role["team"])
        players.append({"id": entity.pid, "nick": entity.nick, "bot": entity.is_bot, "managed": entity.managed,
                        "roles": entity.roles, "left": entity.left,
                        "T": times, "Z": zone_idx, "X": xs, "Y": ys, "S": zs, "R": rs})

    for marker in markers:
        marker["z"] = zone_indices.get(marker["z"], 0)

    first_sample = min((p["T"][0] for p in players if p["T"]), default=0.0)
    last_sample = max((p["T"][-1] for p in players if p["T"]), default=0.0)
    duration = max(max_time, last_sample, round_end or 0.0)
    role_names = {str(k): v[0] for k, v in roles_table.items()}
    for entity in entities:
        for role in entity.roles:
            if role["roleId"] is not None:
                role_names.setdefault(str(role["roleId"]), role["role"])
    for sample_role in colors:
        role_names.setdefault(str(sample_role), str(sample_role))

    return {
        "meta": {"seed": header.get("seed"), "port": header.get("port"), "utc": header.get("utc"),
                 "interval": interval, "duration": round(duration, 2), "start": round(first_sample, 2),
                 "roundEnd": round_end, "source": os.path.basename(path), "hasNav": bool(navs),
                 "reason": header.get("reason")},
        "roleNames": role_names,
        "colors": {str(k): v for k, v in colors.items()},
        "zones": zone_list,
        "players": players,
        "markers": markers,
    }


def render(data):
    payload = json.dumps(data, separators=(",", ":"), ensure_ascii=True).replace("</", "<\\/")
    title = "Round map"
    meta = data["meta"]
    if meta.get("seed") is not None:
        title = "Round map - seed %s" % meta["seed"]
    return HTML_TEMPLATE.replace("__TITLE__", html.escape(title)).replace("__DATA__", payload)


HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>__TITLE__</title>
<style>
:root { --bg:#0e1116; --panel:#151a22; --edge:#222b38; --text:#d7dde6; --dim:#8693a5; --accent:#5aa9ff; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:13px/1.4 "Segoe UI", system-ui, sans-serif; }
header { padding:10px 16px 4px; display:flex; gap:16px; align-items:baseline; flex-wrap:wrap; }
header h1 { margin:0; font-size:16px; font-weight:600; }
header .meta { color:var(--dim); }
#controls { position:sticky; top:0; z-index:5; background:rgba(14,17,22,.95); border-bottom:1px solid var(--edge);
  padding:8px 16px; display:flex; gap:14px; align-items:center; flex-wrap:wrap; }
#controls button, #controls select, #controls input[type=number], .tools button { background:var(--panel); color:var(--text);
  border:1px solid var(--edge); border-radius:4px; padding:3px 8px; font:inherit; }
#controls button { cursor:pointer; min-width:64px; }
#slider { flex:1 1 320px; min-width:200px; accent-color:var(--accent); }
#clock { font-variant-numeric:tabular-nums; min-width:92px; }
label.check { display:inline-flex; gap:5px; align-items:center; color:var(--dim); cursor:pointer; }
main { display:flex; gap:14px; padding:14px 16px; align-items:flex-start; }
#panels { flex:1 1 auto; display:grid; grid-template-columns:repeat(auto-fit, minmax(460px, 1fr)); gap:14px; min-width:0; }
.panel { background:var(--panel); border:1px solid var(--edge); border-radius:6px; padding:8px; min-width:0; }
.panel h2 { margin:0 0 6px; font-size:13px; font-weight:600; color:var(--dim); text-transform:uppercase; letter-spacing:.06em;
  display:flex; justify-content:space-between; }
.panel h2 span { text-transform:none; letter-spacing:0; font-weight:400; }
.panel svg { width:100%; display:block; background:#0b0e13; border-radius:4px; cursor:grab; max-height:82vh; }
.panel svg.drag { cursor:grabbing; }
aside { flex:0 0 330px; max-width:330px; position:sticky; top:62px; max-height:calc(100vh - 76px); overflow:auto;
  background:var(--panel); border:1px solid var(--edge); border-radius:6px; padding:10px; }
aside h3 { margin:10px 0 6px; font-size:12px; text-transform:uppercase; letter-spacing:.06em; color:var(--dim); }
aside h3:first-child { margin-top:0; }
.tools { display:flex; gap:6px; flex-wrap:wrap; margin-bottom:6px; }
.tools button { cursor:pointer; padding:2px 8px; }
.row { display:flex; gap:6px; align-items:flex-start; padding:3px 4px; border-radius:4px; cursor:pointer; }
.row:hover { background:#1c2330; }
.row.focus { background:#1f2b40; }
.row .sw { width:10px; height:10px; border-radius:50%; margin-top:4px; flex:none; }
.row .name { font-weight:600; }
.row .tag { font-size:10px; padding:0 4px; border-radius:3px; background:#263042; color:var(--dim); margin-left:4px; }
.row .hist { color:var(--dim); font-size:11px; }
.row.off { opacity:.4; }
.team { display:flex; flex-wrap:wrap; gap:4px 12px; color:var(--dim); font-size:12px; }
.team i { display:inline-block; width:9px; height:9px; border-radius:50%; margin-right:4px; }
.ev { padding:2px 4px; cursor:pointer; border-radius:4px; font-size:12px; }
.ev:hover { background:#1c2330; }
.ev b { font-variant-numeric:tabular-nums; color:var(--dim); margin-right:6px; font-weight:500; }
#tip { position:fixed; pointer-events:none; z-index:20; background:#0b0e13ee; border:1px solid #33425a; border-radius:4px;
  padding:4px 8px; font-size:12px; display:none; max-width:360px; white-space:pre-line; }
.empty { color:var(--dim); padding:30px; text-align:center; }
</style>
</head>
<body>
<header><h1>__TITLE__</h1><span class="meta" id="meta"></span></header>
<div id="controls">
  <button id="play">Play</button>
  <input id="slider" type="range" min="0" max="1" step="0.1" value="1">
  <span id="clock"></span>
  <label class="check">Speed <select id="speed"><option value="1">1x</option><option value="5">5x</option>
    <option value="15" selected>15x</option><option value="30">30x</option><option value="60">60x</option><option value="120">120x</option></select></label>
  <label class="check">Trail (s) <input id="trailLen" type="number" min="5" max="3600" step="5" value="60" style="width:64px"></label>
  <label class="check"><input id="fullTrails" type="checkbox" checked> Full trails</label>
  <label class="check"><input id="roomLabels" type="checkbox" checked> Room labels</label>
  <label class="check"><input id="names" type="checkbox" checked> Names</label>
</div>
<main>
  <div id="panels"></div>
  <aside>
    <h3>Teams</h3><div class="team" id="teamKey"></div>
    <h3>Players</h3>
    <div class="tools"><button id="showAll">All</button><button id="showHumans">Humans</button><button id="showBots">Bots</button></div>
    <div id="legend"></div>
    <h3>Events</h3><div id="events"></div>
  </aside>
</main>
<div id="tip"></div>
<script type="application/json" id="data">__DATA__</script>
<script>
(function () {
"use strict";
var D = JSON.parse(document.getElementById("data").textContent);
var NS = "http://www.w3.org/2000/svg";
var FALLBACK = "#cbd5e1";
var TEAM_KEY = [["SCPs", "#ef4444"], ["Class-D", "#fb923c"], ["Scientists", "#facc15"], ["Facility guard", "#94a3b8"],
  ["NTF", "#3b82f6"], ["Chaos", "#22c55e"], ["Tutorial", "#f472b6"], ["Other", "#cbd5e1"]];
var interval = D.meta.interval || 1;
var gapLimit = Math.max(2.5 * interval, 2.5);
var tMin = 0, tMax = Math.max(D.meta.duration, 1);
var state = { t: tMax, playing: false, full: true, trail: 60, labels: true, names: true, focus: null, hidden: {} };
var tipEl = document.getElementById("tip");

function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return {"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]; }); }
function fmt(t) { t = Math.max(0, Math.round(t)); var m = Math.floor(t / 60), s = t % 60; return (m < 10 ? "0" : "") + m + ":" + (s < 10 ? "0" : "") + s; }
function color(roleId) { return D.colors[String(roleId)] || FALLBACK; }
function roleName(roleId) { return D.roleNames[String(roleId)] || String(roleId); }
function visible(i) {
  if (state.hidden[i]) return false;
  return state.focus === null || state.focus === i;
}

// ---- Segments: contiguous runs of one player in one zone with one team color ----
D.players.forEach(function (p, index) {
  p.index = index;
  p.segs = [];
  var n = p.T.length, start = 0;
  for (var i = 1; i <= n; i++) {
    var split = i === n || p.T[i] - p.T[i - 1] > gapLimit || p.Z[i] !== p.Z[i - 1] || color(p.R[i]) !== color(p.R[i - 1]);
    if (split) {
      if (i > start) p.segs.push({ a: start, b: i - 1, z: p.Z[start], c: color(p.R[start]) });
      start = i;
    }
  }
  p.color = p.T.length ? color(p.R[0]) : (p.roles.length ? color(p.roles[0].roleId) : FALLBACK);
});

function lastLE(p, t) { // index of last sample with T <= t, or -1
  var lo = 0, hi = p.T.length - 1, ans = -1;
  while (lo <= hi) { var mid = (lo + hi) >> 1; if (p.T[mid] <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
  return ans;
}

// Position of player p at time t: {z, x, s, y, r} or null while absent (dead, not yet spawned, long gap).
function posAt(p, t) {
  var i = lastLE(p, t);
  if (i < 0) return null;
  var j = i + 1;
  if (j < p.T.length && p.T[j] - p.T[i] <= gapLimit) {
    if (p.Z[j] === p.Z[i]) {
      var f = (t - p.T[i]) / (p.T[j] - p.T[i]);
      return { z: p.Z[i], x: p.X[i] + (p.X[j] - p.X[i]) * f, s: p.S[i] + (p.S[j] - p.S[i]) * f, y: p.Y[i], r: p.R[i], i: i };
    }
    return { z: p.Z[i], x: p.X[i], s: p.S[i], y: p.Y[i], r: p.R[i], i: i };
  }
  if (t - p.T[i] <= Math.max(interval, 1) * 1.05) return { z: p.Z[i], x: p.X[i], s: p.S[i], y: p.Y[i], r: p.R[i], i: i };
  return null;
}

// ---- Panels ----
var panelsEl = document.getElementById("panels");
var panels = [];
if (!D.zones.length) { panelsEl.innerHTML = '<div class="empty">The trace has no rooms, navmesh or samples.</div>'; }
D.zones.forEach(function (zone, zi) {
  var b = zone.bounds, w = b[2] - b[0], h = b[3] - b[1];
  var section = document.createElement("section");
  section.className = "panel";
  var info = zone.rooms.length + " rooms" + (zone.navTris ? ", " + zone.navTris + " navmesh triangles" : "");
  section.innerHTML = '<h2>' + esc(zone.label) + '<span>' + info + '</span></h2>';
  var svg = document.createElementNS(NS, "svg");
  var panel = { zone: zone, index: zi, svg: svg, vb: { x: b[0], y: -b[3], w: w, h: h }, home: { x: b[0], y: -b[3], w: w, h: h }, scale: 1 };
  svg.style.aspectRatio = w + " / " + h;
  svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
  svg.innerHTML = (zone.nav ? '<path d="' + zone.nav + '" fill="#2b3444" stroke="#2b3444" stroke-width="0.8" vector-effect="non-scaling-stroke"/>' : "")
    + '<g class="rooms"></g><g class="full"></g><g class="win"></g><g class="marks"></g><g class="dots"></g>';
  panel.g = {};
  ["rooms", "full", "win", "marks", "dots"].forEach(function (k) { panel.g[k] = svg.querySelector("." + k); });
  section.appendChild(svg);
  panelsEl.appendChild(section);
  panels.push(panel);
  attachInteraction(panel);
  setViewBox(panel);
});

function setViewBox(panel) {
  var v = panel.vb;
  panel.svg.setAttribute("viewBox", v.x + " " + v.y + " " + v.w + " " + v.h);
}
function measure(panel) {
  var r = panel.svg.getBoundingClientRect();
  panel.scale = Math.max(0.0001, Math.min(r.width / panel.vb.w, r.height / panel.vb.h));
}

// ---- Static layers ----
function drawRooms(panel) {
  var k = 1 / panel.scale, out = [];
  panel.zone.rooms.forEach(function (r) {
    var w = r.x1 - r.x0, h = r.z1 - r.z0;
    out.push('<rect x="' + r.x0 + '" y="' + (-r.z1) + '" width="' + w + '" height="' + h + '" fill="' + (panel.zone.nav ? "none" : "#1b2330")
      + '" stroke="#2a3547" stroke-width="1" vector-effect="non-scaling-stroke"/>');
    if (state.labels && Math.min(w, h) * panel.scale > 26) {
      out.push('<text x="' + ((r.x0 + r.x1) / 2) + '" y="' + (-(r.z0 + r.z1) / 2) + '" fill="#6f7f95" fill-opacity="0.55" font-size="' + (10 * k)
        + '" text-anchor="middle" dominant-baseline="middle" pointer-events="none">' + esc(r.n) + '</text>');
    }
  });
  panel.g.rooms.innerHTML = out.join("");
}

var pointsCache = {};
function segPoints(p, seg) {
  var key = p.index + ":" + seg.a;
  var s = pointsCache[key];
  if (!s) {
    var parts = [];
    for (var i = seg.a; i <= seg.b; i++) parts.push(p.X[i] + "," + (-p.S[i]));
    s = pointsCache[key] = parts.join(" ");
  }
  return s;
}

function drawFull() {
  panels.forEach(function (panel) {
    var out = [];
    D.players.forEach(function (p) {
      if (!visible(p.index)) return;
      p.segs.forEach(function (seg) {
        if (seg.z !== panel.index) return;
        if (seg.a === seg.b) {
          out.push('<circle cx="' + p.X[seg.a] + '" cy="' + (-p.S[seg.a]) + '" r="' + (1.5 / panel.scale) + '" fill="' + seg.c + '" fill-opacity="0.5"/>');
        } else {
          out.push('<polyline points="' + segPoints(p, seg) + '" fill="none" stroke="' + seg.c
            + '" stroke-width="1.6" stroke-opacity="0.5" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>');
        }
      });
    });
    panel.g.full.innerHTML = out.join("");
    panel.g.full.style.display = state.full ? "" : "none";
  });
}

// ---- Dynamic layers ----
var WIN_CHUNKS = 4;
function drawDynamic() {
  var t = state.t, full = state.full;
  panels.forEach(function (panel) {
    var k = 1 / panel.scale, win = [], marks = [], dots = [];
    D.players.forEach(function (p) {
      if (!visible(p.index) || !p.T.length) return;
      // Fading trail of the last state.trail seconds.
      var from = t - state.trail;
      p.segs.forEach(function (seg) {
        if (seg.z !== panel.index || p.T[seg.b] < from || p.T[seg.a] > t) return;
        var pts = [];
        for (var i = seg.a; i <= seg.b; i++) {
          if (p.T[i] < from) continue;
          if (p.T[i] > t) break;
          pts.push([p.X[i], -p.S[i], p.T[i]]);
        }
        var now = posAt(p, t);
        if (now && now.z === panel.index && (!pts.length || pts[pts.length - 1][2] < t) && p.T[seg.a] <= t && t <= p.T[seg.b])
          pts.push([now.x, -now.s, t]);
        if (pts.length < 2) return;
        for (var c = 0; c < WIN_CHUNKS; c++) {
          var lo = Math.floor(c * (pts.length - 1) / WIN_CHUNKS), hi = Math.ceil((c + 1) * (pts.length - 1) / WIN_CHUNKS);
          if (hi <= lo) continue;
          var s = [];
          for (var q = lo; q <= hi; q++) s.push(pts[q][0] + "," + pts[q][1]);
          win.push('<polyline points="' + s.join(" ") + '" fill="none" stroke="' + seg.c + '" stroke-width="2.4" stroke-opacity="'
            + (0.25 + 0.7 * (c + 1) / WIN_CHUNKS).toFixed(2) + '" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>');
        }
      });
      // Start and final markers.
      var first = 0, last = p.T.length - 1;
      if (p.Z[first] === panel.index && (full || t >= p.T[first]))
        marks.push('<circle cx="' + p.X[first] + '" cy="' + (-p.S[first]) + '" r="' + (4.5 * k) + '" fill="none" stroke="' + color(p.R[first]) + '" stroke-width="1.6" vector-effect="non-scaling-stroke"/>');
      if (p.Z[last] === panel.index && (full || t >= p.T[last])) {
        var fx = p.X[last], fy = -p.S[last], r = 3.6 * k;
        marks.push('<rect x="' + (fx - r) + '" y="' + (fy - r) + '" width="' + (2 * r) + '" height="' + (2 * r) + '" fill="' + color(p.R[last]) + '" stroke="#0b0e13" stroke-width="1" vector-effect="non-scaling-stroke"/>');
      }
      // Current dot.
      var pos = posAt(p, t);
      if (pos && pos.z === panel.index) {
        var cx = pos.x, cy = -pos.s, rr = 5.5 * k;
        dots.push('<circle cx="' + cx + '" cy="' + cy + '" r="' + rr + '" fill="' + color(pos.r) + '" stroke="#fff" stroke-width="1.4" vector-effect="non-scaling-stroke"/>');
        if (state.names)
          dots.push('<text x="' + (cx + 8 * k) + '" y="' + (cy - 7 * k) + '" font-size="' + (11 * k) + '" fill="#e6ebf2" stroke="#0b0e13" stroke-width="' + (3 * k) + '" stroke-linejoin="round" paint-order="stroke" pointer-events="none">' + esc(p.nick) + '</text>');
      }
    });
    D.markers.forEach(function (m) {
      if (m.z !== panel.index || !visible(m.p) || !(full || t >= m.t)) return;
      var r = 6 * k, x = m.x, y = -m.zz;
      if (m.kind === "death") {
        marks.push('<path d="M' + (x - r) + ' ' + (y - r) + 'L' + (x + r) + ' ' + (y + r) + 'M' + (x - r) + ' ' + (y + r) + 'L' + (x + r) + ' ' + (y - r)
          + '" stroke="#ff4d4d" stroke-width="3" stroke-linecap="round" vector-effect="non-scaling-stroke"/>');
      } else if (m.kind === "revive") {
        marks.push('<circle cx="' + x + '" cy="' + y + '" r="' + r + '" fill="#0b0e13" stroke="#4ade80" stroke-width="2" vector-effect="non-scaling-stroke"/>'
          + '<path d="M' + (x - r * .55) + ' ' + y + 'H' + (x + r * .55) + 'M' + x + ' ' + (y - r * .55) + 'V' + (y + r * .55) + '" stroke="#4ade80" stroke-width="2" vector-effect="non-scaling-stroke"/>');
      } else {
        marks.push('<path d="M' + x + ' ' + (y - r * 1.2) + 'L' + (x + r * 1.2) + ' ' + y + 'L' + x + ' ' + (y + r * 1.2) + 'L' + (x - r * 1.2) + ' ' + y
          + 'Z" fill="#22d3ee" fill-opacity="0.35" stroke="#22d3ee" stroke-width="2" vector-effect="non-scaling-stroke"/>');
      }
    });
    panel.g.win.innerHTML = win.join("");
    panel.g.marks.innerHTML = marks.join("");
    panel.g.dots.innerHTML = dots.join("");
  });
}

function redrawAll() {
  panels.forEach(function (panel) { measure(panel); drawRooms(panel); });
  drawFull();
  drawDynamic();
}

// ---- Tooltips, zoom and pan ----
function toWorld(panel, ev) {
  var pt = panel.svg.createSVGPoint();
  pt.x = ev.clientX; pt.y = ev.clientY;
  var m = panel.svg.getScreenCTM();
  if (!m) return null;
  var w = pt.matrixTransform(m.inverse());
  return { x: w.x, s: -w.y };
}

function describeSample(p, i) {
  return p.nick + " (" + (p.bot ? "bot" : "human") + ", id " + p.id + ")\n" + roleName(p.R[i]) + " at " + fmt(p.T[i])
    + "\n" + p.X[i].toFixed(1) + ", " + p.Y[i].toFixed(1) + ", " + p.S[i].toFixed(1);
}

function showTip(ev, panel) {
  var w = toWorld(panel, ev);
  if (!w) return;
  var radius = 12 / panel.scale, best = null, bestD = radius * radius;
  var from = state.t - state.trail;
  D.players.forEach(function (p) {
    if (!visible(p.index)) return;
    for (var i = 0; i < p.T.length; i++) {
      if (p.Z[i] !== panel.index) continue;
      if (!state.full && (p.T[i] < from || p.T[i] > state.t)) continue;
      var dx = p.X[i] - w.x, dz = p.S[i] - w.s, d = dx * dx + dz * dz;
      if (d < bestD) { bestD = d; best = describeSample(p, i); }
    }
    var pos = posAt(p, state.t);
    if (pos && pos.z === panel.index) {
      var ddx = pos.x - w.x, ddz = pos.s - w.s, dd = ddx * ddx + ddz * ddz;
      if (dd < bestD) { bestD = dd; best = p.nick + " (" + (p.bot ? "bot" : "human") + ", id " + p.id + ")\n" + roleName(pos.r) + " now, " + fmt(state.t); }
    }
  });
  D.markers.forEach(function (m) {
    if (m.z !== panel.index || !visible(m.p) || !(state.full || state.t >= m.t)) return;
    var dx = m.x - w.x, dz = m.zz - w.s, d = dx * dx + dz * dz;
    if (d < bestD * 1.5) { bestD = d; best = fmt(m.t) + "  " + m.text; }
  });
  if (!best) { tipEl.style.display = "none"; return; }
  tipEl.textContent = best;
  tipEl.style.display = "block";
  tipEl.style.left = Math.min(ev.clientX + 14, window.innerWidth - 380) + "px";
  tipEl.style.top = (ev.clientY + 14) + "px";
}

function attachInteraction(panel) {
  var svg = panel.svg, drag = null, pending = false, lastEv = null;
  svg.addEventListener("mousemove", function (ev) {
    if (drag) {
      var dx = (ev.clientX - drag.x) / panel.scale, dy = (ev.clientY - drag.y) / panel.scale;
      panel.vb.x = drag.vx - dx; panel.vb.y = drag.vy - dy;
      setViewBox(panel);
      tipEl.style.display = "none";
      return;
    }
    lastEv = ev;
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () { pending = false; showTip(lastEv, panel); });
  });
  svg.addEventListener("mouseleave", function () { tipEl.style.display = "none"; drag = null; svg.classList.remove("drag"); });
  svg.addEventListener("mousedown", function (ev) {
    drag = { x: ev.clientX, y: ev.clientY, vx: panel.vb.x, vy: panel.vb.y };
    svg.classList.add("drag");
  });
  window.addEventListener("mouseup", function () { if (drag) { drag = null; svg.classList.remove("drag"); } });
  svg.addEventListener("dblclick", function () {
    panel.vb = { x: panel.home.x, y: panel.home.y, w: panel.home.w, h: panel.home.h };
    setViewBox(panel); relayout();
  });
  svg.addEventListener("wheel", function (ev) {
    ev.preventDefault();
    var w = toWorld(panel, ev);
    if (!w) return;
    var f = ev.deltaY < 0 ? 0.8 : 1.25;
    var nw = Math.min(panel.home.w * 1.5, Math.max(8, panel.vb.w * f)), k = nw / panel.vb.w;
    var cy = -w.s;
    panel.vb.x = w.x - (w.x - panel.vb.x) * k;
    panel.vb.y = cy - (cy - panel.vb.y) * k;
    panel.vb.w = nw; panel.vb.h = panel.vb.h * k;
    setViewBox(panel); relayout();
  }, { passive: false });
}

var relayoutTimer = null;
function relayout() {
  clearTimeout(relayoutTimer);
  relayoutTimer = setTimeout(function () { panels.forEach(function (panel) { measure(panel); drawRooms(panel); }); drawDynamic(); }, 30);
}
window.addEventListener("resize", relayout);

// ---- Controls ----
var slider = document.getElementById("slider"), clock = document.getElementById("clock"), playBtn = document.getElementById("play");
slider.min = tMin; slider.max = tMax; slider.step = 0.5; slider.value = state.t;
function setTime(t) {
  state.t = Math.max(tMin, Math.min(tMax, t));
  slider.value = state.t;
  clock.textContent = fmt(state.t) + " / " + fmt(tMax);
  drawDynamic();
}
slider.addEventListener("input", function () { setTime(parseFloat(slider.value)); });
var lastFrame = 0;
function tick(now) {
  if (!state.playing) return;
  var dt = Math.min(0.25, (now - lastFrame) / 1000);
  lastFrame = now;
  var speed = parseFloat(document.getElementById("speed").value);
  var next = state.t + dt * speed;
  if (next >= tMax) { setTime(tMax); setPlaying(false); return; }
  setTime(next);
  requestAnimationFrame(tick);
}
function setPlaying(on) {
  state.playing = on;
  playBtn.textContent = on ? "Pause" : "Play";
  if (on) {
    if (state.t >= tMax - 0.01) setTime(tMin);
    lastFrame = performance.now();
    requestAnimationFrame(tick);
  }
}
playBtn.addEventListener("click", function () { setPlaying(!state.playing); });
document.getElementById("trailLen").addEventListener("input", function (e) { state.trail = Math.max(1, parseFloat(e.target.value) || 60); drawDynamic(); });
document.getElementById("fullTrails").addEventListener("change", function (e) { state.full = e.target.checked; drawFull(); drawDynamic(); });
document.getElementById("roomLabels").addEventListener("change", function (e) { state.labels = e.target.checked; panels.forEach(drawRooms); });
document.getElementById("names").addEventListener("change", function (e) { state.names = e.target.checked; drawDynamic(); });

// ---- Legend and events ----
document.getElementById("meta").textContent = [
  D.meta.seed !== null && D.meta.seed !== undefined ? "seed " + D.meta.seed : null,
  D.meta.port ? "port " + D.meta.port : null,
  D.meta.utc ? D.meta.utc.replace("T", " ").replace(/\..*$/, "") + " UTC" : null,
  D.players.length + " players", fmt(D.meta.duration) + " long", D.meta.hasNav ? "navmesh floor" : "room footprints"
].filter(Boolean).join("  |  ");
document.getElementById("teamKey").innerHTML = TEAM_KEY.map(function (t) { return '<span><i style="background:' + t[1] + '"></i>' + t[0] + '</span>'; }).join("");

var legendEl = document.getElementById("legend");
function drawLegend() {
  legendEl.innerHTML = D.players.map(function (p) {
    var hist = p.roles.map(function (r) { return r.role + (r.reason && r.reason !== "None" ? " (" + r.reason + ")" : "") + " " + fmt(r.t); }).join(" > ");
    var cls = "row" + (visible(p.index) ? "" : " off") + (state.focus === p.index ? " focus" : "");
    return '<div class="' + cls + '" data-i="' + p.index + '"><span class="sw" style="background:' + p.color + '"></span><div><span class="name">' + esc(p.nick)
      + '</span><span class="tag">' + (p.bot ? "bot" : "human") + '</span><span class="tag">#' + p.id + '</span><div class="hist">' + esc(hist) + (p.left !== null && p.left !== undefined ? " > left " + fmt(p.left) : "") + '</div></div></div>';
  }).join("");
}
legendEl.addEventListener("click", function (e) {
  var row = e.target.closest(".row");
  if (!row) return;
  var i = parseInt(row.getAttribute("data-i"), 10);
  state.focus = state.focus === i ? null : i;
  state.hidden = {};
  refreshAll();
});
function setFilter(pred) {
  state.focus = null; state.hidden = {};
  D.players.forEach(function (p) { if (!pred(p)) state.hidden[p.index] = true; });
  refreshAll();
}
document.getElementById("showAll").addEventListener("click", function () { setFilter(function () { return true; }); });
document.getElementById("showHumans").addEventListener("click", function () { setFilter(function (p) { return !p.bot; }); });
document.getElementById("showBots").addEventListener("click", function () { setFilter(function (p) { return p.bot; }); });
function refreshAll() { drawLegend(); drawFull(); drawDynamic(); }

var eventsEl = document.getElementById("events");
eventsEl.innerHTML = D.markers.length ? D.markers.map(function (m, i) {
  var glyph = m.kind === "death" ? "x" : (m.kind === "revive" ? "+" : "&gt;");
  return '<div class="ev" data-t="' + m.t + '"><b>' + fmt(m.t) + '</b>' + glyph + " " + esc(m.text) + '</div>';
}).join("") : '<div class="hist" style="color:#8693a5">No deaths, revives or escapes.</div>';
eventsEl.addEventListener("click", function (e) {
  var el = e.target.closest(".ev");
  if (el) setTime(parseFloat(el.getAttribute("data-t")));
});

drawLegend();
redrawAll();
setTime(state.t);
})();
</script>
</body>
</html>
"""


def main(argv=None):
    parser = argparse.ArgumentParser(description="Render a SCPSLBot round trace as a self-contained HTML map.")
    parser.add_argument("trace", help="path to a round-*.jsonl trace")
    parser.add_argument("-o", "--output", help="output HTML path (default: <trace>.html)")
    args = parser.parse_args(argv)

    output = args.output or os.path.splitext(args.trace)[0] + ".html"
    data = build(args.trace)
    with open(output, "w", encoding="utf-8") as handle:
        handle.write(render(data))
    print("wrote %s (%d zones, %d players, %d markers)" % (output, len(data["zones"]), len(data["players"]), len(data["markers"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
