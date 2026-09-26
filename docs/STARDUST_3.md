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
| Evaluation | `tools/simulator` | RocketSim match simulator speaking the RLBot v5 protocol: paired series, round-robin tournaments against RLBot v5 bot-pack bots, scenario fixtures, physics-model checks, and a mechanics lab that runs the bot in-process on measured drills |

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

# Round robin between bot builds and RLBot v5 bot configs (label = path per roster line)
$SIM tournament --roster roster.txt --games 6 --seconds 180 --parallel 3 --out tournament

# Loose balls in our third (clear), slow balls rolling into our goal (roller), Nexto's attacks
# (defense), kickoffs (kickoff-follow) and a time profile (profile) are extra drills, run by name.
# Mechanics drills with the bot in-process; --trace N prints episode N tick by tick,
# --set Type.Field=value (any command) overrides a tuning field (as STARDUST_TUNE does in a match)
$SIM mechanics-lab --drill all --episodes 60
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 60
$SIM mechanics-lab --drill profile --opponent <bot> --episodes 12

# Flick programs from settled carries, scored for robustness
$SIM flick-search --spots 10,30,50
```

A bot is a .NET build directory, an entry assembly, or an RLBot v5 bot config (`*.toml`), which the
simulator starts with its Linux run command. The mechanics lab runs the production `Stardust` class
in-process on the simulator's packets. A drill can direct which action runs, or let the full bot
play; every drill states the pass criteria a pro execution should meet, and the command exits
non-zero when one fails.

Series are paired: every seed is played twice with the builds swapping colours. Kickoff spawns are
jittered by a few units so deterministic bots do not replay one identical game. Scenario episodes
are generated from a seed, so two builds face identical fixtures. With `STARDUST_TRACE=1` the bot
logs its decisions, and the series report then attributes every goal to the decision the conceding
car was running half a second before it (own goals marked), next to each build's time share per
decision.

A 72-game 1v1 series resolves about ±0.4 goals per game (one standard error), and even 288 games
only resolve about ±0.2, so changes are judged on repeated series with fresh seeds rather than one
lucky run.

**Arena fidelity.** The generated arena is checked too (`physics-check --model arena`): an idle car
anywhere on the flat floor, the goal mouths sampled densely, must stay at rest height without
touching anything. An earlier mesh failed it in 74 of 396 placements. Where a corner fillet meets
the back wall, its vertices were placed where two nearly parallel offset lines crossed, far along
the wall, and the back-wall floor ramp spread across both goal mouths. A car in front of the goal
line was lifted up to 45 uu and spun. Every goal-mouth play in earlier runs, for both sides, was on
that bump. Fixture cars are also placed with nothing of their previous motion left over; a jump
still held from the last episode used to swallow the first jump of the next, because the game only
jumps on a press.

**Kickoff spawns.** RocketSim shuffles the five 1v1 spawns with a linear congruential engine seeded
directly by the kickoff seed, and consecutive seeds, one per kickoff of a game, shuffled almost
alike. Within a game one spawn repeated for several kickoffs in a row (sequences like
`DDDDOODD`); a run of 300 consecutive seeds drew no diagonal kickoff at all, 81 % off-centre, and
repeated the previous spawn 62 % of the time. Pooled over many games the mix still came out near the
true 40 / 40 / 20 %, so series results stand, but single games were lopsided. Seeds now pass through
a SplitMix64 finaliser, and `physics-check --model kickoff` checks the mix (42 / 35 / 23 %, repeat
rate 0.32 against 0.36 by chance).

## Results

All figures are 1v1, 180-second games unless noted, goals per game from the candidate's point of view.
Series before the arena fix (see [Evaluation harness](#evaluation-harness)) played both builds on the
same flawed goal mouths, so their comparisons stand but not their absolute numbers.

| Candidate | Opponent | Games | Goals per game |
|---|---|---|---|
| Decision layer + kickoff fix | previous release | 36 | **+0.81** |
| Physics-first "Brain" decision layer (removed) | previous release | 36 | −0.67 to −0.75 |
| Kickoff fix + planner strikes replacing scripted shots up to 0.3 s later | previous release | 36 | −1.1 before flips, −0.19 with flips |
| Kickoff fix + planner strikes when no later than the scripted shot | kickoff fix | 72 + 72 + 144 | +0.40, +0.11, +0.03 (pooled +0.14 ± 0.19) |
| **Kickoff fix + planner strikes + planned saves (shipped)** | kickoff fix | 72 + 144 | +0.61, +0.22 (pooled **+0.35 ± 0.22**) |
| Kickoff fix + planned aerials only | kickoff fix | 72 | +0.14 |
| Kickoff fix + planned saves only, before the flip-execution fixes | kickoff fix | 72 | −0.06 |
| Possession rework: carry, catch, first flicks, stale-touch fix | shipped build before it | 96 (300 s) | +0.39 |
| Possession rework with flick decisions and catch fixes | shipped build before the rework | 96 | **+0.77** (61-35) |
| Air dribbles and flip resets switched on | the same build | 96 | −0.03 (left off) |
| Refuelling from pads on defensive routes | the same build | 53 of 96, stopped | −0.58 (not shipped) |
| **Saves rework, air-control roll fix, jump release** (fixed arena) | the build before them | 144 | +0.06 (70-74): level |
| Roll fix and jump release alone | the build before them | 72 | +0.07 |
| Jump blocks timed as racing to the point vs coasting from takeoff | each other | 96 | ±0.10: level |

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

## Tournament against the RLBot v5 bot pack

Stardust plays paired, side-swapped series (6 games of 180 s each) against eleven bots from the
[RLBot v5 bot pack](https://github.com/RLBot/botpack), under the upstream v5 schema, on the fixed
arena (see [Evaluation harness](#evaluation-harness)). The learned bots (Nexto, Necto, Element,
TensorBot, Wisp, Willo) run their own networks in-process, pinned to one thread each. Nexto has
its own, longer series (see [Against Nexto](#against-nexto-where-the-goals-come-from)).

| Opponent | Kind | W-L-D | Goals per game |
|---|---|---|---|
| Nexto | RLGym | 0-12-0 | −12.92 (12 games) |
| Wisp | RLGym + GGL | 0-4-0 | −10.50 (two games failed to start: the bot timed out loading) |
| Element | RLGym | 0-6-0 | −9.33 |
| Necto | RLGym | 0-6-0 | −8.83 |
| Party Cannon | scripted, C# | 3-3-0 | −0.17 |
| Willo | RLGym + GGL | 5-1-0 | +1.17 |
| Phoenix | scripted, C# | 5-1-0 | +4.67 |
| Noob Black | scripted, Python | 6-0-0 | +4.83 |
| TensorBot | learned | 6-0-0 | +5.17 |
| Beast | scripted, Python | 6-0-0 | +5.83 |
| Mirror | scripted, Python | 6-0-0 | +16.33 (touches the ball 7.7 times per 5 min: it barely plays) |

Bowie Knife does not function in the simulator and is left out. Party Cannon, a scripted C# bot on
RedUtils like Stardust, is level with it: it takes 14.9 shots per 5 minutes to Stardust's 6.2, and
0.99 of Stardust's goals against per 5 minutes are its own touches.

The first round, on the arena before its fix, gave the same picture: Stardust beat the scripted
bots and TensorBot and lost heavily to the strong learned bots. Its match statistics show where:

- **Speed and boost.** Stardust averages 1063 uu/s against Nexto's 1186 and Wisp's 1515. It collects
  211 boost per minute against 379–519, and sits at zero boost 39 % of the time.
- **Passive defence.** Against Nexto, Stardust spends 45 % of its time on the goal-line save and
  never refuels. In the `defense` drill, 36–52 % of Nexto's attacks from midfield score within 6 s.
  The trace shows the cause: the solo shadow drives out at an attack coming at speed, is passed,
  and reverses back to the line.
- **Kickoffs are not the leak.** Stardust wins the first touch on every kickoff against Nexto and the
  ball is in Nexto's half three seconds later 70 % of the time. Still, 20 of Nexto's 58 goals come
  within 10 s of a kickoff, from the play that follows.

## Against Nexto: where the goals come from

With the arena fixed, Nexto beats Stardust by about 14.5 goals per game (head: 2–174 over 12 games;
the build before the saves rework: 2–175, so the rework did not cost it). Replays (`match
--replays`), the decision log and the lab traces give a consistent picture:

- **Our kickoff touch is often our last.** In most goals the last Stardust touch before it was the
  kickoff itself, often a won one that sent the ball into Nexto's half at 2000–3000 uu/s. Nexto then
  collected it, carried it up and scored 5–8 s later without Stardust touching the ball again.
- **The saves were out of reach when they began.** At the moment the goal-line save starts, the car
  is typically 2000–5000 uu from the point where the ball will cross, at zero boost.
- **Nexto carries the ball in.** It dribbles at up to 2300 uu/s, boosting as it goes. The shadow runs
  beside the ball on the inside line, is outpaced once its boost is gone, and the block that follows
  races parallel to the ball without reaching it. The challenge gate is an arrival race, which a
  carrier always wins, so a solo defender never steps into a dribble in midfield.
- **Boost.** Nexto takes about 22 big pads per 5 minutes, Stardust 1–3. Half of Nexto's are the
  midfield pads beside the play, a third are Stardust's own corner pads, taken while attacking.

Each of these led to a change, measured in the `defense` drill (Nexto attacks from midfield; ≥ 100
episodes per arm, since 40 proved too few), in self-play against the unchanged build, or against
Nexto. None survived:

| Change | Measure | Result |
|---|---|---|
| Refuel whenever the route through a pad still beats Nexto's quickest shot home | self-play, 63 games; vs Nexto, 12 | −0.27 ± 0.34; −14.9 vs −14.3 (more boost, 14 vs 9 average, same goals) |
| Final kickoff dodge turned 0.25 rad away from the opponent's car | kickoff drill vs Nexto, 60; self-play kickoffs | kickoffs won 47 % vs 33 % against Nexto, but a mirror opponent gets the first touch 52 to 29, so it loses in self-play |
| Final dodge later (450–600 uu), none at all, or along the approach | kickoff drill vs Nexto, 60 each | worse or level; without the dodge every kickoff is lost |
| Final dodge held 0.1 s instead of 0.18 s, hitting the ball lower | self-play, 24 games | −1.33 ± 0.65: ball-side advantage after the kickoff 75 to 126 |
| Shadow holds its depth against a fast attack instead of stepping up | defense drill, 40 | 21 vs 15 conceded |
| Meet a carried ball with a clearance before a block | defense drill, 40 + 60 | 29 vs 32 per 100 conceded: level |
| Challenge a carrier whenever its path is reachable within 0.8 s | defense drill, 100 | 36 vs 35: level |
| Plan saves on a carried-ball path that speeds up with the carrier's boost | defense drill, 2 × 100 | 76 vs 73 per 200: level |
| Shadow boosts to catch up when 350 uu/s short of its target speed | self-play, 18 games | −1.78 ± 0.53 |
| 2v2: support car refills below 60 boost, big pads below 50, behind any ball in their half | 2v2 self-play, 40 games | −0.60 ± 0.45 |
| Last-line aerial save, any direction, reached 0.05 s early | save drill, 200 shots; self-play, 48 games | 127 vs 125 saved; −0.17 ± 0.41 (left off) |
| The same aerial accepting any simulated touch, without the early reach | save drill, 200 shots | 124 vs 125 |
| Block car turned nose-up after its last jump when contact is above 350 / 450 uu | save drill, 200 shots | 126 / 122 vs 125: height gained, width lost |
| Solo shadow aimed at the front post (half / full blend) | defense drill, 100 each | 27 / 37 vs 31 conceded |
| Solo challenges up to 0.3–0.6 s late | vs Nexto, 12 games on one seed | −13.50 ± 0.85 against −12.92 ± 0.56 |
| Defensive-half touches: planned strike accepted up to 0.25 s later, and/or planned as a clearance past 1500 uu deep | clear drill, 150 each | 42–44 % vs 47 % cleared (harder touches, 1339 vs 1191 uu/s, but aimed wide) |
| 2v2: a covered first man challenges up to 0.35 s late | 2v2 self-play, 72 games | +0.06 ± 0.30 |
| No new ground catch or carry deeper than 1500 uu in our half | clear drill, 150; vs Nexto, 12 on one seed | 52 % vs 47 % cleared; −13.67 ± 0.71 against −12.92 ± 0.56 |
| Aerial climb turning the nose by the quickest swing, roof left free | save drill, 200; aerial planner on the missed saves | 124 vs 125; best simulated miss 142 vs 152 uu: the flights are short, not slow to turn |
| Stricter or looser solo challenge margins | vs Nexto, 12 games each; self-play, 51 | −13.75 and −14.25 against −14.3 to −14.9: not resolvable; stricter −0.24 ± 0.45 |
| Shadow matches the attacker's full speed, up to 1800 uu/s (instead of 0.8 of it, up to 1350) | self-play, 85 games | +0.07 ± 0.29 |

The drill baseline is 35–38 % of attacks conceded within 6 s, against a defence that starts
goal-side with 20–100 boost. The gap to Nexto is not one misjudged threshold: each rule above fixes
the moment it was built for and gives the time back elsewhere.

## Build and regressions

Use the .NET 8 SDK. On Linux, make the FlatBuffers generator executable first:

```sh
chmod +x src/generate-flatbuffers.sh src/flatbuffers-schema/binaries/flatc
dotnet build src/Bot/Bot.csproj --configuration Release
dotnet run --project tests/Stardust.Tests/Stardust.Tests.csproj --configuration Release
```

CI builds the bot, runs the ten regression programs in `tests/`, and runs the RocketSim
physics-model checks.

`Stardust.bot.toml` runs that local build: RLBot starts `Bot.exe` on Windows and `Bot` on Linux.
For the bot pack, `bob.toml` builds the `dockerfile`, which publishes a native (AOT) Linux binary and
a self-contained single-file Windows executable; bob writes both into the packaged bot config.
The native build plays identically to the JIT build (the same matches, frame for frame, in the
simulator). To check the packaging without Docker, run the dockerfile's two `dotnet publish`
commands from `src/Bot`.

## Runtime switches

Set environment variables **before starting the bot process**:

| Variable | Default | Behavior |
|---|---|---|
| `STARDUST_GROUND_CONTROL` | enabled | Set `0` to disable catch/carry/flick selection |
| `STARDUST_AERIAL_CARRY` | enabled | Set `0` to disable aerial possession control |
| `STARDUST_FLIP_RESETS` | disabled | Set `1` to let an aerial carry go for a flip reset (lab: 72 % acquired, 51 % used) |
| `STARDUST_AIR_DRIBBLES` | disabled | Set `1` to pop a controlled hood carry into an air dribble (lab: 60/60 set up, 1.8 s carried; no match gain, see Results) |
| `STARDUST_TRACE` | disabled | Set `1` to log strategy transitions and ETA estimates |
| `STARDUST_TRACE_SAVES` | disabled | Set `1` to also log the clearance and block weighed on every emergency planning tick |
| `STARDUST_TUNE` | unset | `Type.Field=value,...` overrides tuning fields (e.g. `Kickoff.DodgeJump=0.1`), so one build plays as several variants; a bot config whose run command sets it is a variant the `match` command can play. JIT builds only: the native build ignores it |
| `STARDUST_TELEMETRY` | enabled | Structured `STARDUST_JSON` frame/decision telemetry, written to `logs/` next to the executable; set `0` to disable. Test and probe instances stay quiet unless it is set; the simulator sets `0` for bots it starts unless it is set in its own environment |
| `STARDUST_TELEMETRY_HZ` | `10` | Telemetry samples per second; clamped to 1–30 Hz |
| `STARDUST_TELEMETRY_FILE` | `logs/stardust-telemetry-<time>-pid<pid>.jsonl` | Telemetry file; if it cannot be opened the bot falls back to the system temp directory, then to the console |
| `STARDUST_TELEMETRY_CONSOLE` | disabled | Set `1` to also print every telemetry line |

Structured telemetry is designed for real-match debugging without per-tick console spam. Each
line is one JSON object (prefixed with `STARDUST_JSON ` on the console). Decision changes emit
immediately; frame snapshots are rate-limited by `STARDUST_TELEMETRY_HZ`. Frames are recorded after
the action runs and after controller sanitization, so `controller` is the command actually returned.
A file rotates at 64 MB, and on opening one the bot deletes the oldest telemetry files in that
directory beyond 256 MB, so a bot that plays many matches does not fill the disk.

## Possession mechanics

Every possession mechanic has a drill in the mechanics lab (see [Evaluation harness](#evaluation-harness)),
with the pass criteria a pro execution should meet. Numbers are lab results, shipped build against
the mechanics before this rework:

| Drill | Before | Now |
|---|---|---|
| `carry`: keep a balanced ball 4 s while turning onto the lane | 3/60 held | 141/150 held (120/120 from a rolling start), lane error p90 1.9° |
| `flick`: power flick from a 900–1500 uu/s carry | median 1528 uu/s, aim error 11° | median 2177 uu/s, aim error 1.3° |
| `catch`: cushion a dropping ball and settle it for 1 s | 5/60 settled | 18/22 committed catches settle |
| `dribble-duel`: carry against a defender who challenges, shadows or chases | flicked into 76 % of shadowing defenders | good outcome 58 % vs challengers, 85 % vs shadows, 76 % vs chasers; ball lost 17 % |
| `flip-reset`: regain the flip on a high ball and use it | 8 % acquired, never used | 72 % acquired, 100 % of those confirmed, 51 % used on the ball |
| `air-dribble`: keep a nose-carried ball in reach in the air | — | median 2.6 s carried at 1390 uu/s toward goal |

- **Hood carry** (`HoodCarry`). The ball's velocity is the slow state of a carry: the roof can only
  nudge it through friction, so the car stays underneath. Position and velocity errors of the ball
  relative to a spot just over the car origin become forward and sideways car accelerations. The
  forward part is realised by `SpeedActuator`, which dithers throttle, coasting (−525 uu/s²), braking
  (any reverse throttle is a full −3500 uu/s²) and boost (0.1 s minimum) tick by tick so the average
  matches the request. The spot sits over the origin because Rocket League's extra hit impulse points
  from the origin to the ball: a ball carried further forward is pushed away on every contact.
  Moving the spot sideways uses that same impulse to steer the ball onto the lane.
- **Catch** (`GroundCatch`). Commits to one descent to roof height and re-reads it every tick,
  arrives through the navigator's timed, directed arrival at the ball's speed, and hands the settled
  ball straight to the carry. The heading tolerance comes from the ball's horizontal speed, since a
  heading error *a* costs 2·v·sin(*a*/2) of sideways slip.
- **Flicks** (`Flick`). Recipes come from `flick-search`, which runs open-loop jump, tilt and dodge
  programs in RocketSim from carries settled by the bot's own controller. It scores each program on
  its 10th-percentile gain when the ball sits up to 6 uu off its spot. The flick turns the carry
  onto the aim, centres the ball on the recipe's spot, then runs the program with no flip cancel.
  The power flick (ball 50 uu forward, nose-down rolling jump, diagonal front flip) adds 1000 uu/s at
  p10 and leaves 20° up. Back-flip lobs reach 35–48° but only with the ball within 3 uu of the
  centre line, which a carry does not hold, so they are not used.
- **When to flick** (`PossessionControl.PlanFlick`). The dribbler reads the most imminent opponent:
  its time to the ball at its closing speed and where it comes from. It flicks when a challenger
  commits from the front: a power flick 0.25–0.45 s before contact (the 20° climb clears the
  challenger's roof), a lob closer in. It also flicks at the goal from within 3000 uu when no
  defender can reach the line. It keeps carrying against a defender who hangs back or a chaser from
  behind, with the ball already on the flick's spot once a challenger is on the way.
- **Flip resets** (`FlipReset`, behind `STARDUST_FLIP_RESETS=1`). In RocketSim the wheels are
  suspension rays that also land on the ball. Three touching wheels count as grounded, which clears
  the used jump and flip, and they leave no ball touch. So the reset is confirmed from the packet
  flags alone: the flip was unavailable while airborne, then jump, double jump and dodge are all
  cleared high up. The approach closes in nose first and presents the underside for the last
  0.45 s. The regained flip is then fired into the ball from behind it along the goal line.
- **Stale touches.** The packet keeps every car's latest touch, however old. After a reset the bot
  used to treat that old touch as new, and as its own if it had made it, which cancelled or misled
  actions right after every kickoff. Touches are now baselined at each reset.

## Saves

The `save` drill plays the scenario suite's 200 seeded shots (1500–3200 uu/s, up to 560 uu high at
the line, the defender in net, at a post, or rotating back at speed) with the full bot in-process.
The same seeds were played by the strongest learned bots through the scenario runner:

| | Saved |
|---|---|
| Stardust before this work | 58/200 (29 %) |
| **Stardust now** | **126/200 (63 %)** |
| Necto | 128/200 (64 %) |
| Nexto | 144/200 (72 %) |

The drill records, for a ball that gets past untouched, which side of the car it passed and by how
much. That pointed at each of these in turn:

- **The block's approach** (`Block`). It parked on its point when an unrealistically fast drive said
  there was time, arrived late, and then jumped from wherever it was. It now parks only when a
  stopping approach settles before takeoff by its own rollout. Otherwise it drives through the
  point: flat out while tight, at a steady pace of path over time while early. The car cannot speed
  up once it has jumped, so the planner and the block both time a jump block as flat out until
  takeoff and then a straight coast at the takeoff velocity, which must pass within 100 uu of the
  point.
- **Where and how the car stands** (`BlockPlanner.BlockPoint`). A jump block is flown open loop
  for up to a second, so the car centres on the ball's track rather than standing 80 uu off it,
  and turns its length (118 uu, not its 84 uu width) across the ball's path in the air.
- **A steep aerial that tumbled** (`AirControl`). Within 11° of vertical the roof target switched
  to world +y, so a car facing +y climbing steeply was asked for a half roll mid-takeoff. It
  tumbled without ever boosting, and the planner, which simulates the same law, wrote off those
  aerials. Near the roof hint, the roof the shortest nose swing leaves now takes over.
- **Swallowed takeoffs** (`RUBot`). The game jumps only on a press. An action taking over from one
  that held jump lost its takeoff, so a jump held since the last tick that started nothing is
  released for one tick. It keys off the input the game reports it applied, not the bot's own last
  output, so a press still in flight under input latency is never mistaken for a stale hold.

Still open: shots that no block reaches in time. These are the rotating-back cases, where the car
races alongside the ball into its own net. Nexto saves 21 of 69 such shots and Stardust 7. Turning
the car's roof to the ball after a double jump (a 118 × 84 face instead of a graze) was tried; the
longer flight it needs is rarely on time, and it changed nothing measurable.

In matches the rework is level with the build before it (+0.06 goals per game over 144 games).
With decisions traced, the series report shows why: between Stardust builds, about 55 % of goals
are conceded during the goal-line save and 35 % during a block, both last-line states taking a few
per cent of the time. The drill's gains are on shots a block reaches; the goals that decide matches
come from attacks that beat the defence before any block is possible.

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

Results are from RocketSim with the simulator's generated arena, 1v1. The external reference is
the RLBot v5 bot pack in the same simulator; no in-game series is included. Team modes use the same
decision layer but were not separately measured in this branch. The strong learned bots remain far
ahead: closing that gap is a matter of speed, boost economy and defensive decisions, not of the
possession mechanics measured above.
