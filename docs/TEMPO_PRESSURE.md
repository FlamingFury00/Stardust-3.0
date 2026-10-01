# Tempo and pressure evaluation, September 2026

This change addresses three symptoms reported against the previous build: it left the opponent too
much space, it did not commit to challenge the opponent, and it waited for the opponent to move
before responding. The telemetry files the report referred to were not supplied, so the symptoms
were first turned into simulator measurements and the causes were found in the code. The result is
a measured improvement to the previous build (the base of the pull request, `b515ee5`) in the
RocketSim simulator in 3v3; in 1v1 and 2v2 it is level with it. It is not evidence that Stardust
matches professional human players: no professional replay reference was available, and Nexto still
beats it (see [Limits](#limits)).

**How much to trust the numbers.** The results below are paired, side-swapped series on independent
seeds, played on the build in this pull request after the last code change that affects play. A
1v1 game's goal difference has a standard deviation of about 2.4 goals, so a 96-game series
resolves a difference to about ±0.25 goals a game (one standard error) and a 48-game team series to
about ±0.35; most differences between builds in 1v1 are smaller than that and are reported as level.
An earlier version of this document quoted 24- and 48-game series from a runner that advanced the
seed by one per chunk while each chunk played several seeds: a "48-game" 1v1 series held 18 distinct
games and a "24-game" team series 12. Those series are not used here; the runner is fixed, and the
conclusions they supported were re-tested on fresh seeds (one of them, that the press loses in 1v1,
reversed). The drills (200 independent episodes each) are the firmer evidence for anything smaller.

## What was measured first

`match` reports gained five pressure measures, each per team while the other team has the ball. A
team has the ball for 2.5 s after its latest touch while one of its cars is within 650 uu of it, and
keeps it until they drift beyond 800 uu. The 800 uu exit radius was added before the second review:
the possession measures quoted for the series played earlier ran with the single 650 uu radius,
which counts a ball hovering at the edge as several short possessions.

| Measure | Reads as |
|---|---|
| Opponent has the ball, % of time | how long a team defends |
| Gap to ball while they have it (uu) | how far the nearest car of the defending team stands |
| Free space (nobody within 1000 uu) % | the share of the other team's possession time with no defender within 1000 uu: the "leaves the enemy too much space" symptom |
| Possessions contested % | a defender touches the ball, or comes within 400 uu of it, in the first 1.5 s of a possession |
| Response to a possession (s) | how long until a defender is closing on the ball at 600 uu/s or more (or is within 400 uu), capped at 2 s; 0 when a defender is already within 400 uu when the possession starts |

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
challenge gates use the depth only. A defender reads the ball 0.6 s ahead when it is heading for our
net (0.2 s before). **A lone defender does the same.** An earlier version kept the previous full gap
and 0.2 s lookahead for a lone defender, because a 1v1 series at the time had the press losing; that
series repeated seeds. On independent seeds the pressing lone defender beat the one that kept its
room head to head (see [1v1 series](#1v1-series)), and the special case is removed. A lone
defender's challenge margins (`SoloTieDeficit`, `SoloPressureMargin`, `SoloMargin`) are separate and
stay.

**Demolition run** (`src/Bot/Demolition.cs`). A planner and action for supersonic bumper contact
on an opponent near the ball, with at least 22 boost, that a teammate is not clearly nearer to and
that is not started, or continued, while the first man leads the race to the ball. It runs in every
match. In the lab's demolition drill (a plain chaser that wins a loose ball, 60 episodes) it
started a run in 83 % of the episodes and ended 72 % of the runs in a demolition, against 75 %
required. In traced 1v1 games it is rare (about one run in ten games) and nearly every run
demolishes; its effect on results is below what a series resolves ([Limits](#limits)).

## Results

All series are paired and side-swapped, 120 s games unless noted, on independent seeds. "Previous
build" is the base of the pull request. "Pre-activation build" is this branch before the possession
mechanics and demolition were made unconditional (commit `2bfd962`). Each series states the build
that played it; the seed ranges are in [Reproduce](#reproduce).

### Defence drill against Nexto

The drill is a 1v1: Nexto's attacks start from midfield, and "saved" is an attack that did not end
in a goal. Each cell is 200 independent attacks, so a pooled row of 400 resolves about ±2.3 points.

| Defence | seed 7300 | seed 7500 | pooled |
|---|---|---|---|
| Lookahead 0.6 s, press 0.75, covered margin −0.35 s, for a lone defender too (shipped) | 134 | 120 | 254 / 400 = 64 % |
| The same with the lone defender keeping the full gap and a 0.2 s lookahead | 138 | 133 | 271 / 400 = 68 % |
| Previous defence: lookahead 0.2 s, press 1.0, margin −0.12 s (an earlier build, 200 attacks per seed) | 143 | 133 | 276 / 400 = 69 % |

The shipped defence saves 17 fewer attacks of 400 than the lone defender that keeps its room (−4
points, about 1.3 standard errors of the difference) and 22 fewer than the previous defence on an
earlier build. The drill cannot separate the three, and it leans against the press for a lone
defender; the match series, which the drill does not model (counterattacks, the rest of the pitch),
lean the other way (see [1v1 series](#1v1-series)).

An earlier sweep on an earlier build gave a larger lookahead effect (0.2 s: 103 of 160 and 131 of
200; 0.5 s: 117 and 143; 0.7 s: 117 and 139; 1.0 s: 130 and 1.4 s: 117 of 200 on seed 7300), which is
where 0.6 s comes from. Challenge margins (`SoloTieDeficit` 0.4, `ContinueDeficit`
0.6/0.5, both) saved 145, 131 and 146 against 151 on an earlier build, and 136, 130 and 143 against
131 on another: not resolvable. They are not adopted.

### Team series against the previous build

| Series, 48 games each | Record | Goals per game | Free space (ours / theirs) | Time spent defending (ours / theirs) |
|---|---|---|---|---|
| 3v3 against the previous build | **35–13** | **+1.23** | 38.3 % / 49.6 % | 22.3 % / 26.4 % |
| 2v2 against the previous build | 25–23 | +0.23 | 43.2 % / 51.5 % | 23.3 % / 25.5 % |
| 3v3 against the pre-activation build | 26–22 | +0.33 | 39.5 % / 41.5 % | 24.1 % / 25.3 % |
| 2v2 against the pre-activation build | 24–24 | +0.06 | 41.4 % / 41.2 % | 23.8 % / 24.1 % |

In 3v3 the gain is clear: 35–13 is about 3 standard errors from level, shots are 3.84 against 2.17 per
player per 5 minutes, the gap to the ball while the opponent has it is 896 uu against 1033, and the
opponent's free space falls from 49.6 % to 38.3 %. In 2v2 the build takes the same space away
(43 % against 52 % free) and does not win more games than the previous build (25–23). The
pre-activation rows say that making the possession mechanics and demolition unconditional did not
change team results.

Against PartyCannon the team formats are lost, on games the tournament below does not reproduce:

| Series | Record | Goals per game | Notes |
|---|---|---|---|
| 3v3 against PartyCannon (46 games, 2 failed) | 13–33 | −0.65 | PartyCannon: average speed 1354 against 973 uu/s, zero boost 22 % of the time against 38 %, 21.5 big pads against 1.1 per player per 5 minutes, 30.6 against 23.1 touches; our cars spend 54 % of the time in our defensive third |
| 2v2 against PartyCannon (43 games completed) | 17–26 | −0.60 | |

In those 3v3 games a car of ours is at zero boost 52 % of the time against 27 % for PartyCannon, and
when it is empty and not the closest car to the ball a big pad is within 1800 uu of it 18 % of the
time against 66 % for PartyCannon: its cars that are off the ball stand near the pads, ours stand
where the rotation puts them.

### 1v1 series

96 games each. Free space and the other pressure measures are in the third column.

| Series | Record | Goals per game | Free space (ours / theirs) |
|---|---|---|---|
| Shipped build against the previous build | 40–56 | −0.20 | 44.4 % / 50.6 % |
| The same with the lone defender keeping its room (the earlier design) | 41–55 | −0.32 | 49.6 % / 52.3 % |
| Shipped build against the pre-activation build | 46–50 | −0.04 | 50.1 % / 50.0 % |
| Shipped build against PartyCannon | 51–45 | +0.18 | 43.3 % / 38.5 % |
| The lone defender keeping its room, against PartyCannon | 56–40 | +0.26 | 47.3 % / 38.3 % |
| **Shipped build against the lone defender that keeps its room** | **59–35 (2 draws)** | **+0.48** | 41.9 % / 49.0 % |
| The same on a second seed set | 52–44 | +0.23 | 42.9 % / 51.1 % |

The shipped build and the earlier design are level against both the previous build and PartyCannon
(differences of 0.1 goals a game, a third of a standard error). Played against each other they are
not: the pressing lone defender won 111 of 190 decided games over the two seed sets (+0.35 goals a
game, 2.0 standard errors), took the opponent's free space from 49–51 % to 42–43 % and took more
shots (6.15 against 5.11 and 5.80 against 4.52 per 5 minutes). That reverses the earlier conclusion
that the press and the 0.6 s lookahead lose in 1v1, which came from series that repeated seeds, and
it is why the lone defender now presses. The head-to-head is the most direct comparison here and
was replicated; the two third-party series and the Nexto drill (above, −4 points) do not show the
difference, so it is a moderate result.

Against the previous build 1v1 is level to slightly behind (40–56 and 41–55: −0.2 and −0.3 goals a
game, about one standard error). The two builds play almost the same 1v1 game (shots 5.7 against
5.4 per 5 minutes, zero boost 39 % of the time against 37 %, 3.6 against 3.1 big pads per 5
minutes), apart from the space the shipped build takes away (free space 44 % against 51 %), so this
is a 1v1 that has not improved, not one that has regressed. In the series against PartyCannon it
takes twice our shots (11.7 against 6.1 per 5 minutes) and spends 62 % of its time at zero boost
against our 43 %, and the score stays close.

### Tournament of the final build

Round robin of the shipped build against the field, four games per pair (two per side), 180 s
games, Bradley–Terry Elo, on seeds 22100 (1v1), 22200 (2v2) and 22300 (3v3). Four games per pair:
the ratings are ordered, not separated, and a gap under about 100 Elo is not a result. The 1v1
roster adds the pre-activation build. PartyCannon's connection timed out in some team games
(4 of the 3v3 games and 3 of the 2v2 games were excluded, a failure that belongs to that bot).

| 3v3 | Elo | W–L | Goals per game | Conceded per game | Goal difference |
|---|---|---|---|---|---|
| PartyCannon | 1812 | 10–2 | 5.75 | 2.00 | +3.75 |
| **Stardust** | **1703** | 11–4 | 3.80 | 1.93 | **+1.87** |
| Previous build | 1639 | 10–6 | 3.38 | 2.19 | +1.19 |
| Phoenix | 1389 | 5–9 | 1.36 | 2.86 | −1.50 |
| Beast | 957 | 0–15 | 0.33 | 5.07 | −4.73 |

Stardust against the previous build: +1.00 goals a game; against PartyCannon −0.67.

| 2v2 | Elo | W–L | Goals per game | Conceded per game | Goal difference |
|---|---|---|---|---|---|
| **Stardust** | **1735** | 11–3 | 4.00 | 1.71 | **+2.29** |
| PartyCannon | 1726 | 10–3 | 4.62 | 2.08 | +2.54 |
| Previous build | 1687 | 11–5 | 3.56 | 1.75 | +1.81 |
| Phoenix | 1392 | 5–10 | 1.60 | 3.33 | −1.73 |
| Beast | 960 | 0–16 | 0.75 | 5.00 | −4.25 |

Stardust against the previous build: +2.00 goals a game; against PartyCannon 0.00.

| 1v1 | Elo | W–L | Goals per game | Conceded per game | Goal difference |
|---|---|---|---|---|---|
| Nexto | 2364 | 28–0 | 14.07 | 0.57 | +13.50 |
| Necto | 1988 | 24–4 | 9.36 | 2.00 | +7.36 |
| Previous build | 1523 | 15–13 | 3.32 | 4.93 | −1.61 |
| Pre-activation build | 1523 | 15–13 | 3.36 | 5.14 | −1.79 |
| PartyCannon | 1485 | 14–14 | 3.64 | 5.96 | −2.32 |
| **Stardust** | **1409** | 12–16 | 3.61 | 5.32 | **−1.71** |
| Phoenix | 929 | 3–25 | 1.00 | 7.71 | −6.71 |
| Beast | 781 | 1–27 | 0.50 | 7.21 | −6.71 |

In 3v3 Stardust is second to PartyCannon and ahead of the previous build; in 2v2 it is first by
Elo, level with PartyCannon (0.00 head to head) and ahead of the previous build; in 1v1 it is
sixth of eight, between the previous build and the two weakest bots, and indistinguishable from the
previous build, the pre-activation build and PartyCannon (Elo 1409 against 1485–1523; head to head
+0.25, +0.25 and +0.75 goals a game). Nexto and Necto beat every other bot, Stardust included, in
every game or nearly every game, by 8 to 16 goals a game. The round robin ranks ratings inside a
thin sample; the paired series above are the measure.

### Boost

Boost per player, our build against the previous build, shipped build, 120 s games:

| Series | Average boost | Zero boost % | Big pads per 5 min | Boost used per min |
|---|---|---|---|---|
| 1v1, 96 games | 17.0 / 17.0 | 38.9 / 37.3 | 3.6 / 3.1 | 228 / 220 |
| 2v2, 48 games | 18.3 / 18.5 | 34.0 / 34.5 | 2.0 / 1.9 | 176 / 170 |
| 3v3, 48 games | 19.8 / 22.3 | 32.9 / 28.4 | 1.8 / 1.7 | 150 / 136 |

The boost economy collects what the previous build did, and it did not raise the totals (an earlier
build with a kickoff pad run did, and lost the kickoff drill). In 1v1 a car spends about 225 boost
a minute and the pads it reaches give about as much, so a 1v1 tank stays low whichever way the pads
are routed: both builds are at zero boost 37–39 % of the time, against Nexto's 24 % and 22.7 big
pads per 5 minutes to our 3. Replays of 24 1v1 games show why: a car with an empty tank is at a
median 750 uu from the ball and 2500 uu from the nearest big pad, in the thick of the play, and only
about a twentieth of its empty time (0.18 × 0.25) has the ball more than 1500 uu away, the opponent
nearer to it and a big pad within 1800 uu. There is little free time to spend on a pad.

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

Head to head against the shipped build, 1v1, 96 games each on independent seeds (21100–21400), the
variant first. None beats the shipped values; each is within about one standard error of level or
worse.

| Change (`--tune`) | Record | Goals per game | What moved |
|---|---|---|---|
| Midfield press 0.55 instead of 0.75 (`Defense.NeutralGapScale=0.55`) | 51–45 | +0.09 | free space 31 % against 42.5 %, shots 5.8 against 5.1 per 5 minutes |
| Wider carrier reach, 2200/3000 uu within 1.5/2.0 s (`Defense.NeutralCarrierRange` and the three others) | 52–44 | +0.18 | free space 41.9 % against 43.0 % |
| More eager demolition runs (`Demolition.MinValue=0`, `MinBoost=12`, `MaxRange=3400`) | 50–45 (1 draw) | +0.07 | demolitions 0.21 against 0.18 per player per 5 minutes: runs need a supersonic approach, which needs boost the car rarely has |
| More eager boost routing (`BoostEconomy.SecondsPerBoost=0.06`, `ReserveTime=0.25`, `MaxSlack=4`) | 46–50 | −0.36 | |

Team boost, head to head against the shipped build in 3v3 (48 games each) and 2v2: giving a support
car far more slack for a pad trip raised the boost it collects and did not move the score.

| Change (`--tune`) | Record | Goals per game | What moved |
|---|---|---|---|
| Support slack 4 s covered, 2 s uncovered, support reserve ×0.3, `MaxSlack` 5 s, 3v3 | 33–14 (1 draw), then 16–32 on fresh seeds: 49–46 over 96 games | +0.69, then −0.42: +0.14 pooled | average boost 21.3 and 21.5 against 18.0 and 20.0, zero boost 30 % and 29 % against 34 % and 32 %, big pads 2.3 and 2.1 against 1.7 and 1.6 |
| The same in 2v2 | 25–23 | +0.35 | |
| The support part only (no `MaxSlack`), 3v3 | 21–27 | −0.27 | |
| Support slack 8 s and 5 s, support reserve 0, `MaxSlack` 8 s, 3v3 | 23–25 | −0.12 | average boost 22.0 against 18.6, zero boost 29.6 % against 33.9 %, big pads 2.5 against 1.4 |

The first of these is the argument for replicating any single series: 33–14 was 2.8 standard errors
from level and the same change on fresh seeds lost 16–32.

Earlier experiments, each a defence drill or a 24-game series that resolves about ±0.5 goals a game,
so only the large ones mean anything:

| Change | Measure | Result |
|---|---|---|
| Boost whenever the target speed is not reached, in `Drive` and `DefensiveDrive` | defence drill 120 attacks; 24-game series against the previous build | 68 vs 75 saved; 5–18–1 (−1.25). The `Drive` switch alone: 12–12 against the candidate without it. Removed |
| `SecondsPerBoost` 0.08 with free detour 0.2 s, or 0.2 with 0.3 s (default 0.03 and 0.08 s) | 24-game 1v1 series | 8–16 (−1.21) and 12–12 |
| `ReserveTime` 0, `DepthReserve` 0.1, `SecondsPerBoost` 0.1, free detour 0.5 s, `MaxSlack` 4 s | 24-game 1v1 series | 11–13 (−0.46) |
| Planning every 0.06 s instead of 0.12 s | defence drill 160 | 108 vs 103: inside noise, twice the planning cost |
| Wider carrier reach in midfield (2200 uu, 1.4 s) | defence drill 160 | 105 vs 103 |

## Limits

- Nexto still beats Stardust in every completed game, and the defence drill still concedes more
  than a third of Nexto's attacks and clears almost none (the ball is back in our half six seconds after the
  attack, because Nexto keeps it there). This change narrows how much room and time the previous
  build gave, in the builds it is measured against, and nothing more.
- The measured gain is in 3v3 (35–13 against the previous build, +1.23 goals a game). 2v2 and 1v1
  are level with it (25–23 and 40–56) while taking the same space away, and a 1v1 car still runs at
  zero boost 39 % of the time. Against PartyCannon, which takes twice our shots, 1v1 is 51–45, but
  3v3 is 13–33 and 2v2 17–26: it is the stronger team, and plays a faster, boost-rich game.
- 96 games resolve about ±0.25 goals a game in 1v1. Anything the mechanics or the demolition run
  add to a game is smaller: they are measured by drills (`flick` 87 %, `carry` 92 %, the demolition
  drill 72 % of runs) and by the games with every one of them switched on being level with the games
  before (46–50 in 1v1, 26–22 in 3v3, 24–24 in 2v2).
- The demolition drill converts 72 % of its runs against 75 % required, and it does not say whether
  a run wins anything against an opponent that dodges.
- The press's gain in 1v1 comes from one head-to-head comparison replicated once (111–79 over 192
  games); it does not show against the previous build or PartyCannon, and the Nexto drill leans the
  other way by 4 points (about 1.3 standard errors).
- More boost does not buy goals at the scale this routing can reach: raising a 3v3 car's average tank
  from 18.6 to 22.0 and its big pads from 1.4 to 2.5 per 5 minutes left the score level. The gap to
  PartyCannon, which takes 21 big pads per player per 5 minutes and plays at 1354 uu/s against our
  973, is a different way of playing the rotation (cars parked near pads), not a routing threshold.
- A pad trip near our own net still drives with the generic `Drive`. The slack reserve grows toward
  the net, and dodges are off unless there is time for a flip, but a `DefensiveDrive`-style
  controller inside the defensive third is not built.
- The demolition planner leaves a target to a teammate that is nearer to it by flat distance,
  without knowing whether that teammate is chasing the ball or has boost.
- The possession measures are simulator measures with fixed radii and windows.
- Of the conceded goals in 24 replayed 1v1 games, 30 of 40 were struck while our car was upfield
  of the ball (a median 1160 uu beyond it) and most were struck from within 2000 uu of the line
  by a ball the car could not have reached in time. Recovering faster would need boost that the car
  does not have; this is where the remaining gap to a player who keeps the tank full is.

## Reproduce

```bash
SIM="dotnet tools/simulator/Stardust.Simulator/bin/Release/net8.0/Stardust.Simulator.dll"
# Defence drill, shipped defence and the previous one on the same build:
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300
$SIM mechanics-lab --drill defense --opponent nexto.toml --episodes 200 --seed 7300 \
  --set Defense.ReferenceLead=0.2,Defense.NeutralGapScale=1,Defense.NeutralCoveredMargin=-0.12
$SIM mechanics-lab --drill team-kickoff --opponent <previous build> --episodes 120 --seed 9100
$SIM mechanics-lab --drill demolition --episodes 60
# A paired series: 48 games use seeds 20300-20323. Two series must not share seeds unless they are
# meant to be the same games.
$SIM match --a <build> --b <previous build> --size 3 --games 48 --seconds 120 --parallel 2 --seed 20300
```

The series above used these first seeds: 1v1 against the previous build 20000, against PartyCannon
20200, against the pre-activation build 20100, head to head 20700 and 20800; 3v3 against the
previous build 20300 and the pre-activation build 20400; 2v2 20500 and 20600. A game takes
`seed + game / 2`, so a run of N games covers `N / 2` consecutive seeds; split a long series into
chunks only with a stride of `games / 2`.

`--set Type.Field=value` (or `--tune=Type.Field=value` for a bot process, which makes one build play
as several variants) sets any tuning field, such as `Defense.ReferenceLead`, `Defense.NeutralGapScale`,
`Defense.NeutralCoveredMargin`, `BoostEconomy.SecondsPerBoost` or `Demolition.MinValue`. A bad
assignment is reported and skipped. The kickoff pad run and the eager boost switches were removed with
their experiments.
