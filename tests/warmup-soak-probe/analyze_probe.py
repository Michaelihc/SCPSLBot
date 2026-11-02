"""Summarize a WarmupSoakProbe recording: population, combat and zone/elevator movement."""
import argparse
import collections
import json
from pathlib import Path
import statistics
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("probe", type=Path)
parser.add_argument("--stall-seconds", type=float, default=30)
parser.add_argument("--json", action="store_true")
args = parser.parse_args()

records = []
for line in args.probe.read_text(encoding="utf-8").splitlines():
    try:
        records.append(json.loads(line))
    except ValueError:
        pass  # A process exit can truncate the last line.

marks = {r["text"].split()[0]: r["t"] for r in records if r["k"] == "mark"}
client_marks = [(r["t"], r["text"]) for r in records if r["k"] == "mark"]
samples = [r for r in records if r["k"] == "s"]
events = [r for r in records if r["k"] not in ("s", "mark", "start")]
client_ids = {p["id"] for s in samples for p in s["p"] if not p["d"]}
client_id = next(iter(client_ids))
start_play = marks.get("play-start", samples[0]["t"])
arena_t = marks.get("arena-entered", samples[0]["t"])
span = samples[-1]["t"] - samples[0]["t"]
ALIVE_EXCLUDE = {"Spectator", "Destroyed", "None", "Tutorial", "Overwatch"}

lives = collections.OrderedDict()
for sample in samples:
    for p in sample["p"]:
        if p["r"] in ALIVE_EXCLUDE:
            continue
        key = (p["id"], p["l"])
        life = lives.setdefault(key, dict(id=p["id"], life=p["l"], dummy=p["d"], role=p["r"], first=sample["t"], last=sample["t"],
            zones=[], rooms=[], path=0.0, spawn=(p["x"], p["y"], p["w"]), prev=None, stalls=[], anchor=None, anchor_t=sample["t"],
            lifts=set(), max_y=p["y"], min_y=p["y"]))
        life["role"] = p["r"]
        life["last"] = sample["t"]
        life["max_y"] = max(life["max_y"], p["y"])
        life["min_y"] = min(life["min_y"], p["y"])
        if not life["zones"] or life["zones"][-1][1] != p["z"]:
            life["zones"].append((round(sample["t"] - samples[0]["t"], 1), p["z"], p["rm"]))
        if not life["rooms"] or life["rooms"][-1] != p["rm"]:
            life["rooms"].append(p["rm"])
        if p["lift"]:
            life["lifts"].add(p["lift"])
        position = (p["x"], p["y"], p["w"])
        if life["prev"]:
            life["path"] += sum((a - b) ** 2 for a, b in zip(position, life["prev"])) ** 0.5
        life["prev"] = position
        if life["anchor"] is None or sum((a - b) ** 2 for a, b in zip(position, life["anchor"])) ** 0.5 > 1.5:
            held = sample["t"] - life["anchor_t"]
            if held >= args.stall_seconds and life["anchor"]:
                life["stalls"].append((round(life["anchor_t"] - samples[0]["t"], 1), round(held, 1), p["z"], p["rm"]))
            life["anchor"], life["anchor_t"] = position, sample["t"]
for life in lives.values():
    held = life["last"] - life["anchor_t"]
    if held >= args.stall_seconds:
        life["stalls"].append((round(life["anchor_t"] - samples[0]["t"], 1), round(held, 1), life["zones"][-1][1], life["rooms"][-1]))

roles_of = {}
for sample in samples:
    for p in sample["p"]:
        roles_of.setdefault(p["id"], set()).add(p["r"])

shots = [e for e in events if e["k"] == "shot"]
bot_shots = [e for e in shots if e["id"] != client_id]
client_hurt = [e for e in events if e["k"] == "hurt" and e["id"] == client_id]
bot_hits = [e for e in client_hurt if e.get("by") not in (0, client_id)]
client_deaths = [e for e in events if e["k"] == "death" and e["id"] == client_id]
bot_deaths = [e for e in events if e["k"] == "death" and e["id"] != client_id]
intervals = collections.defaultdict(list)
for e in bot_shots:
    intervals[e["id"]].append(e["t"])
gaps = [b - a for t in intervals.values() for a, b in zip(t, t[1:])]
burst_gaps = [g for g in gaps if g < 1.0]
pause_gaps = [g for g in gaps if g >= 1.0]
reloads = [e for e in events if e["k"] == "reloading" and e["id"] != client_id]
reloaded = [e for e in events if e["k"] == "reloaded" and e["id"] != client_id]
dry = [e for e in events if e["k"] == "dry" and e["id"] != client_id]

client_samples = [(s["t"], next((p for p in s["p"] if p["id"] == client_id), None)) for s in samples]
client_on_surface_from = next((t for t, p in client_samples if p and p["r"] not in ALIVE_EXCLUDE), samples[0]["t"])
first_shot = min((e["t"] for e in bot_shots if e["t"] >= client_on_surface_from), default=None)
first_hit = min((e["t"] for e in bot_hits), default=None)

respawn_gaps = []
for death in bot_deaths:
    following = [e["t"] for e in events if e["k"] == "spawn" and e["id"] == death["id"] and e["t"] > death["t"]]
    if following:
        respawn_gaps.append(min(following) - death["t"])
client_respawns = []
for death in client_deaths:
    following = [e["t"] for e in events if e["k"] == "spawn" and e["id"] == client_id and e["t"] > death["t"] and e["r"] not in ALIVE_EXCLUDE]
    if following:
        client_respawns.append(min(following) - death["t"])

bot_lives = [l for l in lives.values() if l["dummy"]]
zone_changes = [(l["id"], l["life"], l["role"], l["zones"]) for l in bot_lives
    if len({z for _, z, _ in l["zones"] if z not in ("None", "Other")}) > 1]
lift_events = [e for e in events if e["k"] == "lift"]
lift_rides = [e for e in events if e["k"] == "liftseq" and any(r != client_id for r in e.get("riders", [])) and e["to"] in ("Moving", "MovingAway", "Arriving", "StartingSequence")]
lift_use = collections.Counter((e["g"], tuple(e.get("riders", []))) for e in lift_rides)
gate_doors = [e for e in events if e["k"] == "door" and e.get("lock", 0)]
pickups = collections.Counter(e.get("item") for e in events if e["k"] == "pickup" and e["id"] != client_id)
used = collections.Counter(e.get("item") for e in events if e["k"] == "used" and e["id"] != client_id)
damage_by = collections.Counter()
for e in bot_hits:
    damage_by[e["by"]] += e.get("dmg", 0)

result = dict(
    seconds=round(span, 1),
    plugins=next((r["plugins"] for r in records if r["k"] == "start"), None),
    bot_roles={str(k): sorted(v - ALIVE_EXCLUDE) for k, v in roles_of.items() if k != client_id},
    bot_lives=len(bot_lives),
    bot_spawns=[(l["id"], l["role"], tuple(round(v, 1) for v in l["spawn"])) for l in bot_lives][:24],
    first_bot_shot_after_client_alive=round(first_shot - client_on_surface_from, 1) if first_shot else None,
    first_hit_on_client_after_client_alive=round(first_hit - client_on_surface_from, 1) if first_hit else None,
    bot_shots=len(bot_shots),
    bot_shots_by_gun=collections.Counter(e.get("gun") for e in bot_shots),
    hits_on_client=len(bot_hits),
    hit_ratio=round(len(bot_hits) / len(bot_shots), 3) if bot_shots else None,
    damage_on_client=round(sum(e.get("dmg", 0) for e in bot_hits), 1),
    damage_by_bot={str(k): round(v, 1) for k, v in damage_by.items()},
    client_deaths=len(client_deaths),
    client_shots=len([e for e in shots if e["id"] == client_id]),
    bot_deaths=len(bot_deaths),
    bot_deaths_by=collections.Counter("client" if e.get("by") == client_id else "bot/other" for e in bot_deaths),
    bot_respawn_seconds=[round(g, 1) for g in respawn_gaps][:20],
    client_respawn_seconds=[round(g, 1) for g in client_respawns][:20],
    burst_interval_median_ms=round(statistics.median(burst_gaps) * 1000) if burst_gaps else None,
    bursts_pause_median_s=round(statistics.median(pause_gaps), 2) if pause_gaps else None,
    bursts=len(pause_gaps) + len(intervals),
    bot_reload_starts=len(reloads),
    bot_reload_done=len(reloaded),
    bot_dry_fires=len(dry),
    bot_pickups=dict(pickups),
    bot_item_uses=dict(used),
    bot_path_m_per_life=[(l["id"], l["role"], round(l["path"]), l["rooms"][:12] if len(l["rooms"]) < 13 else l["rooms"][:6] + ["..."] + l["rooms"][-5:]) for l in bot_lives],
    bot_zone_changes_in_one_life=zone_changes,
    bot_rode_lift=[(l["id"], l["role"], sorted(l["lifts"])) for l in bot_lives if l["lifts"]],
    bot_lift_panel_uses=collections.Counter((e["g"], e["r"]) for e in lift_events if e["id"] != client_id).most_common(),
    lift_moves_with_bot_riders=[(k[0], list(k[1]), v) for k, v in lift_use.items()],
    locked_door_interactions=collections.Counter((e["door"], e["id"], e["r"]) for e in gate_doors if e["id"] != client_id).most_common(),
    stalls=[(l["id"], l["role"], s) for l in bot_lives for s in l["stalls"]],
    errors=[e for e in records if e["k"] == "err"][:5],
)
print(json.dumps(result, indent=1, default=lambda o: dict(o) if hasattr(o, "items") else list(o)))
