# Solver Model

The model is solved over one complete game cycle with Google OR-Tools CP-SAT. For each eligible recipe `r`, `C_r` is a non-negative integer craft count. For each production job `j`, `N_j` is a non-negative integer worker or job-block count.

## Recipe eligibility

The optimiser builds its feasible recipe set before constructing constraints:

- player recipes are excluded from colony production;
- forbidden recipes and recipes needing locked science are excluded;
- forcing a recipe excludes other recipes that produce the same output;
- if no forced recipe for a forced output remains eligible, the optimiser produces an error;
- an automated queue remains available when every one of its outputs is available from the eligible worker-recipe set. Its crafts are deprioritised as fallbacks unless the queue recipe is preferred or forced; and
- preferred recipes remain feasible alternatives but receive a lower penalty in the preference objective.

Automatic external-source detection examines parsed non-player producers before these eligibility filters. An input is automatic external material only when it has no parsed non-player producer at all. A science-gated, forbidden, or displaced producer therefore does not silently turn its output into an external source; the optimiser reports why the requested item cannot currently be produced.

## Material balance

For each non-external item `i`:

```text
sum_r C_r * (output(r, i) - input(r, i) - toolWear(r, i)) >= demand(i)
```

Probabilistic output is `amount * chance` in expected-value mode. Ignore-optional mode sets optional output to zero. Conservative mode sets all chance-based output to zero.

For every active job type with a stocked selected tool, the result lists one starter tool per required worker or job block. Starter stock is a one-time setup requirement. The recurring balance consumes tool replacements from actual tooled work: loaded `durability` is seconds of tooled work, and tool use per craft is effective workload divided by durability. Replacement tools and their ingredients therefore join the recurring recipe allocation. Tools marked `requiresItem: false` are not stockpile consumables.

## Defence demand

Each guard assignment supplies a guard type, guard count, and ammunition mode. The guard definition supplies its day or night shift, `cooldownShot`, and ammunition consumed per shot. Except for an explicit custom-round count, rounds per guard per cycle are rounded up:

```text
ceil(relevantDuration / cooldownShot * utilisationPercent / 100)
```

Entire-shift mode uses the loaded duration of the guard's day or night shift. Hostile-period mode uses only the overlap between that shift and the loaded hostile period, including intervals that cross midnight. Custom-utilisation mode applies a percentage clamped to 0-100% across the whole shift. Custom-round mode uses the requested non-negative integer directly.

Rounds are multiplied by the guard count and each ammunition requirement, then added to ordinary player demand before the material-balance constraints are created. Trap assignments similarly add one configured full refill per trap per cycle.

## Job-block capacity

For every job type `j`:

```text
sum_(r assigned to j) C_r * effectiveWorkload(r)
    <= N_j * availableBlockMilliseconds
```

Worker-operated jobs use their configured active time, or the general worker-active interval, multiplied by efficiency and reduced by requested headroom. Automated queues use the full game cycle and are counted as machine blocks rather than workers. This is a shared-capacity constraint: crafts from several recipes consume the same job capacity before the worker or machine-block count is chosen. Mining recipes are instead grouped by mined output resource, so each resource reports its own miner requirement. Dedicated farm or forestry crafts also reserve their required workers explicitly.

Simple crop farms are modelled as dedicated farm areas. Their growable stages advance once per night, so an `n`-stage crop has a growth period of `n - 1` full game cycles. Each configured field produces its harvested-tile count divided by that period; one farmer is reserved for the area. The crop source tab records field tile counts, growth, science, and expected output per game cycle.

## Integer scaling and rounding

CP-SAT accepts integer coefficients, so item quantities are multiplied by `1,000,000` and rounded to the nearest integer using midpoint rounding away from zero. This retains quantities down to one millionth of an item; each converted coefficient or demand can differ from its decimal value by at most half a millionth. The same conversion is used for material-balance coefficients and the raw-resource objective.

Work is represented in whole milliseconds. Each recipe's cooldown or explicit workload is divided by its effective tool multiplier, converted to milliseconds, rounded up, and clamped to at least one millisecond. Available job-block time is converted to milliseconds after efficiency and headroom are applied, then rounded down. Rounding workload up and capacity down avoids granting work that does not fit within the represented time.

Craft and job-block counts remain integers and are not rounded after solving. Displayed decimal totals are reconstructed from those solved counts and the original decimal item quantities, while effective cooldown and workload displays use the integer millisecond values enforced by the solver.

## Objectives

The default `FewestWorkers` lexicographic objective is:

1. minimise worker blocks, then automated machine blocks;
2. minimise crafts from automated queues whose outputs are collectively covered by eligible worker recipes;
3. minimise non-preferred craft count while retaining those block and fallback minima; and
4. minimise total worker or machine milliseconds.

Preferred-recipes-first first minimises non-preferred crafts, then automated fallbacks, worker blocks, machine blocks, and workload. Lowest-raw-resource-consumption first minimises inputs with no enabled producer, then automated fallbacks, worker blocks, machine blocks, preferences, and workload. It does not invent gathering rates.

## External materials

Explicit external items and inputs with no parsed non-player producer have no material-balance constraint. After solving, their net unmet consumption is calculated from demand and recipe flows and displayed as an external requirement. This keeps unsupported gathering mechanics visible rather than fabricating production rates. Science-gated and forbidden producers are not automatically reclassified as external sources, as described under [Recipe eligibility](#recipe-eligibility).
