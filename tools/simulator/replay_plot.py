#!/usr/bin/env python3
"""Render a window of a simulator replay (replay.json) as top-down and side views.

Usage: replay_plot.py replay.json --start 30 --end 40 [--out window.png] [--events]
Cars are drawn as oriented triangles every --mark seconds; the ball path is black.
"""
import argparse
import json
import math

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Polygon  # noqa: E402

TEAM_COLORS = ["#1f6fd1", "#e8781a"]
PHASES = {1: "countdown", 2: "kickoff", 3: "active", 4: "goal"}


def field_outline():
    corner = 1152
    return [(-4096, -5120 + corner), (-4096, 5120 - corner), (-4096 + corner, 5120), (-893, 5120),
            (-893, 6000), (893, 6000), (893, 5120), (4096 - corner, 5120), (4096, 5120 - corner),
            (4096, -5120 + corner), (4096 - corner, -5120), (893, -5120), (893, -6000), (-893, -6000),
            (-893, -5120), (-4096 + corner, -5120), (-4096, -5120 + corner)]


def load(path):
    with open(path) as f:
        return json.load(f)


def window(replay, start, end):
    return [fr for fr in replay["frames"] if start <= fr[0] - replay["frames"][0][0] <= end]


def draw(replay, start, end, out, mark=0.5, title=None):
    frames = window(replay, start, end)
    if not frames:
        raise SystemExit("no frames in window")
    t0 = replay["frames"][0][0]
    players = replay["players"]
    fig, (top, side) = plt.subplots(1, 2, figsize=(15, 9), gridspec_kw={"width_ratios": [1, 1.1]})

    xs, ys = zip(*field_outline())
    top.plot(xs, ys, color="#555", lw=1)
    top.axhline(0, color="#bbb", lw=0.6)
    top.set_aspect("equal")
    top.set_xlim(-4300, 4300)
    top.set_ylim(-6100, 6100)

    bx = [fr[2][0] for fr in frames]
    by = [fr[2][1] for fr in frames]
    bz = [fr[2][2] for fr in frames]
    ts = [fr[0] - t0 for fr in frames]
    top.plot(bx, by, color="black", lw=1.5, label="ball")
    side.plot(by, bz, color="black", lw=1.5)

    for index, player in enumerate(players):
        color = TEAM_COLORS[player["team"]]
        cx = [fr[4 + index][0][0] for fr in frames]
        cy = [fr[4 + index][0][1] for fr in frames]
        cz = [fr[4 + index][0][2] for fr in frames]
        top.plot(cx, cy, color=color, lw=1.2, label=player["name"])
        side.plot(cy, cz, color=color, lw=1.2)

    next_mark = ts[0]
    for fr, t in zip(frames, ts):
        if t + 1e-6 < next_mark:
            continue
        next_mark = t + mark
        top.plot(fr[2][0], fr[2][1], "o", color="black", ms=4)
        top.annotate(f"{t:.1f}", (fr[2][0], fr[2][1]), fontsize=6, color="#333")
        for index, player in enumerate(players):
            car = fr[4 + index]
            (x, y, _), (fx, fy, _) = car[0], car[1]
            length = math.hypot(fx, fy) or 1
            fx, fy = fx / length * 160, fy / length * 160
            px, py = -fy * 0.45, fx * 0.45
            tri = [(x + fx, y + fy), (x - fx * 0.6 + px, y - fy * 0.6 + py), (x - fx * 0.6 - px, y - fy * 0.6 - py)]
            flags = car[5]
            face = TEAM_COLORS[player["team"]] if flags & 1 else "white"
            top.add_patch(Polygon(tri, closed=True, fc=face, ec=TEAM_COLORS[player["team"]], lw=1))
            if flags & 128:
                top.plot(x - fx * 0.9, y - fy * 0.9, "*", color="#d62728", ms=5)

    side.set_xlim(-6100, 6100)
    side.set_ylim(0, 2100)
    side.axvline(-5120, color="#555", lw=0.8)
    side.axvline(5120, color="#555", lw=0.8)
    side.set_xlabel("y")
    side.set_ylabel("z")
    top.legend(loc="upper left", fontsize=7)
    phase = PHASES.get(frames[0][1], "?")
    fig.suptitle(title or f"{replay['title']}  t={start:.1f}-{end:.1f}s ({phase}); "
                 "filled=grounded, outline=airborne, red star=boost")
    fig.tight_layout()
    fig.savefig(out, dpi=90)
    plt.close(fig)


def events(replay):
    """Print goals and large ball-velocity changes (touches) with times relative to the replay start."""
    frames = replay["frames"]
    t0 = frames[0][0]
    previous = None
    for fr in frames:
        if previous is not None:
            dv = math.dist(fr[3], previous[3])
            if dv > 500:
                print(f"{fr[0] - t0:7.2f}s touch-like dv={dv:5.0f} ball={fr[2]} v={fr[3]}")
            if fr[1] == 4 and previous[1] != 4:
                print(f"{fr[0] - t0:7.2f}s GOAL ball={fr[2]}")
        previous = fr


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("replay")
    parser.add_argument("--start", type=float, default=0)
    parser.add_argument("--end", type=float, default=10)
    parser.add_argument("--mark", type=float, default=0.5)
    parser.add_argument("--out", default="window.png")
    parser.add_argument("--events", action="store_true")
    args = parser.parse_args()
    data = load(args.replay)
    if args.events:
        events(data)
    else:
        draw(data, args.start, args.end, args.out, args.mark)
