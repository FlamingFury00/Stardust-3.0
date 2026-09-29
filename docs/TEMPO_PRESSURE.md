# Tempo and pressure evaluation, September 2026

This change addresses three symptoms reported against the previous build: it left the opponent too
much space, it did not commit to challenge the opponent, and it waited for the opponent to move
before responding. The telemetry files the report referred to were not supplied, so the symptoms
were first turned into simulator measurements and the causes were found in the code. The result is
a measured improvement to the previous build (`beta` at `b515ee5`) in the RocketSim simulator. It is
not evidence that Stardust matches professional human players: no professional replay reference
was available, and Nexto and Necto still beat it (see [Limits](#limits)).

## What was measured first

`match` reports gained five pressure measures, each per team while the other team has the ball
(a team has it for 2.5 s after its latest touch while one of its cars is within 650 uu, and keeps it
until they drift beyond 800 uu):

| Measure | Reads as |
|---|---|
| Opponent has the ball, % of time | how long a team defends |
| Gap to ball while they have it (uu) | how far the nearest car of the defending team stands |
| Free space (nobody within 1000 uu) % | the "leaves the enemy too much space" symptom |
| Possessions contested % | a defender within 400 uu in the first 1.5 s of a possession |
| Response to a possession (s) | how long until a defender is closing on the ball at 600 uu/s or more |

The previous build's own numbers, as the opponent of the candidate: the ball carrier had no
defender within 1000 uu for **50–52 %** of its possessions in 1v1 (47–51 % in 3v3, 54–57 % in 2v2),
the nearest defender stood **1040–1070 uu** away, and its cars had zero boost about **40 %** of the
time in 1v1 (37–42 % across the series).

## Causes found in the code

- **Challenges were race-gated.** A first man only challenged a carrier when it would arrive first,
  which a carrier already holding the ball always wins, so midfield carries went uncontested.
- **The shadow stood 900–1425 uu off the ball at every depth**, including in midfield, where a miss
  costs a goal only much later.
- **The shadow read the ball only 0.2 s ahead**, so it steered toward where the ball had just been
  rather than where it would be when the defender arrived.
- **Boost starvation.** A refill needed a goal-side car, no pressure and, in 1v1, an opponent more
  than 1.35 s from the ball with the ball no deeper than 2500 uu; a first man in a team never
  refilled. Stardust took 1–3 big pads per 5 minutes against Nexto's 22.
- **Two support cars stacked on one spot.** The anchor rule elected every car of rank 2 and above,
  and rank 1 when it was last back or uncovered, so two cars often held the same job.

## Changes

**Boost economy** (`src/Bot/BoostEconomy.cs`, replaces `RoutePlanner` and the refill gate). Every
pad is priced in seconds of travel. A pad's worth is the integral of a marginal value that falls
as the tank fills (the fifth unit of boost saves more time than the ninetieth), so a big pad is
worth about 1.25 s to an empty tank and nothing to a full one. A pad is taken when its worth
exceeds the detour it costs and the detour fits the slack the situation leaves: the opponent's
arrival at the ball, less a reserve that grows as the ball nears our net and while the car is
recovering. Pads stay goal-side of the ball, must be lit on arrival, and are refused when an
opponent or a teammate reaches them clearly first. A trip in progress keeps its pad unless another
is 1.4 times better. The same pricing spends the time a won race leaves before a planned touch, so
a first man that beats the opponent by a second can take a pad on the way instead of braking into
the ball; that detour is capped by the teammate's ETA so a late first man does not hand the ball
over. A pad trip may only dodge when the slack covers a flip, because a speed flip cannot be
called off for 1.35 s.

**One anchor** (`Tactics.Rank`). Every car computes the same split from the same snapshot: the
first man goes for the ball, the deepest of the others anchors the net, and the remainder
support. The split is a pure function with tests for two and three cars.

**Zone- and speed-aware pressing** (`src/Bot/Defense.cs`). Near our net nothing changes. From
their half to our third the first man's gap, carrier reach, contact horizon and covered-race
margin fade toward a pressing value with `Caution(depth)`, and a ball travelling goalward faster
than about 1000 uu/s is never pressed, wherever it is. The shadow reads the ball 0.6 s ahead when
it is heading for our net.

**Demolition run** (`src/Bot/Demolition.cs`). A planner and action for supersonic bumper contact
on an opponent near the ball, at least 22 boost, that a teammate is not nearer to and that does not
pre-empt a finishing touch. Off by default (`Stardust.DemolitionRuns`); see [Limits](#limits).

## Results

All series are paired and side-swapped, against the previous build unless stated.

### Defence drill against Nexto (attacks from midfield; 160–200 shots per arm, same seed per column)

| Shadow lookahead (s) | seed 7100 | seed 7300 |
|---|---|---|
| 0.20 (previous) | 103 / 160 | 131 / 200 |
| 0.35 | 109 / 160 | |
| 0.50 | 117 / 160 | 143 / 200 |
| 0.70 | 117 / 160 | 139 / 200 |
| 1.00 | | 130 / 200 |
| 1.40 | | 117 / 200 |

Pooled over both seeds (360 shots) 0.5 s saves 72 % and 0.7 s 71 % against 65 % at 0.2 s, and 1.0 s
or more overshoots. The shipped defaults (0.6 s with the midfield press below) save **151 / 200**
on seed 7300 (75.5 % against 65.5 %). Challenge margins moved nothing that survives another seed
(`SoloTieDeficit` 0.4, `ContinueDeficit` 0.6/0.5, both: 145, 131, 146 against 151), in line with the
earlier "not resolvable" entries in [STARDUST_3.md](STARDUST_3.md).

### Team series against the previous build (24 games each)

| Build | 3v3 | 2v2 |
|---|---|---|
| Boost economy and one anchor only | 15–9 (+0.42 goals per game) | |
| Final defaults, near-net margin for a covered first man | 18–6 (+0.96) | 18–6 (+1.00) |
| **Final defaults** (midfield covered margin −0.35 s) | **21–3 (+1.63)** | **19–5 (+1.04)** |

In the final 3v3 series the opponent had the ball 19.2 % of the time against 24.8 %, our cars
spent 27.1 % of the time in their third against 16.6 %, shots were 4.22 against 1.58 per player per
5 minutes, and the opponent's free space fell from 48.7 % to 39.1 % (gap to the ball 886 uu against
1071 uu). An earlier build (0.2 s lookahead, the kickoff pad run at 2800 uu, the same press and
covered margin) won 23 of 24 (+1.88). The final build has no kickoff pad run and still wins 21–3,
so the run is not what carries the result. Each 24-game series carries roughly ±0.5 goals per game.

### 1v1 series

| Opponent | Record | Goals per game | Free space (ours / theirs) |
|---|---|---|---|
| Previous build, final 1v1 behaviour, 48 games | 22–26 | −0.13 | 43.9 % / 52.4 % |
| Previous build, boost economy only, 48 games | 27–21 | −0.08 | 49.3 % / 50.1 % |
| Previous build, midfield press 0.75, 48 games | 23–25 | −0.10 | 36.1 % / 50.6 % |

A 48-game series resolves about ±0.45 goals per game, so 1v1 is level: the press takes away part of
the free space at no measurable cost, and the boost economy does not lift the 1v1 zero-boost share
(42 %), because a 1v1 car spends about 220 boost per minute and can pick up at most about 180 at
the pad rates it reaches. The gains are in 2v2 and 3v3, which are also two of the three
tournament formats.

## Tried and rejected

| Change | Measure | Result |
|---|---|---|
| Kickoff: the back car takes its own corner's big pad | team-kickoff drill vs previous build, 120 episodes | 87.5 % without; 81.7 % at reach 2800 uu; **40.8 %** at 3300 uu, which sends the back-centre spawn (3114 uu from its pad) away from the play. Removed |
| Boost whenever the target speed is not reached (`Drive`), also in `DefensiveDrive` | defence drill 120; 24-game series | 68 vs 75 saved; 5–18 (−1.25) with both, 12–12 with `Drive` alone. Removed |
| Value boost more (0.08 or 0.2 s per unit instead of 0.03), free detour 0.2–0.3 s | 24-game 1v1 series | 8–16 (−1.21) and 12–12 |
| No time reserve for the opponent's arrival, free detour 0.5 s | 24-game 1v1 series | 11–13 (−0.46) |
| Shadow gap ×0.65 with carrier reach 1300/2000 uu at every depth | 1v1 series | free space 30 % but 12–12 against the previous build and 9–15 against PartyCannon |
| Midfield press 0.55 instead of 0.75 | 48-game 1v1 series | 20–28 (−0.42) |
| Planning every 0.06 s instead of 0.12 s | defence drill 160 | 108 vs 103: inside noise, twice the planning cost |
| Wider carrier reach in midfield (2200 uu, 1.4 s) | defence drill 160 | 105 vs 103 |

## Limits

- Nexto and Necto still win every completed game against Stardust; the defence drill still concedes
  a quarter of Nexto's attacks and clears almost none. This change narrows how much room and time
  the previous build gave, in the builds it is measured against, and nothing more.
- Demolition runs are unmeasured in full matches and stay off. The drill measures whether the run
  starts and connects against a plain chaser, not whether it wins anything against an opponent
  that dodges.
- A pad trip near our own net still drives with the generic `Drive`. The slack reserve grows toward
  the net, and dodges are off unless there is time for a flip, but a `DefensiveDrive`-style
  controller inside the defensive third is not built.
- The possession measures are simulator measures with fixed radii and windows.

## Reproduce

```bash
SIM="dotnet tools/simulator/Stardust.Simulator/bin/Release/net8.0/Stardust.Simulator.dll"
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300 --set Defense.ReferenceLead=0.2
$SIM mechanics-lab --drill team-kickoff --opponent <previous build> --episodes 120 --seed 9100
$SIM mechanics-lab --drill demolition --episodes 60
$SIM match --a <build> --b <previous build> --size 3 --games 24 --seconds 120 --parallel 2 --seed 8200
```

`--set Type.Field=value` (or `STARDUST_TUNE` for a bot process) re-creates each variant above:
`Defense.ReferenceLead`, `Defense.NeutralGapScale`, `Defense.NeutralCoveredMargin`,
`BoostEconomy.SecondsPerBoost`, `Stardust.DemolitionRuns`.
