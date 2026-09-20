# Stardust 3.0 — Beta

> **Beta software.** Stardust 3.0 is under active development and is not a stable release. Mechanics, strategy, physics/control models, tuning, and compatibility may change substantially while the bot is being validated in live Rocket League matches.

Stardust 3.0 is the experimental next generation of Stardust. It adds threat-first planning, persistent possession mechanics, ground catches/carries/flicks, velocity-matched aerial carries, experimental flip-reset attempts, expanded defensive/rotation logic, boost-management work, and automated control regressions.

It is an evaluation build, **not a benchmark-verified professional-level release**. Experimental mechanics should be treated as beta features until they have repeatable in-game evidence. Reset attempts remain opt-in; no trained neural policy is included.

See **[the Stardust 3.0 implementation, research, feature switches, and evaluation guide](docs/STARDUST_3.md)** for build/test commands, limitations, and the proposed learned-skills roadmap. The original bot registration identity is retained.

## Project background

Stardust is a Rocket League bot built on the [RLBot](http://www.rlbot.org/) framework for offline matches, using RedUtils (C#). This repository targets the vendored RLBot v5 integration; use a compatible v5 setup rather than assuming an older GUI/runtime is interchangeable.

![Stardust Logo](./logo.png)

## Stardust 3.0 history

The original Stardust 2.0 was optimized for 2v2 and 3v3 and was redeveloped for the RLBot Championship finals. The repository records a fourth-place finish in the **RLBot Championship 2023**; the original finals video is [here](https://www.youtube.com/watch?v=6A8_6RR4vR0&t=305s).

The 3.0 beta retains the existing shot solvers, kickoff routines, and drive subactions while iterating on the control lifecycle, possession controllers, team behavior, defensive anticipation, and mechanics. The [evaluation guide](docs/STARDUST_3.md#promotion-criteria-run-these-in-rocket-league-before-a-release) separates software checks from the in-game evidence needed to establish playing strength.
