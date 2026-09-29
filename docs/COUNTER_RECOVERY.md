# Counterattack recovery evaluation — September 2026

The counter-danger branch could mistake a persistent driving action for a clearance. It returned
before refreshing the route, sometimes steering back toward a waypoint the advancing ball had
already passed. The branch now retains only its selected defensive shot; ordinary recovery and
interception drives continue through planning on subsequent ticks.

This fixes a demonstrated decision fault. It does not establish professional-level play or a
competitive improvement on its own. The learned-opponent defence fixtures below still fail.

## Evidence and regression

In cycle-3 `candidate-vs-Nexto/game-000`, car 1 entered `counter clear` at 19.400 s while its action
was `DefensiveDrive`. Its target remained exactly `(2121.1, -168.7)` through 20.708 s while the ball
advanced from approximately `(3167, 72)` to `(1719, -2155)`. The car slowed near that obsolete
target and then steered back toward it. Emergency handling finally resumed at 20.758 s, with the
save target 5,233 units away. A goal followed at 22.458 s; this sequence alone does not prove the
goal was preventable.

The same path appeared in Necto game 002 at 72.988–74.204 s. The recovery target stayed fixed
while its signed progress toward our goal relative to the ball changed from +573 to -707 units.

A mirrored two-tick planner regression reproduces the fault with a ball moving 80 units in
0.08 seconds. Before the fix, the second tick mislabels the recovery as a clearance. After the
fix, it updates the recovery destination. The original selected-shot continuation condition
remains in place.

[RLBot's game-data documentation](https://wiki.rlbot.org/v5/botmaking/game-data/) states that its
ball prediction assumes no car hits the ball. Both new touches and ordinary advancing play
therefore require replanning; a persistent driving action is not proof of a valid clearance.

## Native replay-based tests

`CounterReplayDrill` runs the complete 2v2 policy with Necto or Nexto from states immediately
before the recorded transitions. Each case has 24 episodes per team, mirrored laterally with
small position perturbations. Success requires a team touch without conceding during five
seconds. These fixtures approximate the recording: positions and velocities are rounded,
angular velocity and active jump/flip timers reset to zero, active flipping is disabled, and
`AirTimeSinceJump` is 0.3 seconds. Opponent
internal state is fresh. The Nexto fixture moves the original subject from seat 1 to seat 0.

| Fixture | Previous policy | Recovery fix |
|---|---:|---:|
| Necto, successes | 16/48 | 13/48 |
| Necto, conceded | 32/48 | 35/48 |
| Nexto, successes | 0/48 | 0/48 |
| Nexto, conceded | 48/48 | 48/48 |

The Nexto reset leaves the subject without boost and its teammate far upfield. Updating the
route does not recover that position in these trials. The Necto result also fails to show an
outcome improvement. These failures remain visible; the pass criteria were not weakened.

A separate proposed fixture counted how long a waypoint stayed unchanged. It was discarded:
a correctly replanned interception can legitimately keep the same destination. An unchanged
target alone is not evidence that the planner stopped running.

## Rejected anchor experiments

These experiments preceded the recovery fix and are not part of the production change.

| Experiment, 16 games against cycle 3 | Wins–losses | Goals |
|---|---:|---:|
| Deeper anchors plus continuous lateral blend | 6–10 | 34–42 |
| Deeper anchors only | 9–7 | 35–36 |
| Continuous lateral blend only | 10–6 | 36–24 |

The deeper-anchor controller passed 120 narrow arrival tests but failed two added counterexamples:
some wide targets could never satisfy its coverage requirement, and its arrival radius could
stop just outside the required corridor. The combined policy also worsened full-policy drills.
The lateral blend removed a real discontinuity, but its backwall fixture success fell from
13/48 to 7/48, with concessions increasing from 29/48 to 37/48. It needs further work before
promotion. Both changes were removed; their local experiment records remain under `artifacts/`.

An additional isolation series compared the recovery fix with that same lateral blend enabled
on both teams: 6–10, goals 31–49. This is not the final shipped policy comparison.

## Final tournament

The frozen candidate contains only the recovery fix on top of cycle 3. The five-bot round robin
uses four side-swapped, 120-second 2v2 games per pair, seed 997000. The candidate finishes
**third of five, 7–9, 28–68 goals**:

| Opponent | Wins–losses | Goals |
|---|---:|---:|
| Previous cycle-3 build | 3–1 | 12–7 |
| Necto | 0–4 | 1–21 |
| Nexto | 0–4 | 2–34 |
| Party Cannon | 4–0 | 13–6 |

All **40 final games are valid**. The first attempt completed 39 games and failed one after
Party Cannon threw `ArgumentOutOfRangeException` in its boost constructor, then stopped replying
at 114.569 s. Its log, result and initial standings are preserved under
`artifacts/cycle4-first-party-failure` and `artifacts/cycle4-tournament-first-attempt.*`.
A single lower-concurrency retry reused 36 games from complete pairs and reran the entire
Necto–Party Cannon pair. All four retry games completed. No installed opponent was modified.
Final records are Nexto 16–0, Necto 12–4, Stardust 7–9, Party Cannon 3–13 and the prior build 2–14.

The Nexto games still show weak mobility and resource management: 1,155 uu/s average speed,
18 average boost, and 37.9% of time without boost, against Nexto's 1,484 uu/s, 41 boost and
23.8% empty. Stardust touched first on all 40 kickoffs, but the teams scored 2 versus 10 goals
within ten seconds of kickoff. First-touch counts alone would badly overstate kickoff quality.
These observations identify further work; they do not prove that a single resource or kickoff
change would close the competitive gap.

## Reproduction

All eleven local regression programs pass, **229 checks**. The gameplay commit is
`4166710bd9fed344700ef81b90bc79a431ec521b`.
[CI run 36580686288](https://github.com/FlamingFury00/Stardust-3.0/actions/runs/36580686288)
passes Windows and Ubuntu regressions, native physics-model checks, and the packaged bot smoke
test including the trimming/AOT warning gate. Live Rocket League validation remains outstanding.

A historical human reference is now available locally: Carball's public
[OCE RLCS replay fixture](https://github.com/SaltieRL/carball/blob/master/carball/tests/replays/OCE_RLCS_7_CARS.replay),
identified by its [benchmark](https://github.com/SaltieRL/carball/blob/master/carball/tests/benchmarking/benchmarking.py).
The downloaded replay has SHA-256
`1c78965ba19928546b4e091b98af0567bafc9a21408c470ae6be6b6506392da7`.
`rrrocket` 0.11.6 parses it with CRC checking into 10,466 frames. Its metadata identifies a
2018-09-09 3v3 match, "Match 6 Game 1 - TM 4 - 2 CHMP", including CJCJ and Express.
This is preparation for a comparison, not a completed blind review or a current-pro benchmark.

Build the simulator with `tools/simulator/build.ps1` or `tools/simulator/build.sh`, then freeze
the candidate and reference build directories before running comparisons. The reference uses
the cycle-3 `Bot.dll` with the same new simulator and communication dependencies.

```powershell
dotnet $sim mechanics-lab --drill counter-replay-necto,counter-replay-necto-orange --episodes 24 --seed 996041 --opponent $necto --out artifacts/counter-necto
dotnet $sim mechanics-lab --drill counter-replay-nexto,counter-replay-nexto-orange --episodes 24 --seed 996051 --opponent $nexto --out artifacts/counter-nexto
dotnet $sim tournament --roster roster.txt --size 2 --games 4 --seconds 120 --parallel 2 --seed 997000 --replays --out artifacts/counter-tournament
```
