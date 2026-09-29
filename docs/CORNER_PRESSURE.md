# Corner pressure, defensive turns and match reliability

This follows [the recovery/finishing evaluation](RECOVERY_FINISHING.md). The retained gameplay
changes are in `3e91ad1`; transport fixes are in `09125bc`, and `32b264a` enables CI for the
stacked PR. The original policy baseline remains `8e99957`, and the previous candidate is
`f8ff58e`. Professional-level play remains unachieved.

## Why the first defender left so much space

The second tournament exposed an additional positioning defect. `ShadowTarget` applied the
goal anchor's mouth bounds to the first defender once its waypoint entered the final 1200 units.
That could park a healthy first defender inside the posts while an opponent controlled a wide ball.

- Against Necto at 56.613 s, the first defender retained 40 boost but stood 2079 units from the ball,
  with its assigned waypoint only 247 units away.
- Against Nexto at 164.749 s, the first defender retained 44 boost, stood 1757 units from the ball,
  and had a waypoint only 130 units away.

The Shadow role now retains its wide, goal-side position while still respecting the back wall,
corner plane and actual goal opening. The Anchor and Support roles retain their existing bounds.
Mirrored regressions and the existing 12000-point geometry sweep check the goal-side invariant.

## Turns near the goal

Removing the positioning clamp alone did not fix the approach. A traced back-wall fixture showed
the car accelerating through a turn from y = -4869 into the net, reaching approximately -5501
after one second while trying to reach a field-side waypoint.

`DefensiveDrive` now evaluates target speeds over a 0.8-second ground rollout near the own goal.
It includes braking, steering response and the existing fitted ground model. A coasting lateral
envelope prevents a sideways landing from being treated as stationary. Normal aligned driving
keeps its requested speed; constrained turns slow down. The lowest-speed fallback is best effort,
not a collision-free guarantee, and the rollout does not model contacts with other cars or the ball.

This use of short-horizon velocity evaluation relates to
[Fox, Burgard and Thrun's dynamic-window work](https://publications.ri.cmu.edu/the-dynamic-window-approach-to-collision-avoidance).
Here, the existing steering controller is retained and forward target speeds are sampled against
the game's ground model.

The native `goalward-slip` drills pass **120/120** mirrored cases. Their 95th-percentile maximum
goalward depth is about **4903 units**, with no boost during the initial slide. This validates
those recovery fixtures; it does not establish general defensive competence.

## Replay-based tests against learned opponents

The corner fixtures reconstruct the recorded positions, velocities and headings, with small
perturbations and team/side mirroring. Replay rounding and missing angular velocities limit their
fidelity: angular velocities reset to zero, and pads start available. Both defenders run the full
Stardust policy; opponents are external Necto or Nexto processes. No director selects a successful
defensive action.

| 48 episodes per fixture family | Prior policy: successes / conceded | Final policy: successes / conceded |
|---|---:|---:|
| Necto corner approach | 17 / 27 | 22 / 19 |
| Nexto back-wall approach | 6 / 35 | 13 / 29 |

The initial first defender's median closing time improves from roughly 1.5–1.6 s to 0.96–1.02 s
in the Necto fixture, and from about 2.1–2.2 s to 1.2 s in the Nexto fixture. The final fixtures
also track the team's closest defender so a legitimate role exchange is not counted as passivity.
**Both fixture families still fail their success and concession criteria.**

Further review identified a remaining contract mismatch: an assigned far-post anchor can fail
`CoversGoal` even when a stationary car reaches it. In the current Necto game at 44.386 s, ball
(-2421.9, -4938.3) produces target (672.8, -5054.1), outside that predicate's corridor despite a
valid 181.7-unit depth span. Coverage geometry, target selection and arrival tolerance need to be
evaluated together before changing the challenge gate. Brief held positions facing away from play
were also observed, but intervening play prevents attributing later goals directly to that posture.

An earlier candidate, before adding the lateral envelope, split an initial 16-game comparison
**9–7**, **41–27 goals**, against the previous candidate. That small sample is encouraging but
does not prove a strength gain; the final tournament evaluates the retained build separately.

## Final tournament

The six-bot, side-swapped 2v2 round robin used four 120-second games per pair, seed 982000,
with trace, telemetry and replays enabled. The candidate's gameplay matches `3e91ad1`. Both
Stardust comparison builds use the same repaired communication library with their original policies.

The first attempt completed 59 games; one Nexto process exited during startup with code
`-1073740022`, before any game time elapsed. Its logs, results and incomplete standings are
preserved under `artifacts/cycle3-tournament-first-nexto-attempt` and
`artifacts/cycle3-tournament-first-attempt.md`. The cause of that third-party startup failure is
not established. Resuming at parallelism 1 reused the 56 games from complete pairs and reran the
entire four-game candidate–Nexto series. **The resumed run exits successfully with 60/60 valid
games and zero failed games.** This does not erase the original startup failure.

| Rank | Bot | Wins–losses |
|---|---|---:|
| 1 | Nexto | 20–0 |
| 2 | Necto | 16–4 |
| 3 | Candidate | 9–11 |
| 4 | Party Cannon | 6–14 |
| 5 | Previous candidate | 5–15 |
| 6 | Original baseline | 4–16 |

| Opponent | Candidate record | Goals for–against |
|---|---:|---:|
| Previous candidate | 4–0 | 14–7 |
| Original baseline | 1–3 | 9–11 |
| Necto | 0–4 | 3–24 |
| Nexto | 0–4 | 2–31 |
| Party Cannon | 4–0 | 15–7 |
| Total | **9–11** | **43–80** |

The candidate performs well against two scripted opponents in this sample but loses its direct
comparison with the original baseline and every game against the learned opponents. Tournament
placement is specific to this roster and small series. It does not establish professional-level
play or universal superiority over the original build. The PR remains a draft.

## A small-pad routing experiment was withheld

At 42.494 s in the Nexto replay, an empty support car could approach the small pad at
(2048, -1036) by adding about 135 units to its route. The general refill gate rejected the option
because there was no explicit cover and the ball was insufficiently upfield. Availability was
inferred from the preceding four seconds without a car near that pad.

An experimental waypoint policy collected the pad in **180/180** held-out controller fixtures,
with 1.783 s p90 defensive arrival. With both Stardust defenders running their full policy against
Nexto, it changed pickups from **0/48 to 45/48**, and concessions from **15/48 to 13/48**.
The latter difference is too small to claim improved defence. Its 24-game comparison against the
same build without routing finished **11–13**, **48–53 goals**, with no match errors.

Review found that its two-leg ETA could reuse the same boost supply and omit defensive turn
braking. Admission bounds could also cancel a committed pad close to the endpoint. A replacement
rolling estimate removed repeated fuel use but did not meet the pickup criteria across the expanded
fixtures. These were reasons to withhold the feature, rather than promote an isolated pickup score.

The experiment was removed from the default bot. Its source snapshot, reports and patch remain
under `artifacts/cycle3-route-rejected-source` and `artifacts/cycle3-*`. The full-policy
`support-pad-replay` fixture remains as an unresolved resource-management test.

## Communication and process output

A real `Bot.Run` loopback test now sends fragmented predictions, game packets and interleaved
match claims, requiring one correctly associated controller response for every packet. A deliberate
controller exception reproduced a lockstep stall in the old manager. The manager now logs the frame,
releases controls for that frame and continues with the next packet. This does not establish that
controller exceptions caused the earlier unexplained timeouts.

The prior dedicated-match-worker change was insufficient for Windows redirected process pipes.
A constrained-pool test that writes to both stdout and stderr reproduced another stall. Dedicated
readers now drain both streams independently of the pool. Tests verify all 512 emitted lines,
continued drainage after a log sink fails, and bounded cleanup when a crashed wrapper's surviving
child retains pipe handles. A cleanup timeout explicitly reports detached output rather than
claiming that all output was retained. It does not certify termination of arbitrary descendants
launched by a broken third-party wrapper.

Timeout diagnostics identify the seat, last sent frame and game time. Wrong-seat controller
messages are rejected. Disposal of shared read/write buffers no longer masks startup failures.
The approach is consistent with Microsoft's discussion of
[redirected-pipe deadlocks and runtime changes](https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/).

## Validation and reproduction

All eleven local Windows regression programs pass: **228 checks**, including 20 simulator checks
and 60 team/defence checks. Release builds have zero warnings and errors.
[CI run 36567743368](https://github.com/FlamingFury00/Stardust-3.0/actions/runs/36567743368)
also passes Windows and Ubuntu regression jobs, native physics-model checks, and the bot-pack
build/smoke test, including the trimming/AOT warning gate. A live Rocket League client has not been
validated in this iteration.

Build with `tools/simulator/build.ps1` on Windows or `tools/simulator/build.sh` on Linux. Use frozen
build folders for comparisons. Here `$sim` points to `Stardust.Simulator.dll` and `$necto`/`$nexto`
point to installed bot configurations.

```powershell
dotnet $sim mechanics-lab --drill goalward-slip,goalward-slip-orange --episodes 60 --seed 981103 --out artifacts/slip-check
dotnet $sim mechanics-lab --drill corner-pressure,corner-pressure-orange --episodes 24 --seed 971011 --opponent $necto --out artifacts/corner-check
dotnet $sim mechanics-lab --drill backwall-pressure,backwall-pressure-orange --episodes 24 --seed 971011 --opponent $nexto --out artifacts/backwall-check
dotnet $sim mechanics-lab --drill support-pad-replay,support-pad-replay-orange --episodes 24 --seed 976019 --opponent $nexto --out artifacts/support-check
dotnet $sim tournament --roster roster.txt --size 2 --games 4 --seconds 120 --parallel 3 --seed 982000 --replays --out artifacts/tournament
```

The human-pro blind comparison remains outstanding. A public
[RLCS 2026 2v2 reference series](https://ballchasing.com/group/atow-zen-vs-juicy-vatira-pugo3md7z1)
was located, but the site requires Steam login for raw replay downloads. Finding that reference
does not constitute a completed comparison.

The publicly visible statistics for
[one game in that series](https://ballchasing.com/replay/089b2778-1b65-41a8-8681-c50f69c61eef)
provide useful context: the four players average 1643–1694 uu/s, retain 42.6–49.4 boost on average,
spend 20.1–25.6% of play at supersonic speed, and have zero boost 8.1–14.6% of the time. In the
first tournament attempt's three completed candidate–Nexto games, the corresponding figures are 1158 uu/s, 16 boost,
3.5% supersonic and 39.3% empty boost. These figures come from different opponents and sampling
pipelines; they identify hypotheses about mobility and resource management, not a matched or
blind performance test.
