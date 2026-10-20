#!/usr/bin/env python3
"""Write a small synthetic round trace for checking render_round_map.py without a game server.

Usage: synthetic_trace.py <out.jsonl> [--no-nav]
"""
import argparse
import json
import math

ROLES = {
    "0": ["Scp173", "SCPs"], "1": ["ClassD", "ClassD"], "4": ["Scp106", "SCPs"], "6": ["Scientist", "Scientists"],
    "11": ["NtfPrivate", "FoundationForces"], "12": ["Tutorial", "OtherAlive"], "15": ["FacilityGuard", "FoundationForces"],
    "17": ["ChaosRifleman", "ChaosInsurgency"], "2": ["Spectator", "Dead"],
}


def room(name, zone, shape, cx, cy, cz, sx, sz):
    return {"name": name, "zone": zone, "shape": shape, "pos": [cx, cy, cz], "rotY": 0.0,
            "bounds": {"center": [cx, cy + 2, cz], "size": [sx, 6.0, sz]}}


def lerp_path(path, t):
    """path: [(time, x, y, z)] -> position at t (clamped)."""
    if t <= path[0][0]:
        return path[0][1:]
    for a, b in zip(path, path[1:]):
        if a[0] <= t <= b[0]:
            f = (t - a[0]) / max(b[0] - a[0], 1e-6)
            return tuple(a[i] + (b[i] - a[i]) * f for i in (1, 2, 3))
    return path[-1][1:]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("--no-nav", action="store_true")
    args = parser.parse_args()

    rooms = []
    for i in range(4):
        for j in range(3):
            rooms.append(room("LczRoom%d%d" % (i, j), "LightContainment", "Straight", i * 15.0, 0.0, j * 15.0, 14.0, 14.0))
    for i in range(3):
        rooms.append(room("HczCorridor%d" % i, "HeavyContainment", "Straight", 30.0 + i * 15.0, -1000.0, -20.0, 14.0, 14.0))
    rooms.append(room("HczCross", "HeavyContainment", "Cross", 30.0, -1000.0, -5.0, 14.0, 14.0))
    rooms.append(room("Outside", "Surface", "Endroom", 0.0, 1000.0, 0.0, 120.0, 80.0))

    lines = [{"t": "header", "version": 1, "utc": "2026-01-01T00:00:00.0000000Z", "reason": "synthetic", "port": 7777,
              "seed": 12345, "mapAge": 12.3, "roundTime": 0.0, "interval": 1.0, "roles": ROLES, "rooms": rooms}]

    if not args.no_nav:
        verts, idx = [], []
        for r in rooms:
            if r["zone"] == "Surface":
                continue
            cx, cy, cz = r["pos"]
            half = r["bounds"]["size"][0] / 2 - 1.0
            base = len(verts) // 3
            for dx, dz in ((-half, -half), (half, -half), (half, half), (-half, half)):
                verts += [round((cx + dx) * 10), round(cy * 10), round((cz + dz) * 10)]
            idx += [base, base + 1, base + 2, base, base + 2, base + 3]
        lines.append({"t": "nav", "time": 1.0, "scale": 10, "v": verts, "i": idx})

    players = {
        1: ("Alice", False, 1, "ClassD", "ClassD",
            [(0, 0, 0, 0), (60, 30, 0, 15), (120, 45, 0, 30), (180, 20, 0, 40)]),
        2: ("SCPSL Round Bot 1", True, 15, "FacilityGuard", "FoundationForces",
            [(0, 15, 0, 15), (90, 30, 0, 0), (150, 0, 0, 30)]),
        3: ("Bob", False, 0, "Scp173", "SCPs",
            [(0, 45, 0, 30), (100, 30, 0, 15), (200, 0, 0, 0)]),
        4: ("SCPSL Round Bot 2", True, 6, "Scientist", "Scientists",
            [(0, 0, 0, 30), (80, 15, 0, 15), (95, 30, -1000, -20), (190, 60, -1000, -20)]),
        5: ("Carol", False, 12, "Tutorial", "OtherAlive",
            [(0, 5, 1000, 5), (200, 40, 1000, -10)]),
    }
    for pid, (nick, bot, role_id, role, team, _) in players.items():
        lines.append({"t": "player", "time": 0.0, "id": pid, "nick": nick, "isBot": bot, "managedBot": bot,
                      "role": role, "roleId": role_id, "team": team})

    death_time = {1: 150.0}
    for step in range(0, 201):
        t = float(step)
        entries = []
        for pid, (_, _, role_id, role, team, path) in players.items():
            if pid in death_time and t >= death_time[pid] and t < 170:
                continue
            if pid == 1 and t >= 170:
                role_id = 17  # revived as Chaos
            x, y, z = lerp_path(path, t)
            if pid == 2 and t >= 120:
                role_id = 11  # promoted for the sake of the test
            entries.append([pid, round(x, 1), round(y, 1), round(z, 1), role_id])
        lines.append({"t": "s", "time": t, "p": entries})
        if t == 150.0:
            lines.append({"t": "death", "time": t, "id": 1, "attackerId": 3, "pos": [round(v, 1) for v in lerp_path(players[1][5], t)],
                          "role": "ClassD", "cause": "Scp173DamageHandler"})
            lines.append({"t": "role", "time": t, "id": 1, "role": "Spectator", "roleId": 2, "team": "Dead", "reason": "Died"})
        if t == 170.0:
            lines.append({"t": "role", "time": t, "id": 1, "role": "ChaosRifleman", "roleId": 17, "team": "ChaosInsurgency",
                          "reason": "Revived", "pos": [round(v, 1) for v in lerp_path(players[1][5], t)]})
        if t == 120.0:
            lines.append({"t": "role", "time": t, "id": 2, "role": "NtfPrivate", "roleId": 11, "team": "FoundationForces", "reason": "Escaped",
                          "pos": [30.0, 0.0, 0.0]})
    lines.append({"t": "round_end", "time": 200.0, "leadingTeam": "Anomalies"})

    with open(args.out, "w", encoding="utf-8") as handle:
        for line in lines:
            handle.write(json.dumps(line, separators=(",", ":")) + "\n")
        handle.write('{"t":"s","time":2')  # a truncated trailing line must be tolerated
    print("wrote %s (%d lines)" % (args.out, len(lines)))


if __name__ == "__main__":
    main()
