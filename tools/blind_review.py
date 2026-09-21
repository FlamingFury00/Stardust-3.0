#!/usr/bin/env python3
"""Build a blinded paired-video review and score locked ratings. Python 3.10+.

FFmpeg/ffprobe must be on PATH for build. The operator must first remove visible
identifiers and match camera/cosmetics. Metadata removal cannot hide a nameplate.
Only the public directory goes to reviewers; the answer key stays with the operator.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
from pathlib import Path
import random
import re
import shutil
import subprocess
import tempfile
from typing import Callable

DIMENSIONS = ("coverage", "mechanics", "smoothness")
COLUMNS = ("case_id", "winner", *(f"{d}_{s}" for d in DIMENSIONS for s in "AB"), "notes")


def digest(path: Path) -> str:
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    return checksum.hexdigest()


def command(arguments: list[str], timeout: int = 180) -> str:
    result = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout, check=False)
    if result.returncode:
        raise ValueError(f"{arguments[0]} failed: {result.stderr[-2000:]}")
    return result.stdout


def normalize(source: Path, destination: Path, seconds: float) -> None:
    probe = json.loads(command(["ffprobe", "-v", "error", "-protocol_whitelist", "file,pipe",
        "-select_streams", "v:0", "-show_entries", "stream=duration:format=duration",
        "-of", "json", str(source)], timeout=30))
    streams = probe.get("streams", [])
    if not streams:
        raise ValueError(f"No video stream in {source}")
    duration = float("nan")
    for value in (streams[0].get("duration"), probe.get("format", {}).get("duration")):
        try:
            parsed = float(value)
            if math.isfinite(parsed):
                duration = parsed
                break
        except (TypeError, ValueError):
            continue
    if not math.isfinite(duration) or duration + 0.02 < seconds:
        raise ValueError(f"Video is shorter than requested {seconds}s: {source}")
    command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
        "-protocol_whitelist", "file,pipe", "-i", str(source), "-map", "0:v:0",
        "-t", str(seconds), "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
        "-vf", "setpts=PTS-STARTPTS,scale=1280:720:force_original_aspect_ratio=decrease,"
        "pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=60",
        "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p",
        "-movflags", "+faststart", str(destination)])


def review_html(case_ids: list[str]) -> str:
    sections = []
    for case_id in case_ids:
        videos = "".join(f'<figure><figcaption>{side}</figcaption><video controls muted preload="metadata" '
            f'src="{case_id}_{side}.mp4"></video></figure>' for side in "AB")
        sections.append(f'<section><h2>{case_id}</h2><div class="pair">{videos}</div>'
            '<button type="button" data-action="play">Play both</button> '
            '<button type="button" data-action="pause">Pause both</button> '
            '<button type="button" data-action="reset">Restart both</button></section>')
    return '''<!doctype html><html lang="en"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Paired replay review</title>
<style>body{font:17px system-ui;max-width:1500px;margin:2rem auto;padding:1rem}
.pair{display:flex;gap:1rem}figure{margin:0;flex:1;min-width:0}video{width:100%}
section{margin-bottom:3rem}button{padding:.6rem;margin-top:.8rem}</style>
<h1>Paired replay review</h1><p>Rate coverage, mechanics and smoothness from 1 (poor) to 5 (excellent).
Enter A, B or TIE as the overall winner in ratings.csv. Judge execution and decisions, not just the outcome.
Save all ratings before any identities are revealed.</p>''' + "\n".join(sections) + '''
<script>document.querySelectorAll('button').forEach(button => button.onclick = async () => {
const videos = [...button.closest('section').querySelectorAll('video')];
const action = button.dataset.action;
videos.forEach(video => video.pause());
if(action === 'reset') videos.forEach(video => video.currentTime = 0);
if(action === 'play') {
videos[1].currentTime = videos[0].currentTime;
try { await Promise.all(videos.map(video => video.play())); }
catch(error) { videos.forEach(video => video.pause()); alert('Playback failed. Check the video files.'); }
}});</script></html>'''


def build(manifest: Path, public: Path, key: Path, *, rng: random.Random | None = None,
          encoder: Callable[[Path, Path, float], None] = normalize) -> int:
    manifest, public, key = manifest.resolve(), public.resolve(), key.resolve()
    if public == key or public in key.parents:
        raise ValueError("The answer key must be outside the public review directory")
    if public.exists() or key.exists():
        raise ValueError("Refusing to overwrite an existing review or answer key")
    spec = json.loads(manifest.read_text(encoding="utf-8"))
    if not isinstance(spec, dict):
        raise ValueError("Manifest must be a JSON object")
    if spec.get("blinding_checked") is not True:
        raise ValueError("Set blinding_checked only after checking visible IDs, HUD, camera and cosmetics")
    pairs = spec.get("pairs")
    if not isinstance(pairs, list) or not 1 <= len(pairs) <= 500:
        raise ValueError("Manifest must contain 1..500 pairs")
    prepared = []
    for pair in pairs:
        if not isinstance(pair, dict) or not all(isinstance(pair.get(k), str) and pair[k].strip()
                for k in ("scenario", "candidate", "reference", "reference_kind", "provenance", "candidate_revision")):
            raise ValueError("Each pair needs scenario, candidate, reference, reference_kind, provenance and candidate_revision")
        seconds = float(pair.get("seconds", 10))
        if not math.isfinite(seconds) or not 0.1 <= seconds <= 120:
            raise ValueError("Pair duration must be between 0.1 and 120 seconds")
        paths = [(manifest.parent / pair[k]).resolve() for k in ("candidate", "reference")]
        if any(not path.is_file() or path.stat().st_size == 0 for path in paths):
            raise ValueError(f"Missing or empty source video for {pair['scenario']}")
        if paths[0] == paths[1] or digest(paths[0]) == digest(paths[1]):
            raise ValueError("A comparison cannot use the same clip on both sides")
        prepared.append((pair, paths, seconds))
    randomizer = rng if rng is not None else random.SystemRandom()
    randomizer.shuffle(prepared)
    public.parent.mkdir(parents=True, exist_ok=True)
    key.parent.mkdir(parents=True, exist_ok=True)
    key_created = public_created = False
    try:
        with tempfile.TemporaryDirectory(prefix=".review-", dir=public.parent) as directory:
            stage = Path(directory)
            records = []
            for index, (pair, paths, seconds) in enumerate(prepared, 1):
                case_id = f"case-{index:03}"
                candidate_side = randomizer.choice("AB")
                clips = {}
                for kind, source in zip(("candidate", "reference"), paths):
                    side = candidate_side if kind == "candidate" else ("B" if candidate_side == "A" else "A")
                    filename = f"{case_id}_{side}.mp4"
                    source_hash = digest(source)
                    encoder(source, stage / filename, seconds)
                    if digest(source) != source_hash:
                        raise ValueError(f"Source video changed during encoding: {source}")
                    clips[side] = {"source": str(source), "source_sha256": source_hash,
                                   "public_sha256": digest(stage / filename)}
                records.append({"case_id": case_id, "candidate_side": candidate_side,
                                "manifest_pair": pair, "clips": clips})
            ids = [record["case_id"] for record in records]
            (stage / "index.html").write_text(review_html(ids), encoding="utf-8")
            with (stage / "ratings.csv").open("w", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=COLUMNS)
                writer.writeheader()
                writer.writerows({"case_id": case_id} for case_id in ids)
            descriptor = os.open(key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            key_created = True
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                json.dump({"version": 1, "manifest_sha256": digest(manifest), "cases": records}, stream, indent=2)
            public.mkdir()  # Exclusive creation, never replace another directory.
            public_created = True
            for path in stage.iterdir():
                shutil.move(str(path), public / path.name)
    except Exception:
        if public_created:
            shutil.rmtree(public)
        if key_created:
            key.unlink()
        raise
    return len(prepared)


def score(key: Path, ratings: Path, public: Path | None = None) -> dict:
    key_bytes, rating_bytes = key.read_bytes(), ratings.read_bytes()
    answer = json.loads(key_bytes)
    if not isinstance(answer, dict) or answer.get("version") != 1 or not isinstance(answer.get("cases"), list):
        raise ValueError("Unsupported answer key")
    cases = {case["case_id"]: case for case in answer["cases"]}
    if not 1 <= len(cases) <= 500 or len(cases) != len(answer["cases"]):
        raise ValueError("Empty, oversized or duplicate cases in answer key")
    public = public or ratings.parent
    for case_id, case in cases.items():
        if not re.fullmatch(r"case-[0-9]{3}", case_id):
            raise ValueError("Invalid case ID in answer key")
        for side in "AB":
            if digest(public / f"{case_id}_{side}.mp4") != case["clips"][side]["public_sha256"]:
                raise ValueError(f"Reviewed video changed: {case_id}_{side}")
    wins = losses = ties = 0
    differences = {dimension: [] for dimension in DIMENSIONS}
    seen = set()
    with ratings.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if tuple(reader.fieldnames or ()) != COLUMNS:
            raise ValueError("Ratings CSV columns do not match the generated template")
        for row in reader:
            if None in row or any(row.get(column) is None for column in COLUMNS):
                raise ValueError("Ratings row has missing or extra columns")
            case_id = row["case_id"].strip()
            if case_id in seen or case_id not in cases:
                raise ValueError(f"Duplicate or unknown case: {case_id}")
            seen.add(case_id)
            winner = row["winner"].strip().upper()
            if winner not in ("A", "B", "TIE"):
                raise ValueError(f"Missing or invalid winner for {case_id}")
            candidate = cases[case_id]["candidate_side"]
            if candidate not in ("A", "B"):
                raise ValueError("Invalid candidate side in answer key")
            reference = "B" if candidate == "A" else "A"
            for dimension in DIMENSIONS:
                values = [int(row[f"{dimension}_{side}"]) for side in (candidate, reference)]
                if any(value < 1 or value > 5 for value in values):
                    raise ValueError(f"Scores must be integers 1..5: {case_id}")
                differences[dimension].append(values[0] - values[1])
            ties += winner == "TIE"
            wins += winner == candidate
            losses += winner == reference
    if seen != cases.keys():
        raise ValueError("Ratings are incomplete; do not unblind a partial review")
    if ratings.read_bytes() != rating_bytes:
        raise ValueError("Ratings changed while being scored; save and close them before scoring")
    n = wins + losses
    rate = wins / n if n else None
    interval = None
    p_value = None
    if n:
        z = 1.959963984540054
        center = (rate + z*z/(2*n)) / (1 + z*z/n)
        radius = z * math.sqrt(rate*(1-rate)/n + z*z/(4*n*n)) / (1 + z*z/n)
        interval = [max(0, center-radius), min(1, center+radius)]
        p_value = min(1, 2 * sum(math.comb(n, i) for i in range(min(wins, losses)+1)) / (2**n))
    return {"pairs": len(cases), "candidate_wins": wins, "reference_wins": losses, "ties": ties,
        "decisive_preference_fraction": rate, "wilson_95_decisive": interval,
        "two_sided_sign_test_p": p_value,
        "mean_candidate_minus_reference": {k: sum(v)/len(v) for k, v in differences.items()},
        "ratings_sha256": hashlib.sha256(rating_bytes).hexdigest(),
        "key_sha256": hashlib.sha256(key_bytes).hexdigest(),
        "interpretation": "Preference on these clips, not match win rate or proof of professional strength. "
            "Intervals assume independent paired cases; repeated clips or correlated reviewers invalidate that assumption."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("build")
    create.add_argument("--manifest", type=Path, required=True)
    create.add_argument("--public", type=Path, required=True)
    create.add_argument("--key", type=Path, required=True)
    evaluate = commands.add_parser("score")
    evaluate.add_argument("--key", type=Path, required=True)
    evaluate.add_argument("--ratings", type=Path, required=True)
    evaluate.add_argument("--public", type=Path, help="Video directory; defaults to ratings.csv's directory")
    arguments = parser.parse_args()
    try:
        if arguments.command == "build":
            count = build(arguments.manifest, arguments.public, arguments.key)
            print(f"Created {count} blinded pairs. Share only {arguments.public}; keep the key private.")
        else:
            print(json.dumps(score(arguments.key, arguments.ratings, arguments.public), indent=2, allow_nan=False))
    except (ValueError, OSError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
        parser.exit(2, f"Error: {error}\n")


if __name__ == "__main__":
    main()
