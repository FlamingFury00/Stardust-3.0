# Tempo and pressure evaluation, September 2026

This change addresses three symptoms reported against the previous build: it left the opponent too
much space, it did not commit to challenge the opponent, and it waited for the opponent to move
before responding. The telemetry files the report referred to were not supplied, so the symptoms
were first turned into simulator measurements and the causes were found in the code. The result is
a measured improvement to the previous build (the base of the pull request, `b515ee5`) in the
RocketSim simulator, above all in 2v2 and 3v3. It is not evidence that Stardust matches
professional human players: no professional replay reference was available, and Nexto still beats
it (see [Limits](#limits)).

## What was measured first

`match` reports gained five pressure measures, each per team while the other team has the ball. A
team has the ball for 2.5 s after its latest touch while one of its cars is within 650 uu of it, and
keeps it until they drift beyond 800 uu (the 800 uu exit radius was added late: every series in
this document except the ones marked otherwise ran with the single 650 uu radius, which counts a
ball hovering at the edge as several short possessions):

| Measure | Reads as |
|---|---|
| Opponent has the ball, % of time | how long a team defends |
| Gap to ball while they have it (uu) | how far the nearest car of the defending team stands |
| Free space (nobody within 1000 uu) % | the share of the other team's possession time with no defender within 1000 uu: the "leaves the enemy too much space" symptom |
| Possessions contested % | a defender within 400 uu in the first 1.5 s of a possession |
| Response to a possession (s) | how long until a defender is closing on the ball at 600 uu/s or more |

The previous build's own numbers, in the series where it was the opponent of a candidate: free
space **49–55 %** in 1v1 (47–51 % in 3v3, 54–57 % in 2v2), the nearest defender **1010–1110 uu**
away in 1v1, and zero boost **33–42 %** of the time in 1v1.

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
opponent or a teammate reaches them clearly first (of two equal teammates the lower index takes
the pad, and a teammate with a full tank does not block it). A trip in progress keeps its pad
unless another is 1.4 times better. The same pricing spends the time a won race leaves before a
planned touch, so a first man that beats the opponent by a second can take a pad on the way instead
of braking into the ball; that detour is capped by the teammate's ETA so a late first man does not
hand the ball over. A pad trip may only dodge when the slack covers a flip, because a speed flip
cannot be called off for 1.35 s. **The economy did not raise boost totals in the final build**
([Boost](#boost)); it replaced the refill gate without losing ground, and it is not what produces
the results below.

**One anchor** (`Tactics.Rank`). Every car computes the same split from the same snapshot: the
first man goes for the ball, the deepest of the others anchors the net, and the remainder
support. The split is a pure function with tests for two and three cars. For two cars it matches
the old rule; for three the old rule could elect two anchors, and the new one elects exactly one.

**Zone- and speed-aware pressing** (`src/Bot/Defense.cs`). Near our net the first man's gap, carrier
reach, contact horizon and covered-race margin keep their previous values. From their half to our
third they fade toward a pressing value with `Caution(depth)`: the gap is 0.75 of the old one, a
carrier is met from 1500 uu (2200 uu when covered) within 1.0 s (1.5 s), and a covered first man
commits to a 50/50 he is up to 0.35 s late for (0.12 s near the net). The shadow gap also stops
closing when the ball comes at our goal faster than 1000 uu/s and is fully open at 1700 uu/s; the
challenge gates use the depth only. At every depth the shadow reads the ball 0.6 s ahead when it is
heading for our net.

**Demolition run** (`src/Bot/Demolition.cs`). A planner and action for supersonic bumper contact
on an opponent near the ball, at least 22 boost, that a teammate is not clearly nearer to and that
does not pre-empt a finishing touch. Off by default (`Stardust.DemolitionRuns`); see
[Limits](#limits).

## Results

All series are paired and side-swapped. "Previous build" is the base of the pull request. Each
series names the build that played it; builds differ in the small fixes that followed each
review, and the last section lists what each played. A goals-per-game difference between two
series carries roughly ±0.35 (one standard error; 24-game team series 0.24–0.37, 48-game 1v1
series 0.32–0.38), so a 95 % interval is about twice that.

### Defence drill against Nexto

The drill starts Nexto's attacks from midfield; each cell is 200 attacks on the final code, and
"saved" is an attack that did not end in a goal.

| Defaults | seed 7300 | seed 7500 | pooled |
|---|---|---|---|
| Shipped: lookahead 0.6 s, press 0.75, covered margin −0.35 s | 150 | 139 | 289 / 400 = 72 % |
| Lookahead 0.2 s, the rest shipped | 141 | 138 | 279 / 400 = 70 % |
| Previous defence: lookahead 0.2 s, press 1.0, margin −0.12 s | 143 | 133 | 276 / 400 = 69 % |

The shipped defaults save 13 more attacks of 400 (+3 points, about one standard error) and are
ahead on both seeds. An earlier sweep on an earlier build of the code gave a larger lookahead
effect (0.2 s: 103 of 160 and 131 of 200; 0.5 s: 117 and 143; 0.7 s: 117 and 139; 1.0 s: 130 and
1.4 s: 117 of 200 on seed 7300), which is where 0.6 s comes from: 0.5–0.7 s was the best of the
range and 1.0 s or more overshoots. On the final build the previous defaults already save 143 of
200 on seed 7300, so part of that earlier gain is now delivered by the other changes in the build,
above all the pad trips that no longer commit the car to a flip. Challenge margins
(`SoloTieDeficit` 0.4, `ContinueDeficit` 0.6/0.5, both) saved 145, 131 and 146 against 151 on an
earlier build of the final defaults, and 136, 130 and 143 against 131 on the earlier build: not
resolvable, in line with the earlier entries of [STARDUST_3.md](STARDUST_3.md). They are not
adopted.

### Team series against the previous build

| Build | 3v3 | 2v2 |
|---|---|---|
| Earlier build: boost economy, one anchor, kickoff pad run at 2800 uu, lookahead 0.2 s, no press | 15–9 (+0.42 goals per game) | |
| The same with midfield press 0.75 and covered margin −0.35 s | 23–1 (+1.88) | |
| Final code before the second review, near-net covered margin everywhere (−0.12 s) | 18–6 (+0.96) | 18–6 (+1.00) |
| The same with the shipped midfield covered margin (−0.35 s) | 21–3 (+1.63) | 19–5 (+1.04) |
| **Final code** after the second review (pad tie-break, hysteresis floor), same seeds | **18–6 (+0.83)** | **17–7 (+0.92)** |
| Final code, a second set of 3v3 seeds (8600–8603) | 18–6 (+1.58) | |
| Final code, both 3v3 seed sets pooled (48 games) | **36–12 (+1.21)** | |

The final code's first 3v3 series is 0.8 goals per game below the 21–3 series on the same seeds
(about 1.6 standard errors of the difference); the fixes between the two touch only which pad a
car takes, so the difference is most likely noise between two runs of non-deterministic bot
processes, and the second seed set gives 18–6 again. In the final code's first 3v3 series the
opponent had the ball 21.5 % of the time against 26.6 %, our cars spent 25.7 % of the time in
their third against 18.5 %, shots were 3.95 against 2.10 per player per 5 minutes, and the
opponent's free space fell from 46.3 % to 39.6 % (gap to the ball 909 uu against 1022 uu). In 2v2
the free space fell from 53.7 % to 44.3 % (983 uu against 1062 uu).

### 1v1 series

| Opponent and build | Record | Goals per game | Free space (ours / theirs) |
|---|---|---|---|
| Previous build; final 1v1 behaviour, 48 games | 22–26 | −0.13 | 43.9 % / 52.4 % |
| Previous build; boost economy only, 48 games | 27–21 | −0.08 | 49.3 % / 50.1 % |
| Previous build; midfield press 0.75, 48 games | 23–25 | −0.10 | 36.1 % / 50.6 % |
| PartyCannon; final code before the second review, 48 games | 22–26 | −0.25 | 42.0 % / 35.6 % |

The previous build scored 18–30 (−0.65) and 16–8 (+0.54) against PartyCannon on two seeds, 34–38
pooled; on the seed the press builds shared (6000) the boost-economy build won 27–21, the 0.75
press 23–25 and the 0.55 press 21–27 against the previous build's 18–30. The last build was
played on one seed (8400) only, so its comparison is not paired. PartyCannon takes twice our shots
(10.1 against 5.0 per player per 5 minutes) and spends 31 % of its time in its attacking third
against our 21 %, yet the score stays close (goals per 5 minutes 5.1 against 4.6; saves 4.8
against 1.9). A 48-game series resolves about ±0.35 goals per game, so 1v1 is level: the press
takes away part of the free space at no measurable cost.

### Tournament of the final code

Round robin, four games per pair (two per side), 180 s games, Bradley–Terry Elo. Only four games
per pair: the ratings are ordered, not separated, and a gap under about 100 Elo is not a result.
The roster is the previous build, PartyCannon, Phoenix and Beast, plus Nexto, Necto and NoobBlack in 1v1.

| 3v3 | Elo | W–L | Goal difference per game | against the previous build |
|---|---|---|---|---|
| PartyCannon | 1899 | 12–1 | +3.31 | +2.67 |
| **Stardust** | **1759** | 12–3 | +2.60 | **+3.25** |
| Phoenix | 1478 | 7–8 | −1.13 | −0.50 |
| Previous build | 1422 | 6–9 | −0.20 | |
| Beast | 942 | 0–16 | −3.88 | −4.00 |

| 2v2 | Elo | W–L | Goal difference per game | against the previous build |
|---|---|---|---|---|
| PartyCannon | 1826 | 13–3 | +3.25 | +1.50 |
| Previous build | 1772 | 12–4 | +2.50 | |
| **Stardust** | **1720** | 11–5 | +2.31 | **+0.75** |
| Phoenix | 1283 | 4–12 | −2.00 | −4.50 |
| Beast | 898 | 0–16 | −6.06 | −7.75 |

In 3v3 Stardust is second, 337 Elo above the previous build, and beats it by 3.25 goals a game;
PartyCannon is ahead (Stardust −0.33 goals a game against it, one win and two losses; a fourth
game was excluded when PartyCannon's connection timed out with Stardust ahead 2–1). In 2v2 the
previous build and Stardust are level (Stardust ahead head to head by 0.75 goals, 52 Elo behind in
the table, four games each), which the 24-game paired series above resolves better: 17–7.
PartyCannon leads in both. The 3v3 round robin excluded three games in which PartyCannon's
connection timed out, a failure that belongs to that bot.

| 1v1 | Elo | W–L | Goal difference per game | against the previous build |
|---|---|---|---|---|
| Nexto | 2344 | 24–0 | +13.83 | +12.00 |
| Necto | 1960 | 20–4 | +6.58 | +8.75 |
| Previous build | 1546 | 13–11 | −1.25 | |
| PartyCannon | 1500 | 12–12 | −2.42 | −0.75 |
| **Stardust** | **1454** | 11–13 | −2.25 | **−1.00** |
| Phoenix | 1040 | 4–20 | −7.25 | −5.25 |
| Beast | 656 | 0–24 | −7.25 | −6.25 |

NoobBlack exited during startup in every game (28 games in the round robin failed and are
excluded), so it has no rating and sits at the 1500 default. In 1v1 Stardust is fifth of seven
rated bots, 92 Elo behind the previous build (1–3 on four games, −1.00 goals a game) and level
with PartyCannon (2–2); it beats
Phoenix and Beast 4–0 each and loses 0–4 to Nexto (−13.75) and to Necto (−7.25), as every other
bot does. Excluding Nexto and Necto it is 11–5. The 48-game paired 1v1 series above are the better
measure against the previous build and are level; nothing in these tables says that 1v1 improved.

### Boost

Boost per player, our build against the previous build:

| Series | Average boost | Zero boost % | Big pads per 5 min |
|---|---|---|---|
| 1v1, final 1v1 behaviour | 13 / 17 | 42.1 / 37.4 | 2.4 / 3.5 |
| 2v2, final code, margin −0.12 s | 19 / 19 | | 2.2 / 2.6 |
| 3v3, final code, margin −0.12 s | 21 / 21 | 32.3 / 29.5 | 1.6 / 1.7 |
| 3v3, final code, margin −0.35 s | 19 / 21 | 33.4 / 29.5 | 1.9 / 1.5 |
| 3v3, earlier build with the kickoff pad run | 27 / 21 | 28.9 / 29.6 | 3.7 / 1.2 |

Only the earlier build, with the kickoff pad run, collected more boost; the final code collects
what the previous build did. A 1v1 car spends about 220 boost per minute and the pads it reaches
give about 180, so a 1v1 tank stays low whichever way the pads are routed.

### Kickoff drill

The 2v2 kickoff against the previous build (120 episodes, seed 9100; success is a goal or a
follow-up touch without conceding within ten seconds):

| Kickoff behaviour of the back car | Success | Conceded within 10 s |
|---|---|---|
| Cheat to midfield (shipped) | 105 / 120 = 87.5 % | 0.10 |
| Take its own corner's big pad, reach 2800 uu | 98 / 120 = 81.7 % | 0.14 |
| The same, reach 3300 uu (the back-centre spawn included) | 49 / 120 = 40.8 % | 0.55 |

The 95 % intervals of the first two rows overlap (80–92 % and 74–88 %); the third is clearly worse,
because the back-centre spawn is 3114 uu from its pad and leaves the play. A run that shows no
benefit at one reach and fails at another was removed rather than kept.

## Tried and rejected

| Change | Measure | Result |
|---|---|---|
| Boost whenever the target speed is not reached, in `Drive` and `DefensiveDrive` | defence drill 120 attacks; 24-game series against the previous build | 68 vs 75 saved; 5–18–1 (−1.25). The `Drive` switch alone: 12–12 against the candidate without it. Removed |
| `SecondsPerBoost` 0.08 with free detour 0.2 s, or 0.2 with 0.3 s (default 0.03 and 0.08 s) | 24-game 1v1 series | 8–16 (−1.21) and 12–12 |
| `ReserveTime` 0, `DepthReserve` 0.1, `SecondsPerBoost` 0.1, free detour 0.5 s, `MaxSlack` 4 s | 24-game 1v1 series | 11–13 (−0.46) |
| Shadow gap ×0.65 (floor 380 uu) with carrier reach 1300/2000 uu at every depth | 1v1 series | free space 30 %, 12–12 against the previous build (+0.17) and 9–15 against PartyCannon (−0.46) |
| Midfield press 0.55 instead of 0.75 | 1v1 series against the previous build | 12–12 (24 games), then 20–28 (−0.42, 48 games) |
| Planning every 0.06 s instead of 0.12 s | defence drill 160 | 108 vs 103: inside noise, twice the planning cost |
| Wider carrier reach in midfield (2200 uu, 1.4 s) | defence drill 160 | 105 vs 103 |

## Limits

- Nexto still beats Stardust in every completed game, and the defence drill still concedes a
  quarter of Nexto's attacks and clears almost none. This change narrows how much room and time the
  previous build gave, in the builds it is measured against, and nothing more.
- 1v1 is level with the previous build and with PartyCannon. The measured gains are in 2v2 and 3v3.
- The covered midfield margin (−0.35 s) was adopted on 21–3 against 18–6 in 3v3, about 1.4 standard
  errors, level in 2v2, and the earlier 23–1. It cannot act in 1v1 (it needs a covering teammate).
- Demolition runs are off. Its drill (a plain chaser that wins a loose ball, 60 episodes) starts a
  run in 83 % of the episodes and ends 72 % of the runs in a demolition (36 of 50), against 75 %
  required, without conceding; before the review's changes it converted 26–50 %. The drill does not
  say whether a run wins anything against an opponent that dodges.
- A pad trip near our own net still drives with the generic `Drive`. The slack reserve grows toward
  the net, and dodges are off unless there is time for a flip, but a `DefensiveDrive`-style
  controller inside the defensive third is not built.
- The demolition planner leaves a target to a teammate that is nearer to it by flat distance,
  without knowing whether that teammate is chasing the ball or has boost.
- The possession measures are simulator measures with fixed radii and windows.

## Reproduce

```bash
SIM="dotnet tools/simulator/Stardust.Simulator/bin/Release/net8.0/Stardust.Simulator.dll"
# The defence defaults, and the previous ones on the same build:
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300 \
  --set Defense.ReferenceLead=0.2,Defense.NeutralGapScale=1,Defense.NeutralCoveredMargin=-0.12
$SIM mechanics-lab --drill team-kickoff --opponent <previous build> --episodes 120 --seed 9100
$SIM mechanics-lab --drill demolition --episodes 60
$SIM match --a <build> --b <previous build> --size 3 --games 24 --seconds 120 --parallel 2 --seed 8200
```

`--set Type.Field=value` (or `STARDUST_TUNE` for a bot process) sets any variant that still exists:
`Defense.ReferenceLead`, `Defense.NeutralGapScale`, `Defense.NeutralCoveredMargin`,
`BoostEconomy.SecondsPerBoost`, `Stardust.DemolitionRuns`. The kickoff pad run and the eager boost
switches were removed with their experiments.
