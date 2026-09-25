# Stardust 3.0: architecture, physics, and how it was measured

Stardust 3.0 is a 1v1/2v2/3v3 Rocket League bot for RLBot v5, written in C# on RedUtils. This
document describes what the bot does, the physics models it plans with, and — because every change
in this branch was accepted or rejected on evidence — how it was evaluated and what the evidence
showed.

## At a glance

| Layer | Where | What it does |
|---|---|---|
| Decision layer | `src/Bot` | Threat-first supervision, possession play (ground catches, hood carries, pressure flicks, aerial carries), shot selection, shadow/anchor/support defence, goal-line saves, boost refills, team claims |
| Kickoff | `src/RedUtils/Actions/Kickoff.cs`, `src/RedUtils/Bot.cs` | Speed-flip kickoffs; the kickoff lasts until the ball is first touched |
| Scripted mechanics | `src/RedUtils/Actions` | Ground/jump/double-jump/aerial shots, dodges, speed flips, half flips, wavedashes, recovery |
| Physics core | `src/RedUtils/Physics` | Ground dynamics and navigation, jumps, dodges, flips, air control, aerial guidance, car–ball impacts — each validated against RocketSim |
| Planning | `src/RedUtils/Planning`, `src/RedUtils/Actions/Strikes` | Strike planner (ground, jump, flip, double-jump, aerial touches aimed with the hit model), block planner, and their executors |
| Evaluation | `tools/simulator` | RocketSim match simulator speaking the RLBot v5 protocol, scenario fixtures, physics-model checks |

## The kickoff fix

RLBot reports the match phase as `Active` about two seconds after a kickoff starts even when nobody
has touched the ball. The previous release ended its kickoff routine there, so the car stopped
mid-approach. A kickoff now lasts until the ball is first touched (`RUBot.IsKickoff`). On its own
this is worth **+0.81 goals per game** against the previous release (36-game 1v1 series; see
[Results](#results)).

## Physics core

Every model the planner uses is checked against RocketSim by `physics-check`, which also runs in CI
(`physics-models` job):

| Check | Model | Result (default 300 trials) |
|---|---|---|
| `hit` | `HitModel`: inelastic impulse with Coulomb friction plus Rocket League's extra hit impulse | Δv direction error p50 1.3° (a centre-line model: 7.4°); speed error p50 0.6 % |
| `drive` | `GroundModel`: throttle, boost, brake, coast and steer dynamics under random open-loop inputs | after 1 s: position error p50 22 uu, heading error p50 1.9° |
| `navigate` | `Navigator`: tangent-arc approach policy and its rollout ETA | 298/300 arrive; ETA error p50 0.03 s, p90 0.14 s |
| `timed` | Timed arrivals with loose slack, and with strike-tight slack under 0.12 s | tight: error p50 0.04 s; more than 0.1 s late in 11 of 238 |
| `jump` | `JumpModel`: minimum jump, hold acceleration, double jump | height error p50 2.7 uu, max 7.1 uu |
| `dodge` | `DodgeModel`: speed-dependent dodge impulse and stick inversion | direction error p90 0.03°; stick inversion p90 0.5° |
| `flip` | `FlipModel` and the pitched contact solver: planned jump-and-dodge touches | 300/300 touch; box 4 uu from the ball on the planned tick; ball direction 1.5°, speed 1.2 % (p50) |
| `orient` | `AirControl`: attitude control with the game's torques and damping | settles in 0.85 s p50 (previous controller 1.23 s) |
| `flight` | `AerialModel`: boosted flight | position error after 1 s p50 3.4 uu |
| `aerial` | `AerialGuidance`: the aerial control law flown closed-loop | 205/205 flown; miss p50 3 uu, p90 5 uu |

Notable details:

- **Navigation never collapses onto its target.** A directed approach steers along its arrival line,
  extended past the destination, so a car a few degrees off the line converges instead of braking
  for an impossible last-metre turn.
- **Timed arrivals keep a margin.** An early car sheds speed only while flat-out driving would still
  arrive more than 0.07 s early, and never in a hard turn, where arrival times are least predictable.
- **Flips are modelled, not improvised.** A forward dodge adds 500 uu/s along the nose at once
  (capped with the rest of the velocity at 2300 uu/s), the nose pitches down at about 7.3 rad/s after
  a two-tick lag, and the height stays ballistic for 0.15 s. Dodging five ticks before contact keeps
  almost all of that impulse in the ball: in RocketSim a flip adds roughly 700 uu/s of ball speed at
  any car speed (`physics-check --model flip-probe`).

## Strike planner

`StrikePlanner` searches the ball prediction for the best reachable touch. A cheap reachability
bound finds the first candidate slice. Each candidate is then solved exactly:

1. Find where the car's hitbox first meets the ball.
2. Choose the lateral offset that sends the ball at the aim, using the hit model.
3. Score the result as the chance of scoring (shot spread, save chance) or clearing.
4. Verify the timing with the same navigation rollout the executor drives.

Jump strikes line up on the contact line, run up at constant speed and take off the model's rise
time before contact. Flip strikes add the dodge. `DrivenStrike` re-solves the contact every 1/30 s
against the live prediction and stands down as soon as the touch is out of reach.

`BlockPlanner` finds the earliest point under the path of a ball heading into the net that the car
can reach in time. It meets the ball there with its body: on the wheels, or with a single or double
jump that puts the roof in the ball's path.

In play, `Tactics.SelectShot` first finds the scripted shot. The planner then searches the same
window, and its strike replaces the scripted shot only when it touches no later. A later touch is
never taken: in a contested game the earlier ball is the one that matters. Allowing up to 0.3 s
later cost about a goal per game.

## Evaluation harness

`tools/simulator` runs bots exactly as RLBot would: each bot is its own process speaking the RLBot v5
flatbuffers protocol, stepped in lockstep with RocketSim at 120 Hz on a generated Soccar arena (no
game assets).

```sh
tools/simulator/build.sh
SIM="dotnet tools/simulator/Stardust.Simulator/bin/Release/net8.0/Stardust.Simulator.dll"

# Paired, side-swapped 1v1 series between two bot builds
$SIM match --a <build dir> --b <build dir> --size 1 --games 72 --seconds 180 --parallel 4 --out results

# Scenario fixtures: kickoff, open-net, vs-keeper, aerial, save, recovery, duel, jump-touch
$SIM scenarios --a <build dir> [--b <build dir>] --suite save --episodes 60

# Physics models against RocketSim
$SIM physics-check --model all
```

Series are paired: every seed is played twice with the builds swapping colours. Kickoff spawns are
jittered by a few units so deterministic bots do not replay one identical game. Scenario episodes
are generated from a seed, so two builds face identical fixtures. With `STARDUST_TRACE=1` the bot
logs its decisions, which the match reports attribute to goals.

A 72-game 1v1 series resolves about ±0.4 goals per game (one standard error), and even 288 games
only resolve about ±0.2, so changes are judged on repeated series with fresh seeds rather than one
lucky run.

## Results

All figures are 1v1, 180-second games, goals per game from the candidate's point of view.

| Candidate | Opponent | Games | Goals per game |
|---|---|---|---|
| Decision layer + kickoff fix | previous release | 36 | **+0.81** |
| Physics-first "Brain" decision layer (removed) | previous release | 36 | −0.67 to −0.75 |
| Kickoff fix + planner strikes replacing scripted shots up to 0.3 s later | previous release | 36 | −1.1 before flips, −0.19 with flips |
| Kickoff fix + planner strikes when no later than the scripted shot | kickoff fix | 72 + 72 + 144 | +0.40, +0.11, +0.03 (pooled +0.14 ± 0.19) |
| **Kickoff fix + planner strikes + planned saves (shipped)** | kickoff fix | 72 + 144 | +0.61, +0.22 (pooled **+0.35 ± 0.22**) |
| Kickoff fix + planned aerials only | kickoff fix | 72 | +0.14 |
| Kickoff fix + planned saves only, before the flip-execution fixes | kickoff fix | 72 | −0.06 |

What these showed:

- **The decision layer, not the physics, was the gap.** A physics-first decision layer built on the
  new planner lost to the previous release even with the new strike execution. It had no
  possession play, raced into 50/50s (18 % of its time) and spent 35 % of its time in the attacking
  third against the opponent's 18 %. It was removed, and the tuned tactical layer was kept.
- **Execution was the rest of it.** Flipping into the ball was the largest single missing piece.
  Before flips, planned strikes cost more than a goal per game even when the planner chose the same
  touches as the scripted shots.
- **Planned strikes are a small gain, not a large one.** Taking the planner's strike only when it
  touches no later than the scripted shot is positive in every series but not decisively so; it is
  shipped because it never hurt and it brings the validated flip finishes and hit-model aiming.
- **Saves need good execution to pay off.** Planned saves stopped 35 % of fixture shots against
  22 % for the goal-line save, yet were neutral in matches while their clearances still ran the
  unreviewed flip timing. With the reviewed strike execution and planned strikes, they add about
  0.2 goals per game and are enabled. A save comes from, in order:
  1. a comfortable ground or aerial clearance;
  2. a planned block in the ball's path;
  3. any clearance;
  4. a best-effort block;
  5. the scripted intercept and goal-line save.

## Build and regressions

Use the .NET 8 SDK. On Linux, make the FlatBuffers generator executable first:

```sh
chmod +x src/generate-flatbuffers.sh src/flatbuffers-schema/binaries/flatc
dotnet build src/Bot/Bot.csproj --configuration Release
dotnet run --project tests/Stardust.Tests/Stardust.Tests.csproj --configuration Release
```

CI builds the bot, runs the ten regression programs in `tests/`, and runs the RocketSim
physics-model checks.

## Runtime switches

Set environment variables **before starting the bot process**:

| Variable | Default | Behavior |
|---|---|---|
| `STARDUST_GROUND_CONTROL` | enabled | Set `0` to disable catch/carry/flick selection |
| `STARDUST_AERIAL_CARRY` | enabled | Set `0` to disable aerial possession control |
| `STARDUST_FLIP_RESETS` | disabled | Set `1` to enable experimental reset attempts within an aerial carry |
| `STARDUST_TRACE` | disabled | Set `1` to log strategy transitions and ETA estimates |
| `STARDUST_TELEMETRY` | disabled | Set `1` to emit structured `STARDUST_JSON` frame/decision telemetry |
| `STARDUST_TELEMETRY_HZ` | `10` | Telemetry samples per second; clamped to 1–30 Hz |

Structured telemetry is designed for real-match debugging without per-tick console spam. Each
sampled line starts with `STARDUST_JSON ` followed by one JSON object. Decision changes emit
immediately; frame snapshots are rate-limited by `STARDUST_TELEMETRY_HZ`. Frames are recorded after
the action runs and after controller sanitization, so `controller` is the command actually returned.

## Possession control

The ground carry projects **relative** ball/car motion before placing the ball over a hood target;
advancing only the ball would inject a false position error whenever both share a high world
velocity. Throttle follows longitudinal error and ball velocity; lateral error changes the heading.
The aerial carry samples the ball prediction at a short horizon, advances the car ballistically to
the same time, and applies a velocity-matching correction with a shortest-rotation quaternion
attitude controller. A reset attempt requires adequate height, fuel, proximity, low relative speed,
an already spent flip, and separation from opponents; a confirmed reset additionally needs an own
wheels-first touch and packet flags showing the flip was restored.

## References

- Samuel Mish, [Rocket League aerial-control notes](https://www.smish.dev/rocket_league/aerial_control/):
  orientation representation, torque signs and damping used by the attitude controllers.
- [RocketSim](https://github.com/ZealanL/RocketSim) (MIT): the physics ground truth for every model
  check and the match simulator.
- RLBot v5: the [game-data schema](../src/flatbuffers-schema/schema/gamedata.fbs), including the
  persistent jump/dodge flags, and [match communication](https://wiki.rlbot.org/v5/botmaking/matchcomms/).
- [RLGym](https://rlgym.org/), Moschopoulos et al. 2023 ([Lucy-SKG](https://arxiv.org/abs/2305.15801)),
  Pleines et al. 2022 ([sim-to-sim transfer](https://arxiv.org/abs/2205.05061)): learned-policy
  approaches. They are not used here; the harness above is the evaluation half such work would need.

## Limitations

Results are from RocketSim with the simulator's generated arena, 1v1, against Stardust's own
previous versions. No external reference bot or in-game series is included. Team modes use the same
decision layer but were not separately measured in this branch.
