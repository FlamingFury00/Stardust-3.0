# Recovery, finishing and transport follow-up

This follows [the initial pressure/possession work](PRESSURE_POSSESSION.md). The objective remains
competitive play comparable to professionals; passing isolated drills does not establish that.
The original policy baseline is `8e99957`; the prior reviewed candidate is `fdf9d99`.
This iteration's frozen candidate contains the production changes through `2f73102`.

## What the match replays exposed

Two final-tournament game-000 replays were inspected: against Necto and Nexto. In the Nexto game,
a defender retreated backwards through roughly 73.24–74.94 s while retaining 41 boost. Its reverse
speed could not match the incoming attack. This is a concrete control problem, distinct from the
correlated low-boost totals. No clearly safe nearby full-pad diversion was found in those two
replays, so this change does not grant blanket permission to leave defence for boost.

The defensive controller now distinguishes:

- short net corrections, which remain grounded;
- sustainable reverse shadowing, which preserves its goal-facing orientation;
- a retreat waypoint moving faster than reverse can serve, or a genuinely long route;
- an actual ball collision in the reverse stopping corridor, which requires braking.

Waypoint velocity is measured when the planner updates the target, rather than dividing sparse
planner movement by the 120 Hz action interval. Reorientation checks the field, goal deadline,
and a swept ball/car corridor that includes the backward dodge impulse. Forty samples cover the
1.25 s window, including fast crossings between the old coarse samples.

## Intercept timing

`DrivePhysics.TravelTime` previously clamped negative approach speed to zero. A retreating car
therefore inherited a stationary car's ETA. The model now includes braking time and the distance
lost while moving away, and the carrier gate preserves that signed input.

The native `signed-travel` check uses 300 trials, 150–2000 units of travel, initial speeds across
−2300 to +2300 uu/s, and varying boost. Absolute ETA error is **0.009 s median, 0.060 s p90**;
the old clamped estimate has **0.993 s p90** error on the moving-away cases. This validates the
longitudinal model, not every tactical turning/interception assumption.

## Carry-to-shot finishing

The previous carry routine marked every flick urgent, including uncontested shots. It could
launch before aiming or centring. Free finishes now receive normal preparation; an approaching
challenger, including one arriving during setup, still brings the launch forward.

The goal is retained as a world-space target and the aim updates as the ball moves. Returning
to the turning phase resets the centring timer. Finally, the goal decision checks the nominal
post-flick flight against the posts and crossbar using the existing `BallFlight` model. It
considers both power and lob recipes and accounts for their different horizontal speeds when
checking defender reach. An over-high shot can remain a carry until the finish is viable.

The release height, elevation and recipe gain are estimates, not an exact online contact
simulation. The urgent placement timeout remains a fallback under immediate pressure.

`carry-finish` runs the production GroundDribble-to-Flick transition after installing one carry.
It deliberately isolates that mechanic from the supervisor's separate strike selector. It
counts actual native goals, including legitimate driven-in finishes; selecting a flick alone
does not pass.

| Same 120 fixtures, seed 8281 | Goals |
|---|---:|
| Prior reviewed build | 25/120 |
| Normal preparation instead of unconditional urgency | 83/120 |
| Live goal aiming | 93/120 |
| Flight-aware finish selection | 112/120 |

On a fresh seed, 9913, the final finisher scores **165/180**, with no own goals and p90 goal time
**2.784 s**. The 85% success criterion passes. The accompanying full-policy dribble duel passes
all its criteria at **154/180**. These are synthetic tests, not human-professional comparisons.

Several plausible approaches were rejected:

- Requiring ideal placement while aborting at a challenger deadline reduced duels from 101/120
  to 83/120 and standalone flicks from 75/120 to 63/120.
- Globally extending precise centring reduced standalone flicks to 49/120.
- Faster placement gains helped one challenger sample but lost on a fresh seed: 151/180 versus
  156/180 for the original response. The production carry gains remain unchanged.
- A compact flick search at a 35-unit roof offset did not produce a sufficiently robust replacement.

## Simulator and client reliability

Three fragmented-packet tests failed on the old RLBot reader: interruption after one header byte,
after the complete header, and inside the payload. The reader now retains progress until a whole
frame is available. Only `WouldBlock` is temporary; EOF and other socket errors terminate. Writes
remain blocking while callbacks or controller output run. This follows the documented
[socket blocking semantics](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.blocking?view=net-10.0)
and [stream read behaviour](https://learn.microsoft.com/en-us/dotnet/api/system.io.stream.readexactly?view=net-10.0).

A separate constrained-thread-pool test reproduced callback starvation in the parallel match
runner. Long-lived blocking matches now use bounded dedicated workers, leaving the pool available
to drain bot output and service asynchronous I/O. Successful and failed process exits drain
redirected output before closing logs. Failed executable startup also releases its log; accepted
connections are closed when startup fails. The crash-output test verifies all 1000 emitted lines.

Early traced screening runs had timeouts and were not counted as complete comparisons. Failed
series were rerun. A later traced/telemetry-enabled 16-game run completed with zero errors,
finishing 8–8 and 39–38 goals against the prior candidate. This supports parity, not a proven
competitive strength gain.

The subsequent tournament still exposed intermittent response failures, including a candidate
timeout against Party Cannon and a Party Cannon timeout against the original policy. The exact
candidate failure seed (964001) completed on retry: 5–4 on blue, then 0–4 on orange, with zero
errors in those two diagnostic games. They are not added to the tournament record. A successful
retry does not resolve the original fault. Neither candidate log contains an exception, and
the sampled telemetry cannot identify which seat or processing stage stalled.

Code review also identified two follow-up needs: timeout reports should include the seat and
last sent frame; the manager's controller-exception path currently logs and returns without
sending a controller or disconnecting. That path can leave a lockstep peer waiting, but there
is no evidence it caused these tournament failures. The tested candidate remains frozen rather
than attributing this intermittent fault to an unverified cause.

## Recovery and policy evidence

The initial broad reverse exit passed controller drills but lost its first 16-game comparison
5–11, 35–42 goals. It was narrowed to avoid interrupting a sustainable shadow or braking simply
because a ball was nearby in front of the nose. The narrowed controller passes **480/480** native
cases across both teams: fast retreat, sustainable reverse, near-ball stopping, and net correction.
A subsequent 96-case confirmation passes after the finer collision sampling.

In the long-retreat cases, the car faces forward on the ground by approximately 1.5 s and reaches
about 2204 uu/s with usable boost. Short net correction behaviour remains unchanged. These are
controller tests; match policy can still choose the wrong moment or destination.

Policy screening used the same seeds per opponent (956000 against the original policy, 957000
against Necto). It did not justify disabling either pressure policy:

| Policy | Baseline, 8 games | Necto, 4 games |
|---|---:|---:|
| Both enabled | 5–3, 18–14 goals | 0–4, 0–22 |
| Carrier challenge disabled | 4–4, 23–23 | 0–4, 1–24 |
| First-man shadow disabled | 5–3, 19–15 | 0–4, 0–19 |
| Both disabled | 5–3, 23–21 | 0–4, 1–28 |

These are small screening samples. Failed games were excluded and their complete series rerun
with corrected transport. The two switches remain available through `STARDUST_TUNE` as
`Stardust.CarrierChallenges` and `Stardust.FirstManShadow`; defaults remain enabled.

The [RLGym PPO development guide](https://github.com/ZealanL/RLGym-PPO-Guide/blob/main/making_a_good_bot.md)
was also reviewed for resource-management and outplay context. No learned policy or boost-detour
change is claimed here: the evidence did not support one yet.

## Final tournament: candidate results

The frozen candidate played a six-bot, side-swapped 2v2 round robin, four 120-second games per
pair, seed 960000. Both comparison Stardust builds use the repaired communication library with
their original policies. Trace, telemetry and replays are enabled.

All **60 scheduled games** were attempted: **58 completed and two failed**. The command exited
with code 1, correctly rejecting the run as a reliability pass. The failed candidate–Party Cannon
and baseline–Party Cannon games remain in the artifact directory; they were not overwritten by
the diagnostic retry. Standings over valid games are:

| Rank | Bot | Wins–losses |
|---|---|---:|
| 1 | Nexto | 20–0 |
| 2 | Necto | 16–4 |
| 3 | Party Cannon | 7–11 |
| 4 | Original baseline | 6–13 |
| 5 | Candidate | 5–14 |
| 6 | Prior reviewed candidate | 4–16 |

The candidate's individual matchups were:

| Opponent | Candidate record | Goals for–against | Failed games |
|---|---:|---:|---:|
| Prior reviewed candidate | 2–2 | 10–8 | 0 |
| Original baseline | 2–2 | 4–8 | 0 |
| Necto | 0–4 | 0–23 | 0 |
| Nexto | 0–4 | 3–30 | 0 |
| Party Cannon | 1–2 | 7–13 | 1 |
| Total valid candidate games | **5–14** | **24–82** | **1** |

The failed game is excluded from wins, losses, goals and strength estimates. The two diagnostic
retry games are reported separately above. No competitive promotion is justified by this result;
the candidate has neither established an overall gain over the baseline nor met the requested
professional standard. The PR remains a draft.

Against Necto, the candidate averages 23.6 touches per five minutes per player versus 46.4,
spends only 4.8% of measured play in the attacking third, and has zero boost 43.2% of the time.
Against Nexto, it averages 1140 uu/s versus 1473 and collects 2.4 big pads per five minutes versus
23.0. These observations prioritise sustained pressure, possession and resource management;
they do not by themselves prove a safe boost detour or a particular tactical fix.

## Reproduction

Build with `tools/simulator/build.ps1` on Windows or `tools/simulator/build.sh` on Linux. Use
separate frozen output folders for simultaneous comparisons. In the commands below, `$sim` is
the full path to `Stardust.Simulator.dll`.

```powershell
dotnet $sim physics-check --model all --trials 300
dotnet $sim mechanics-lab --drill carry-finish,dribble-duel --episodes 180 --seed 9913 --out artifacts/finish-check
dotnet $sim mechanics-lab --drill reverse-retreat,reverse-retreat-orange,reverse-slow,reverse-slow-orange,reverse-near-ball,reverse-near-ball-orange,reverse-net,reverse-net-orange --episodes 60 --seed 9281 --out artifacts/reverse-check
dotnet $sim flick-search --spots 35 --parallel 2 --out artifacts/flick-search
dotnet $sim tournament --roster roster.txt --size 2 --games 4 --seconds 120 --parallel 3 --seed 960000 --replays --out artifacts/tournament
```

The final roster includes the new candidate, prior reviewed candidate, original baseline,
Necto, Nexto and Party Cannon. The three Stardust policies use the same repaired RLBot transport.
Raw reports, telemetry and replays are retained under `artifacts/cycle2-*`.

## Validation scope

All eleven local Windows regression programs pass: **218 checks** in total. The latest counts
include 58 team/defence checks, 13 simulator checks and six possession-mechanics checks. Release
builds complete without warnings or errors, and all **13 native physics groups** pass at 300
trials. Native recovery and finishing results are reported above with their fixture limits.

Linux CI and a live Rocket League client have not been validated in this iteration. The current
stacked PR targets the earlier physics branch; the workflow's `beta` branch filter does not run
CI for that target. Local Windows results must not be described as a cross-platform CI pass.

The professional-level goal remains unachieved. A human-pro blind comparison and real-client
validation are still outstanding; simulator figures cannot substitute for them.
