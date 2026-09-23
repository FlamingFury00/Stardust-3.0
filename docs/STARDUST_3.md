# Stardust 3.0 candidate: control, possession, and an evaluation path

This branch is an implemented C# controller upgrade built from `v5` at `0ec1f3784d0ecf8115430d78af9eb7b2de1813c6`. It is **not a verified professional-level bot**. Build success and deterministic tests establish specific software/control properties, not a win-rate improvement. No trained neural policy, match series, or in-game reset success rate is included.

## What changed

| Layer | Implementation | Intended benefit |
|---|---|---|
| Packet lifecycle | Current-frame delta before execution; duplicate-packet handling; clock-discontinuity invalidation; cancellation before action execution; safe action replacement | Avoid stale plans, timing drift, and accidental removal of a replacement action |
| Jump state | Persistent `HasJumped`, `HasDoubleJumped`, `HasDodged`, `DodgeTimeout` from the pinned v5 schema | Do not mistake the end of an animation for a restored flip |
| Prediction | Exact first-match search; timestamp interpolation; null/short/duplicate handling; public shot-validity check | Remove missed coarse-index/tail intercepts and unsafe interpolation |
| Strategy | Predicted-goal supervision before possession/boost; separate defensive-shot identity; approximately 8 Hz tactical replanning; fused loose-ball/next-touch opponent clock; at most 48 prediction candidates per shot search | React to danger without repeatedly restarting mechanics or trusting stale ground-only races |
| Possession | Ground catches, hood-relative carries, pressure-triggered flicks, possession-first routine-shot arbitration, JumpShot-to-AerialCarry handoff, velocity-matched aerial carries | Preserve controllable states instead of converting every valid touch into a scripted hit |
| Orientation | Quaternion shortest-rotation error and angular-rate damping | Recover from inverted/backward attitudes without a 180-degree cross-product dead zone |
| Reset attempts | Entry constraints, spent-flip evidence, own wheel-contact evidence, restored packet flags, timed follow-through | Avoid claiming a reset merely because the car is near the ball |
| Team play | Deterministic equal-ETA ownership; invariant-culture shot claims; heartbeat/expiry; left-goes kickoff and same-side cheat/cover | Reduce mutually yielding claims and duplicate commitments |
| Boost/recovery | Goal-side small/large on-route pickups; landing-surface orientation | Avoid abandoning defense for a corner boost and reduce unproductive aerial tumbling |

The existing ground/jump/double-jump/aerial shot solvers and kickoff/drive subactions remain. This is deliberately not a wholesale framework migration.

## Build and regressions

Use the .NET 8 SDK. On Linux, enable the existing FlatBuffers generator first:

```sh
chmod +x src/generate-flatbuffers.sh src/flatbuffers-schema/binaries/flatc
dotnet build src/Bot/Bot.csproj --configuration Release
dotnet run --project tests/Stardust.Tests/Stardust.Tests.csproj --configuration Release
dotnet run --project tests/Stardust.ModelChecks/Stardust.ModelChecks.csproj --configuration Release
```

The first executable tests actual production methods: packet timing, persistent jump flags, reset evidence, action interruption, finite outputs, claims, prediction interpolation, team symmetry, control signs, possession gates, relative-motion invariance, and release-before-dodge sequencing. Seeded loops additionally exercise 10,000 clock updates and 2,000 attitude targets.

The second executable integrates an **analytic attitude model**, with the torque/damping equations described in [1], at 120, 60, and 30 Hz. It checks convergence across fixed and seeded initial orientations. It also tests boost-route constraints. It is not RocketSim, does not simulate ball contact, and must not be described as an in-game mechanics benchmark.

The GitHub Actions workflow builds the bot and runs both programs. Failures are not ignored. No additional runtime package or training service was added to the bot.

## Runtime switches and rollback

Set environment variables **before starting the bot process**:

| Variable | Default | Behavior |
|---|---|---|
| `STARDUST_GROUND_CONTROL` | enabled | Set `0` to disable the new catch/carry/flick selection |
| `STARDUST_AERIAL_CARRY` | enabled | Set `0` to disable aerial possession control |
| `STARDUST_FLIP_RESETS` | disabled | Set `1` to enable experimental reset attempts within an aerial carry |
| `STARDUST_TRACE` | disabled | Set `1` to log human-readable strategy transitions and ETA estimates |
| `STARDUST_TELEMETRY` | disabled | Set `1` to emit structured `STARDUST_JSON` frame/decision telemetry |
| `STARDUST_TELEMETRY_HZ` | `10` | Legacy JSONL telemetry samples per second; clamped to 1–30 Hz |
| `STARDUST_DEBUG` | enabled for the normal packaged bot | Set `0` to disable the live local tactical debugger; test/probe instances stay quiet unless explicitly enabled |
| `STARDUST_DEBUG_OPEN` | enabled with debugger | Set `0` to prevent automatically opening the dashboard in the default browser |
| `STARDUST_DEBUG_PORT` | `49152` | Preferred loopback port; Stardust tries the next 9 ports if occupied |
| `STARDUST_SCENARIOS` | enabled for the normal packaged bot | Set `0` to disable automatic scenario capture; set `1` to force it on for a probe/test launch |
| `STARDUST_SCENARIO_HZ` | `20` | Rolling world-state capture rate; clamped to 2–60 Hz |
| `STARDUST_SCENARIO_PRE_SECONDS` | `8` | Seconds retained before a concession/manual capture; clamped to 2–20 |
| `STARDUST_SCENARIO_DIR` | auto | Optional output directory for captured scenarios |

Example in PowerShell, before launching the bot from that environment:

```powershell
$env:STARDUST_DEBUG = "1"
$env:STARDUST_SCENARIO_HZ = "20"
$env:STARDUST_SCENARIO_PRE_SECONDS = "8"

# Optional legacy logs / experimental mechanics:
$env:STARDUST_TELEMETRY = "0"
$env:STARDUST_FLIP_RESETS = "1"
```


### Live tactical debugger and scenario capture

The normal packaged Stardust bot now starts the debugger automatically, so it no longer depends on environment variables being propagated through the RLBot manager. Set `STARDUST_DEBUG=0` to opt out. The debugger is a zero-dependency HTTP dashboard bound only to `127.0.0.1` and opens in the default browser unless `STARDUST_DEBUG_OPEN=0`. The console prints the exact `STARDUST_DEBUG_READY url=...` address. The dashboard updates roughly every controller sample and shows:

- a scaled field view with all cars, the ball, current target, and bot-to-target line;
- the current supervisor decision, objective, and a human-readable explanation of why that state exists;
- car speed/boost/goal-side progress, ball speed and latest touch, raw ground-race ETAs, fused opponent-contact ETA, raw/effective free time, pressure and goal-threat clocks;
- possession, ground/air acquisition readiness, shot-to-air-carry handoff readiness, challenge, goal-side, open-lane, emergency, counter-threat, and fast-recovery checks;
- actual sanitized controller output and the active mechanic/subaction;
- captured scenarios with **Save current buffer** and **Replay** buttons.

The dashboard server uses a loopback TCP listener rather than a platform-specific UI toolkit, so the normal bot build remains cross-platform and gains no desktop/NuGet dependency.

Scenario recording keeps a rolling physics buffer even during goal/replay phases, where normal controller output may stop. When the opponent score increments, Stardust automatically writes a `conceded-*.json` capture containing the configured pre-goal window. Manual captures use the same format. Each frame contains the tactical snapshot plus ball location/velocity/angular velocity, every car's location/velocity/angular velocity/rotation/boost/jump state, boost-pad state, gravity, score and match phase. The capture records a pre-score `replay_frame_index` intended as the deterministic reproduction point.

When RLBot state setting is enabled, the dashboard **Replay** button restores the captured replay frame's ball and car physics/rotation/boost through RLBot's game-state API. Boost-pad timers and historical planner state are retained in the file for diagnosis but are not currently written back by the C# state-setting builder.

If Stardust is running from a source checkout, captures default to `scenarios/captured/` at the repository root. Packaged builds fall back to a `scenarios/captured/` directory beside the executable. Override either with `STARDUST_SCENARIO_DIR`.

### Legacy JSONL telemetry

The existing `STARDUST_TELEMETRY=1` JSONL stream remains available for automation and offline parsing. Each sampled line starts with `STARDUST_JSON `; decision changes emit immediately and frame snapshots are rate-limited by `STARDUST_TELEMETRY_HZ`. The live debugger is the preferred interactive workflow, while JSONL is useful for bulk analysis or attaching a single machine-readable file to an issue.

A reset attempt is not selected on every aerial: it requires adequate height, fuel, proximity, low relative speed, an already spent flip, and apparent opponent separation. A confirmed reset additionally needs a recent own touch with the wheels facing the ball and persistent flags showing that the flip was restored. Neither a normal airborne state nor a touch by another player is sufficient. Unsuccessful attempts time out into replanning/recovery.

For an exact A/B baseline, use the original commit in a separate checkout; disabling feature flags does **not** restore the original strategy or packet lifecycle. The original `v5` branch and bot registration identity are not overwritten by this feature branch.

## Control design

The ground carry projects **relative** ball/car motion before placing the ball over a hood target. Advancing only the ball would inject a false position error whenever both objects share a high world velocity. `PossessionControl` makes this invariant directly testable. Throttle follows longitudinal error and ball velocity; lateral error changes the heading. Boost and handbrake are suppressed during the carry.

The aerial carry samples the framework ball prediction at a short horizon and advances the car ballistically to the same time before computing a velocity-matching residual PD correction. Because both future states already include gravity, the horizon residual deliberately does not add gravity feed-forward a second time. A shortest-rotation quaternion error drives pitch/yaw/roll. Boost is gated by nose alignment, fuel, contact closing speed, and hysteresis. This is a **receding-horizon PD controller**, not a full nonlinear MPC optimizer or learned policy.

Strategy keeps the conservative ground-intercept race estimate for ordinary ownership, but treats the independent short-horizon opponent-contact clock as authoritative when the two disagree enough to indicate a stale ground-only race (for example, an airborne opponent already committed to the ball). The pressure estimator uses planar nose alignment for grounded approaches but full 3D nose alignment for airborne attackers; a pitched car can be aimed directly at a raised ball even when its flat heading points away. Loose-ball possession and challenge continuation use the resulting effective free-time margin, while high-quality existing possession retains its separate continuation rules. Defensive **contact** planning still caps scripted touches at the earliest credible state-change deadline: untouched goal crossing, opponent race ETA, or modeled next touch. Defensive **staging** is different: it may use the full current threat horizon and simply replan when the opponent actually touches. This prevents an early next-touch clock from suppressing every reachable intermediate block and forcing a hopeless sprint to the final goal crossing. Losing-race solo shadows also widen their reaction gap instead of compressing toward an attacker they cannot beat. For lateral attacks, the base shadow/recovery depth is re-shaped toward the angular bisector of the two post rays: the side-threat weight combines ball lateral position, cross-goal speed, defensive depth, race loss, and available team cover. The lateral corridor remains wider before the line and narrows only near the real goal plane, so a wall attacker is not prematurely reduced to a center-mouth waypoint. This matters because RLBot's ball prediction assumes no future car collisions.

Goal-line saves are arrival-time driven rather than distance-threshold driven, but they are now strictly **positioning** actions: they compute the flat speed needed to cover the mouth before the crossing and never initiate a jump, dodge, or aerial. Elevated defensive contacts are searched first through the existing `GroundShot`, `JumpShot`, `DoubleJumpShot`, and `AerialShot` solvers; if none of those mechanics can prove a reachable contact, the fallback stays on its wheels or, when already airborne, yields to `Recover` instead of inventing a bespoke goalkeeper flight. The same rule applies to `EmergencyClear`: it is now only a low, grounded, point-blank block and immediately yields once the ball rises into shot-mechanic territory. Before an active low block is abandoned for a final-line sprint, the supervisor still applies an intentionally optimistic straight-line reach bound. Threat staging and emergency pre-contact waypoints remain field-side of the own goal line. DefensiveDrive reverse mode remains local, while defensive speedflips/dodges now require a long route that has remained stable for at least 0.22 s plus a cooldown after a committed mobility action, preventing moving shadow targets from spawning throwaway flips. Cars grounded behind their own goal line must first exit through the mouth before normal loose-ball attack planning can reclaim control.

Kickoff keeps the existing speedflip approach and spawn geometry, but the final contact dodge remains inside the Kickoff action and becomes touch-interruptible. The first changed ball touch therefore releases kickoff ownership immediately into normal active-play planning instead of allowing a stale non-interruptible dodge to add a second follow-through touch.

Shot selection still prefers lower-cost ground/jump options over expensive aerials when comparable contacts exist, but routine shot availability no longer automatically defeats possession. A separate high-value possession-finish gate protects point-blank/open scoring contacts, while safely dribble-ready states may preempt routine interruptible shots. A successful GroundCatch now hands directly into GroundDribble instead of returning control to generic shot search for one planning interval. Likewise, a JumpShot that has already created genuine airborne control may hand off to AerialCarry before its final dodge unless that shot is itself a real finish; established air control uses a wider handoff gate than a cold aerial-carry start, and low post-jump carry setups are no longer rejected solely for being under 180 uu of car height. The shot-ranking speed heuristic uses the same initial contact-velocity geometry as the GroundShot/JumpShot solvers; it does **not** use the deliberately offset pre-contact car target as a velocity proxy. The estimate remains a ranking heuristic, not a full car-ball collision simulator. Emergency clears use the **own** goal as an exclusion target. Expensive searches remain separated from per-frame control, but actual p95/p99 tick latency must still be measured on the target hardware with multiple bots.

## Research and what was actually used

**[1] Samuel Mish, Rocket League aerial-control notes.** [Primary source](https://www.smish.dev/rocket_league/aerial_control/). The orientation/angular-velocity representation, torque signs, and damping model inform the independent controller and analytic tests. The implementation here is not copied from RLUtilities and adds no RLUtilities dependency.

**[2] RLGym, Training an Agent.** [Official documentation](https://rlgym.org/Rocket%20League/training_an_agent/). Describes the RocketSim/PPO training workflow. This supports a practical next stage: train individual possession skills in a fast simulator and evaluate them separately. No such training was executed in this PR.

**[3] Moschopoulos et al. (2023), Lucy-SKG: Learning to Play Rocket League Efficiently Using Deep Reinforcement Learning.** [Paper](https://arxiv.org/abs/2305.15801). Its reward-combination, auxiliary-task, and ablation ideas are relevant to a future learned controller. The historical results in that paper are not a statement about current bot rankings, and the Lucy-SKG policy or training algorithm is not included here.

**[4] Pleines et al. (2022), On the Verge of Solving Rocket League using Deep Reinforcement Learning and Sim-to-sim Transfer.** [Paper](https://arxiv.org/abs/2205.05061). Demonstrates transfer of particular goalie/striker behaviors rather than proving general professional match strength. This motivates separate skill tests plus transfer validation, not equating a simulator score with full-game success.

**[5] RLBot v5 protocol.** The authoritative integration contract for this change is the repository's [pinned game-data schema](../src/flatbuffers-schema/schema/gamedata.fbs), especially the persistent jump/dodge flags. [Official match-communication documentation](https://wiki.rlbot.org/v5/botmaking/matchcomms/) explains the teammate coordination transport.

## Promotion criteria: run these in Rocket League before a release

Use ordinary Soccar with the same car hitbox, arena, gravity, boost mutator, bot count, and game/framework versions for both builds. Preserve initial-state fixtures, replay files, random seeds where supported, commit hashes, and feature flags. Mirror every fixture and swap blue/orange sides.

1. **Mechanic fixtures.** Start with stationary/moving hood carries, descending catches, pressured flicks, low/high aerial carries, spent-flip reset approaches, zero-boost recoveries, and inverted landings. Vary initial lateral error, speed, height, boost, and opponent approach. Record carry duration, own-touch continuity, ball-control loss, boost per useful touch, landing orientation, and recovery time. A reset is successful only if packet evidence confirms acquisition **and** a subsequent useful dodge/touch occurs; attempts and acquired-but-wasted resets must be separate counters.
2. **Match evaluation.** Run paired 1v1, 2v2, and 3v3 matches against the original `v5` and fixed, named reference opponents. Keep a held-out fixture/opponent set. Report wins/draws/losses, goal differential, own goals, double commitments, empty-net concessions, and a confidence interval. Preselect the sample size and acceptance rule rather than stopping after a winning streak. A practical first pass is 100 paired matches per mode, followed by more runs when uncertainty remains large.
3. **Ablation and deployment.** Compare baseline, runtime/strategy changes, ground-control enabled, aerial-carry enabled, and reset attempts enabled. Do not enable resets by default unless their net contribution is positive and they do not worsen defensive concessions. Measure actual p95/p99 control latency and allocations, including multiple instances in one process. Recheck on the real game even after simulator tests pass.

## A concrete route toward stronger learned mechanics

Keep the deterministic supervisor as a fallback and train **skill-conditioned residual controllers**, not an unvalidated replacement for every decision. A policy could propose bounded corrections to the analytic target acceleration/hood setpoint while the existing availability, resource, and interruption checks remain authoritative. This is a proposed architecture, not an implemented model.

A useful curriculum is: controlled contact -> sustained moving carry -> target-directed carry -> opponent pressure -> flick or aerial finish -> reset acquisition -> useful post-reset continuation. Reset rewards should require a spent-to-restored state transition plus contact evidence, and success should depend on retained possession or a useful follow-up rather than on touching the underside of the ball alone. Randomize contact offsets, boost, latency, opponent starts, and ball velocity; mix harder earlier stages back into later training to avoid forgetting. Evaluate held-out starts and opponents before selecting a checkpoint.

Use RLGym/RocketSim [2] for experience generation, and consider reward-shaping/auxiliary-task ideas from [3]. Export only a policy that has passed the held-out skill and game-transfer checks [4]. Version its observation normalization, action semantics, action-repeat rate, and recurrent-state reset behavior with its weights. Keep controls bounded and inference failure recoverable.

## Known limitations

No in-game match series, RocketSim contact rollout, GPU training, professional-player comparison, or measured multiplier is available from this change. Contact offsets and gains are heuristics requiring hitbox-specific tuning. The reset routine is an experimental acquisition/follow-through attempt, not a guarantee of advanced freestyle chains. Wall/ceiling setups, doubles, musty flicks, and learned opponent modeling are not newly implemented. The new tactical dimensions assume standard Soccar. Ground-race ETA is still not a full opponent aerial predictor; the short-horizon pressure/contact model now mitigates large ground/aerial disagreement, but it is a deterministic heuristic rather than a learned trajectory policy. Existing static RedUtils world data is serialized across bot instances, but broader multi-match/process isolation remains a separate architectural task.
