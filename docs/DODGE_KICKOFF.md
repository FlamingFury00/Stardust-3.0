# Dodge impulse and kickoff follow-through — September 2026

The generic dodge controller applied forward/backward speed compensation to yaw and lateral
compensation to pitch. Backward-dodge detection also read yaw. Mirrored approaches consequently
produced different impulses. `Dodge` now uses the existing, validated `DodgeModel.InputToward`
and explicitly assigns its named pitch/yaw outputs. Jump timing, kickoff dodge distance and
jump hold remain unchanged.

The shared boost action also now finishes safely when there is no eligible pad. It no longer
indexes an unselected `-1` or silently chooses pad zero from an empty candidate set. It cancels
before executing a stale route when field data or its public pad index changes. Copied candidates
are matched to the current field, and availability comes from the live pad.

These are verified correctness changes. They do not establish professional-level play.

## Physics and regression evidence

The new action-level unit regression fails on the old controller and passes with the fix. It
checks mirrored controls and intended impulses at forward/reverse speeds and several headings.
Existing jump press/release checks still pass at 120, 60, 30 and 15 Hz.

The native `dodge-direction` drill invokes the real action from 120 airborne states comprising
60 mirrored pairs. It samples speeds -900, 0, 1000 and 2000 uu/s and compares the first physics
tick with the model's predicted velocity, including the speed cap.

| Native measurement | Old controller | Corrected controller |
|---|---:|---:|
| Passing trials | 17/120 | 120/120 |
| 99th-percentile velocity error | 324.158 uu/s | 0.006 uu/s |
| 99th-percentile mirrored velocity error | 440.793 uu/s | below 0.001 uu/s |

This checks the initial impulse with flat starting orientation, not every possible airborne
orientation or the complete landing. A separate 400-trial native dodge-model check also passes:
90th-percentile direction error is 0.03 degrees. The implementation agrees with
[RocketSim's forward/right impulse calculation](https://github.com/ZealanL/RocketSim/blob/c2baacb8f4b441dd8505e63c2aeb5a1679b60b02/src/Sim/Car/Car.cpp).

Boost tests cover empty fields, unavailable candidates, invalid indices, stale field objects,
index changes, active copied candidates that conflict with live cooldown, and valid active or
soon-respawning pickups. A four-game simulator smoke comparison with only the boost guard
changed ended 2–2, goals 13–13; it showed no normal-game regression in that small sample.

All eleven local regression programs pass, **235 checks**, including the direct-goal scoring
regression added during review. CI now also runs the 120-trial native action test, alongside
the existing model checks. Gameplay changes are in commit `34ed53c`.
[CI run 36589716017](https://github.com/FlamingFury00/Stardust-3.0/actions/runs/36589716017)
passes Windows/Ubuntu regressions, native models and action impulses, and bot-pack/AOT smoke checks.

## Full-policy kickoff evaluation

The new `team-kickoff` drill runs both teammates and two external Nexto opponents. It samples
distinct kickoff spawn pairs, mirrors the teams, and follows play for ten seconds after first
contact. Success means a goal, or a subsequent team touch without conceding. Follow-up touches
must occur at least 0.35 seconds after initial contact to exclude the opening collision.

This follows the emphasis on possession and teammate follow-up in the
[Dignitas kickoff guide](https://dignitas.gg/articles/blogs/rocket-league/12642/take-your-rocket-league-gameplay-to-the-next-level-kickoffs).
The two-second kickoff-phase limit was checked against the
[RLBot schema](https://github.com/RLBot/flatbuffers-schema/blob/main/schema/gamedata.fbs) and retained.

| Sample | Controller | Successes | Conceded | Scored | Follow-up touch |
|---|---|---:|---:|---:|---:|
| Screening, seed 1001001 | Previous | 23/48 | 15/48 | 3/48 | 28/48 |
| Screening | Corrected dodge | 25/48 | 14/48 | 2/48 | 32/48 |
| Held out, seed 1001900 | Previous | 15/48 | 17/48 | 1/48 | 26/48 |
| Held out | Corrected dodge | 28/48 | 7/48 | 5/48 | 28/48 |

Review caught a mistake in the initial success predicate: it excluded direct goals without a
later touch. The table applies the corrected predicate to every recorded episode. This adds
one previous-build screening success and two corrected-build held-out successes; other rows
are unchanged. Original reports remain intact, with the uniform re-scoring audit in
`artifacts/cycle5-kickoff-scoring-audit.json`.

Across both samples, concessions fall from 32/96 to 21/96. That is still above the 10% gate.
Screening results are asymmetric: blue success falls from 14/24 to 8/24, while orange rises
from 9/24 to 17/24. These samples support further evaluation, not a universal kickoff claim.
The separate 16-game 2v2 comparison against the immediately preceding cycle-4 build finishes
**9–7, goals 36–31**, with a wide 95% win-rate interval of 33%–77%.

A faster second-player approach was rejected. Raising cruise speed to 2000 with braking to
750 near the cheat point left follow-up touches unchanged at 28/48, increased concessions
from 15 to 21, and reduced success from 23 to 21. Production retains its previous approach.

## Final tournament: incomplete opponent gate

The five-bot, side-swapped 2v2 round robin uses four 120-second games per pair, seed 997000.
It reruns all candidate pairs and reuses 24 unchanged opponent/reference games from the
preceding tournament, with the same frozen simulator and matching build/experiment fingerprints.

| Opponent | Completed record | Goals |
|---|---:|---:|
| Cycle-3 reference | 4–0 | 9–1 |
| Necto | 0–4 | 0–22 |
| Nexto | 0–4 | 4–32 |
| Party Cannon | 1–2, one invalid game | 4–6 |

There are **39 valid games and one failed game**, so this tournament gate is not complete.
The candidate's completed-game record is **5–10, goals 17–61**, provisionally fourth of five.
It still loses every match against Necto and Nexto. The completed games do not establish an
overall tournament-strength improvement despite the corrected mechanics and kickoff sample.

Party Cannon throws `ArgumentOutOfRangeException` in its own boost constructor at frame 13372,
121.426 seconds, while the incomplete score is candidate 1–2 Party Cannon. One lower-concurrency
retry of the entire pair reproduces the same exception at the same frame. No further retries
or opponent modifications were made. The invalid game is excluded, not counted as a win or loss.
Both attempts remain in `artifacts/cycle5-first-party-failure`,
`artifacts/cycle5-tournament-first-attempt.md` and the final tournament directory/logs.

## Reproduction

Build and freeze separate candidate/reference directories. The native reference uses the same
new simulator with the preceding `RedUtils.dll`; the model itself is unchanged. The match
comparison uses the complete cycle-4 reference build.

```powershell
dotnet $sim mechanics-lab --drill dodge-direction --episodes 120 --seed 1001500 --out artifacts/dodge-direction
dotnet $sim physics-check --model dodge --trials 400 --seed 1001501
dotnet $sim mechanics-lab --drill team-kickoff,team-kickoff-orange --episodes 24 --seed 1001900 --opponent $nexto --out artifacts/team-kickoff
dotnet $driver match --a $candidate --b $cycle4 --size 2 --games 16 --seconds 120 --parallel 2 --seed 1001700 --replays --out artifacts/dodge-paired
```

## Blind positioning review

Three anonymous 3v3 pairs were selected with seed 1002300, matching the starting ball's field
third. One source is the public 2018 RLCS replay described in the previous report; the other is
the current build against Nexto. Identities were randomly assigned to A/B and withheld until
the critic saved its judgments. Both sources use the same four-samples-per-second positioning
view. Sparse replay updates make this unsuitable for judging fine mechanics or smoothness.

| Starting context | Blind preference | Identity revealed afterward |
|---|---|---|
| Own half | A, medium confidence | Historical human team |
| Midfield | B, medium confidence | Historical human team |
| Opposing half | A, medium-high confidence | Historical human team |

The critic identified duplicated support positions and missing defensive depth during transitions.
For example, the bot's two support cars were only 156 units apart in the opposing-half clip; later
all three were upfield of a ball travelling toward their goal. The critic explicitly declined an
overall strength ranking: these are short clips with different contexts and opponents. Its review
used anonymous coordinates and derived plots; browser access was unavailable to that reviewer.

The saved pre-reveal report is `artifacts/replay-blind/review-blind.md`, SHA-256
`6d1c340d4845f4c5df1afee669d7bb5dd29fa58fc07b3c627f06d5764b812f69`. The anonymous data, view,
selection code and private mapping are retained beside it. The two source 3v3 simulator games
finished 0–2 against Nexto, goals 1–16. Live-client validation and a full/current-pro mechanics
comparison remain incomplete.
