# Pressure and possession evaluation, September 2026

This change addresses the passive 2v2 play in the two September 28 telemetry files ending in
`pid1928.jsonl` and `pid9472.jsonl`. The baseline is commit
`8e999576a4a4142c089596592c5febef57b98c26`. It is a measured improvement to that build, not evidence
that Stardust matches professional human players. No professional replay reference was supplied.

## Failures in the supplied match

Both cars recorded 2,171 sampled frames, ending at a 1–6 score. These are sampled-frame counts,
not time-weighted percentages. Car 1 recorded 1,596 defensive/support frames; car 0 recorded 1,293.

- At **16.542 s**, car 1 was 141 units from the ball immediately after its own touch. Its retention
  predicate was true, its ball ETA was 0.05 s against the opponent's 1.06 s, and it was first man.
  But its goal-side progress was only 5.33 units: the challenge gate required 10 and the deep
  acquisition gate required 35. It selected a recovery target almost 2,000 units away.
- At **17.492 s**, the same car again abandoned a speed-matched ball only 158 units away,
  with retention true and a 0.85 s race advantage. Its small negative goal-side projection again
  overruled physical ownership.
- At **189.867 s**, car 0 had cover, 82 boost and a grounded opponent holding the ball. It stayed
  in support about 1,161 units away because its 1.22 s ETA lost to the carrier's 0.05 s ETA.
  A controlled ball cannot be won by arriving before the player already holding it.
- A first man in 2v2 that failed the challenge gate used the support role, whose gap did not
  compress under pressure. First-man containment now uses the shadow role.

## Changes

Close ground ownership now has a bounded, speed-matched envelope separate from settled roof
quality. A first man can centre a slightly off-axis cushion without retreating. Passing balls,
second-man takeovers and substantially wrong-side geometry remain excluded. Immediate goal
threats still take priority.

A separate carrier interception tests physical control, goal-side positioning, approach heading,
cover, contact height and reachable time. Covered first defenders get a longer interception
window; uncovered defenders must already be close and aligned. The approach remains steerable
and does not force a dodge. Its boost policy matches the travel model. A distant hypothetical
goal crossing no longer preempts this challenge; hard emergencies still use the save planner.

Flick preparation now uses a settled roof envelope rather than the carry's central quality
score. The old power setup moved the ball toward x=50 while its eligibility score expected
x=10, sometimes disabling its own flick. A committed challenger also triggers lateral centring
early instead of continuing to steer the roof ball sideways during launch preparation.

## Research and evaluation method

[RLBot's ball-prediction documentation](https://wiki.rlbot.org/v5/botmaking/ball-path-prediction/)
explicitly excludes car collisions. That limitation motivates the separate short-horizon carrier
model; it is not a claim that a free-flight trajectory predicts dribbling.

[Pleines et al., 2022](https://arxiv.org/abs/2205.05061) evaluate Rocket League skills through
sim-to-sim transfer and physics ablations. We apply the evaluation principle here: check the
physics, randomize nearby fixtures, separate skill outcomes from match outcomes, and retain
the need for real-game validation. This change does not implement their learned policy.

[Nexto/Necto](https://github.com/Rolv-Arild/Necto) provide external learned opponents. Beating
scripted regression fixtures does not establish strength against these opponents or human pros.

Fixtures run the full production decision layer and native RocketSim physics. They perturb and
mirror both recorded possession geometries and rotate them with the teams. Success requires a
real follow-up contact before the challenger and useful progress, not just an aggressive
decision label. The carrier scripts cannot flick; those drills do not validate flick defence.
These are reconstructions of the logged geometry, not complete packet replays: idle teammates
and scripted ground opponents supply the states missing from the telemetry.

An initial fixed 1.2 s follow-up deadline reported 38/60 close-possession successes. Inspection
showed the initial native collision lofted one fixture's ball until about 1.3 s; Stardust
recontacted it before the challenger and advanced it upfield. The corrected deadline uses that
initial loft's predicted descending roof-height crossing plus one planning interval, capped at
2 s, and is applied identically to both builds. The original result is retained here to make
that measurement correction explicit.

## Measured results

Full-policy drills, seed 4103, 60 episodes per team variant, before the final flick-readiness
change:

| Scenario | Baseline blue / orange | Candidate blue / orange |
|---|---:|---:|
| Logged close possession | 15 / 13 | 60 / 60 |
| Slow covered carrier | 2 / 3 | 60 / 60 |
| Fast covered carrier | 43 / 43 | 56 / 57 |
| Fast uncovered carrier | 17 / 14 | 38 / 41 |

Each entry is successes out of 60. All covered and close-possession fixtures had zero
concessions. Both builds conceded once per team in the fast uncovered fixtures. The candidate
still fails those fixtures' strict success and zero-concession criteria. Faster contact alone
is not sufficient for a good 50/50.

After the flick-readiness change, paired dribble duels over 120 episodes improved from **88/120
to 104/120**. Challenger outcomes improved from **25/44 to 40/44**, shadow outcomes from 38/42
to 39/42, and chase outcomes stayed 25/34. Lost-possession episodes fell from 18 to 9; neither
build conceded in this short drill. The candidate passes all of this drill's criteria.

Standalone carry and flick results are identical between baseline and candidate: carry 52/60,
flick 36/60, median power-flick exit speed 2,130 uu/s. Their strict criteria still fail. Four
carry failures are automatic shot conversions before the hold deadline; four are genuine
crawling-speed control losses. A remaining flick defect launches on a setup timeout even when
the ball is outside the recipe's placement tolerance. It requires a separate deadline-aware
executor change, not a relaxed test threshold.

Paired, side-swapped matches before the final flick change, 120 s regulation:

| Format / seed | Games | Candidate W–L | Goals | Goal difference/game |
|---|---:|---:|---:|---:|
| Initial 2v2 / 2810 | 12 | 6–6 | 39–32 | +0.58 |
| Fresh 2v2 / 6210 | 24 | 16–8 | 65–52 | +0.54 |
| Fresh 1v1 / 8910 | 12 | 5–7 | 35–32 | +0.25 |

The 24-game 2v2 win-rate interval is 47–82% (Wilson 95%); the sample is encouraging but still
uncertain. The 1v1 result is inconclusive. These series must not be represented as results of
the later flick revision.

The final reviewed bot (`ed9c55e`) additionally rejects elevated/rising balls outside a grounded
car's contact envelope. Its confirmation drills use the same seed:

| Final-build drill | Result |
|---|---:|
| Close possession, blue / orange | 120/120 / 120/120 |
| Slow covered carrier, blue / orange | 60/60 / 60/60 |
| Fast covered carrier, blue / orange | 56/60 / 57/60 |
| Dribble duel | 101/120 |
| Challenger / shadow / chase outcomes | 39/44 / 38/42 / 24/34 |
| Fast uncovered carrier, blue / orange | 81/120 / 87/120 |

The close-possession and covered-carrier criteria pass, as do all dribble-duel criteria. Fast
uncovered challenges still fail the strict targets and concede once per team. Their test command
correctly exits nonzero; this is a remaining limitation, not an all-green mechanics result.

The final build's fresh 24-game 2v2 confirmation (seed 944000, 120 s regulation) finished
**13–11, 51–46 goals** (+0.21/game; win-rate 95% interval 35–72%). Its six-game tournament
baseline matchup finished **1–5, 9–19 goals**. Pooled over those 30 games this is 14–16 and
60–65 goals: overall match-strength improvement is **inconclusive**, despite the large gains
on the targeted behavioural regressions.

Final frozen-build tournament matchups, 2v2, six side-swapped 120 s games per pair, seed 928000:

| Opponent | Candidate W–L | Goals for–against |
|---|---:|---:|
| Baseline | 1–5 | 9–19 |
| Necto | 0–6 | 1–39 |
| Nexto | 0–6 | 5–49 |
| Party Cannon | 2–4 | 13–15 |

The complete round robin finished **60/60 games with zero errors**. Nexto placed first (24–0),
Necto second (17–7), the baseline third (9–15), Party Cannon fourth (7–17), and the candidate
fifth (3–21). The candidate therefore fails the tournament performance bar; it is not promoted
as an overall stronger release. This negative result is retained alongside the targeted gains.

These results do **not** support a professional-level claim. The learned opponents remain
clearly stronger. Boost economy, uncovered 50/50 outcomes, transitions from shadowing into
challenges, and flick setup timeouts remain priorities. A blind comparison to human professionals
has not been performed. The draft PR is a reviewable behavioural fix and evaluation foundation,
not completion of that larger performance goal.

## Reproduction and harness changes

On Windows with .NET 8 and a C++ build toolchain:

```powershell
./tools/simulator/build.ps1
$sim = 'tools/simulator/Stardust.Simulator/bin/Release/net8.0/Stardust.Simulator.dll'
dotnet $sim physics-check --model all --trials 300
dotnet $sim mechanics-lab --drill close-possession,close-possession-orange,carrier-pressure,carrier-pressure-orange --episodes 60 --seed 4103 --out artifacts/pressure-check
dotnet $sim mechanics-lab --drill fast-carrier-covered,fast-carrier-covered-orange,fast-carrier-uncovered,fast-carrier-uncovered-orange --episodes 60 --seed 4103 --out artifacts/fast-pressure-check
dotnet $sim mechanics-lab --drill dribble-duel --episodes 120 --seed 4103 --out artifacts/duel-check
dotnet $sim tournament --roster roster.txt --size 2 --games 6 --seconds 120 --parallel 3 --seed 928000 --replays --out artifacts/tournament
```

Linux continues to use `tools/simulator/build.sh`. Roster lines are `label = path`; relative
paths resolve against the roster directory. Use a frozen build directory for each participant,
especially on Windows where running assemblies cannot be overwritten.

The launcher selects the host's run command and uses RLBot's TOML parser, including literal
Windows paths. Quoted executable paths and arguments are tested. In-process lab bots relay
match communications rather than trying to send through an unconnected RLBot socket.

Tournament reuse now checks bot and simulator/native code/model/configuration fingerprints,
`STARDUST_*` environment overrides, seed, team size, duration, game count and replay settings.
Cache keys and results are committed in one atomic bundle; cached per-game sides, seeds and team
sizes are checked. Failed games return a nonzero exit code. Fingerprints cannot
identify dependencies outside a bot's working directory; freeze those dependencies too.

The build passes without warnings. All eleven deterministic regression programs pass, including
the new launcher/cache/communication checks; all twelve physics-check groups pass at 300 trials
where applicable. Schema generation is incremental and the Windows script avoids user shell
profiles, keeping repeated regression builds from rewriting the tracked generated source.
These are local Windows results. The Windows/Linux CI matrix is configured; remote CI and a
live Rocket League client validation remain pending for this stacked draft.

All results and replays from this session are retained under the ignored `artifacts/` directory;
the original user telemetry remains in its original log directory.
