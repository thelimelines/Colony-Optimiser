# Solver Model

The model is solved over one complete game cycle with Google OR-Tools CP-SAT. Item coefficients use millionths of an item and work uses milliseconds, avoiding fragile floating-point feasibility tests while retaining low tool-wear rates.

Inputs with no parsed non-player producer are treated as unmodelled source materials. They remain visible in the result as automatic external requirements; science-gated and forbidden producers are not treated as sources and still produce an explanation.

For every active job type with a stocked selected tool, the result lists one starter tool per required worker/job block. Starter stock is a one-time setup requirement. The recurring material balance consumes tool replacements from actual tooled work: the loaded `durability` is seconds of tooled work, and the consumed tool quantity for a recipe is its effective workload divided by that durability. Replacement tools and their inputs are therefore included in the recurring recipe allocation. Tools marked `requiresItem: false` are not treated as stockpile consumables.

For each enabled recipe `r`, `C_r` is a non-negative integer number of crafts. For each production job `j`, `N_j` is a non-negative integer worker/job-block count.

For each non-external item `i`:

```text
sum_r C_r * (output(r, i) - input(r, i) - toolWear(r, i)) >= demand(i)
```

Probabilistic output is `amount * chance` for expected value. Ignore-optional mode sets optional output to zero. Conservative mode sets all chance-based output to zero.

For every job type `j`:

```text
sum_(r assigned to j) C_r * effectiveCooldown(r)
    <= N_j * activeWorkerMilliseconds * efficiency * (1 - headroom)
```

This is the global shared-capacity constraint: crafts from several recipes consume the same job capacity before worker count is rounded.

Simple crop farms are modelled as dedicated farm areas. Their growable stages advance once per night, so an `n`-stage crop has a growth period of `n - 1` full game cycles. Each configured field produces its harvested-tile count divided by that period; one farmer is reserved for the area. The crop source tab records field tile counts, growth, science, and expected output per game cycle.

The default lexicographic objective is:

1. minimise total `sum_j N_j`;
2. minimise non-preferred craft count while retaining that worker minimum;
3. minimise total worker milliseconds.

Preferred-recipes-first changes the first two priorities. Lowest-raw-resource-consumption first minimises inputs with no enabled producer, then workers, preferences, and workload. It does not invent gathering rates; unresolved material stays explicit as an external input.

External items intentionally have no material-balance constraint. Their net unmet consumption is calculated after the solve and displayed as an external requirement. This is what prevents unsupported gathering mechanics from being silently fabricated.
