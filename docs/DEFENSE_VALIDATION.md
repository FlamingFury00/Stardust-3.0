# Goal-side defense and mechanics validation

Baseline: `5593b1916f35ebecf6030e1ef3c27aa74b88e549` on `beta`.

## What changed

The open-net report exposed three independent failure layers. The solo race owner
never became an anchor; fixed 3250/4400 defensive depths could be ahead of a deep
ball; and a 350 uu/s guard floor plus generic Drive's 400 uu/s turning floor could
prevent parking. A further transition bug consumed a goal-threat edge during a
physically committed action before the planner could respond.

`Defense.cs` separates ETA ownership from actual goal coverage. A teammate counts
as cover only when grounded, alive, goal-side, and in the ball-to-mouth corridor
both now and after 0.25 seconds of current momentum. This remains a heuristic,
not a proof that the teammate can save every reachable shot. Last-man challenges
need a positive race margin unless an immediate contact is available; uncovered
own-half defenders cannot take boost excursions merely because pressure is not
currently detected.

Targets are derived from the ball and own goal, never from an opponent's location.
An incoming prediction sample can lead the reference by 0.20 seconds. A ball moving
away does not pull the defensive reference upfield. Continuous interpolation and
a smooth post-clearance funnel replace hard depth/side switches. Deep guarding
can occupy the first 80 uu inside the net; it must not be confused with a deep-net
car that needs to exit through the mouth.

`DefensiveDrive` is a persistent arrival action rather than a pass-through drive.
It enters hold within 75 uu and releases outside 140 uu, makes short reverse
corrections, disables defensive powerslides/flips, and permits zero terminal speed.
Its speed envelope is:

```
room = max(0, distance - 75 - max(0, closing_speed) * 0.12)
v_brake = sqrt(2 * Car.BrakeAccel * 0.85 * room)
v_target = min(cruise_speed, v_brake, max(0, distance - 75) * 2.5)
```

The 0.12 s allowance and 0.85 braking factor are empirical margins, not measured
end-to-end latency or certified friction bounds. Physical wheel slip, bumps,
post collisions and orientation changes still require in-game validation.

Predicted inbound goal-plane crossings are interpolated in time and position.
Emergency shot search cannot schedule contact after the crossing deadline, and
its fallback guards the crossing's lateral coordinate. Threat transitions remain
pending until a physically committed action can be interrupted.

The complete regression run also exposed two unrelated baseline mechanics issues.
`FlightAtHorizon` was applying gravity compensation after both ballistic
trajectories already included gravity. It now applies only relative position and
velocity feedback; identical ballistic trajectories request zero control thrust.
This function is for a ballistic ball-contact reference, not a fixed hover point.
The separate fixed-target controller retains gravity feed-forward. Ground dribble
flick timing now takes the earlier of opponent ETA and pre-contact pressure.

## Software checks

All original assertions remain enabled. TeamDefense has 30 tests, including 25
new regressions and a seeded 24,000-state team-symmetry/goal-side geometry sweep.
MechanicsPhysics has seven tests, including the three original assertions and a
1,000-case common-motion invariance check. The Python evaluation tool has 17 tests;
its native FFmpeg test checks the actual transcoded video, audio removal and
metadata removal. Synthetic test videos are not gameplay evidence.

From the repository root, with .NET 8 and Python 3.10+:

```bash
chmod +x src/generate-flatbuffers.sh src/flatbuffers-schema/binaries/flatc
dotnet build src/Bot/Bot.csproj --configuration Release
for suite in Tests ModelChecks Movement Shooting AerialControl JumpEdges ShotJumpEdges TeamDefense BoostGoal MechanicsPhysics; do
  dotnet run --project "tests/Stardust.$suite/Stardust.$suite.csproj" --configuration Release || exit 1
done
python3 -m unittest discover -s tools -p 'test_*.py' -v
```

The CI workflow runs these suites. See the PR's latest workflow result for the
actual execution status; the commands above are not themselves evidence of a pass.

## Match-validation gate

No professional-player comparison, match win rate or save rate is established by
these code changes. Before promoting the branch as a competitive release, collect
matched baseline/candidate trials in Rocket League. Record the commit, game and
RLBot versions, map, mutators, tick rate, packet drops, initial ball/car states,
boost, opponent policy, scenario seed and replay provenance. Predeclare the trial
set and scoring rules; do not replace failures with more flattering clips.

| Scenario family | Required variations | Failure to inspect |
| --- | --- | --- |
| Solo dribble defense | Both teams; central, diagonal and stopped dribbles; delayed flicks | Ahead-of-ball driving, premature commitment, goal-line passivity |
| Deep corners and cutbacks | Near/far post, ball across the mouth, ball behind car | Post impact, lateral target jumps, wrong-side recovery |
| Team coverage | 2v2/3v3; nearest car also last back; teammate airborne/demolished/moving out | False cover, double commit, uncovered boost diversion |
| Emergency transitions | Threat begins during a flip, own/opponent touches, stale prediction | Consumed threat edge, impossible late save, invalid retained shot |
| Arrival and recovery | 0/12/30 boost; full-speed return; shallow/deep net; low tick rate | Orbiting, brake chatter, trapped reverse motion, repeated net exit |
| Possession transitions | Matched ballistic carry, vertical overshoot, contest/uncontested dribble | Upward overboost, missed flick window, unnecessary flick |

Measure goals conceded per opportunity, successful saves, time without a grounded
shooting-corridor defender, commitment before/after opponent contact, boost spent,
post collisions, controller sign changes and decision latency. Separate 1v1,
2v2 and 3v3 results. Report confidence intervals and failures by scenario, not just
an aggregate win rate. Professional footage is a qualitative comparison unless
initial conditions and opposition are genuinely matched.

`STARDUST_TRACE=1` records decision transitions with ETA, rank, pre-contact pressure,
last-back, cover and goal-side flags. It is not a per-frame telemetry recorder or
an automatic match evaluator. Existing ground-control/aerial-carry ablation flags
remain available. Flip resets remain explicitly opt-in; changing that default
without match evidence would hide rather than solve the validation gap.

## Blinded side-by-side review

`tools/blind_review.py` builds a real paired-video review and scores completed
ratings. It does not launch Rocket League, create pro-player footage, judge its
own clips or remove visible nameplates automatically.

Prepare equal-duration clips with aligned scenario start times and comparable
camera/cosmetics. Remove visible player names, identifying HUD and commentary
before setting `blinding_checked: true`. Verify professional-reference provenance
and put the exact candidate commit in the manifest. Copy and edit
`docs/blind-review.example.json`; its false blinding flag deliberately prevents
accidental use of placeholder data.

```bash
python3 tools/blind_review.py build --manifest review-input/manifest.json \
  --public review-public --key review-private/answer.json
# Give reviewers only review-public. Open index.html and complete ratings.csv.
python3 tools/blind_review.py score --key review-private/answer.json \
  --ratings review-public/ratings.csv > review-private/result.json
```

Build requires FFmpeg and ffprobe on PATH. It randomizes case order and A/B side,
normalizes videos to 1280x720 at 60 fps, removes audio and metadata, creates an
HTML paired player, and keeps identities/source hashes outside the public bundle.
The paired player starts both videos together; independent browser decoders do
not guarantee frame-exact synchronization. Use source-frame analysis separately
when sub-frame timing matters. Normalization does not repair mismatched content.

Reviewers choose A/B/TIE and rate coverage, mechanics and smoothness from 1 to 5.
The scorer rejects missing/duplicate cases, malformed rows, out-of-range ratings
and changed review videos. It reports candidate preference, ties, paired score
differences, a Wilson interval for decisive preference and an exact two-sided sign
test. Keep all ratings locked before opening the answer key. Hashes detect changes
relative to the retained key; they do not make an untrusted operator trustworthy.

These statistics describe the selected clips, not match win rate. Repeated clips
or multiple ratings of the same episode are correlated and cannot be counted as
independent trials. Use a separately retained holdout set for the final comparison;
iterating on the same judged examples overfits the evaluation.

## Research and implementation boundary

- RLBot v5 prediction documentation states that future car collisions are omitted.
  This directly motivates pre-contact pressure and independent goal coverage:
  https://wiki.rlbot.org/v5/botmaking/ball-path-prediction/
- Ames et al., *Control Barrier Function Based Quadratic Programs for Safety
  Critical Systems* (2016), separates safety constraints from performance
  objectives. This PR uses explicit heuristic commitment gates, not a CBF/QP
  solver and not the paper's forward-invariance guarantees:
  https://arxiv.org/abs/1609.06408
- *Lucy-SKG* (2023) reports reward design, auxiliary learning tasks and component
  ablations for Rocket League. Its evaluation methodology informs the requirement
  for component-level tests and held-out gameplay; this PR does not implement or
  claim a trained Lucy-SKG policy: https://arxiv.org/abs/2305.15801
- Video normalization follows FFmpeg/ffprobe's stream-selection, metadata and
  output-control interfaces: https://ffmpeg.org/ffmpeg.html and
  https://ffmpeg.org/ffprobe.html
