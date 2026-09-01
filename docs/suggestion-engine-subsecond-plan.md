# Suggestion Engine Sub-Second Plan

Status: Phase 1 exact optimizations implemented and benchmarked
Last updated: 2026-09-01

## 1. Objective

Bring a new `POST /api/teams/getOptimal` calculation below one second on the
development workstation without silently changing the meaning of the scoring
model.

This document supersedes the performance estimates and implementation order in
`docs/performance-improvements.md`. That earlier document remains useful as
historical context, but several assumptions were rechecked against the current
engine and the 26-27 dataset.

The target has two distinct cases:

1. **Repeated identical request:** return a cached result in tens of milliseconds.
2. **Previously unseen team, auctioned player, bid or strategy:** solve the new
   scenario in less than one second at p50, with a separately defined p95 target.

The first target is already met for base-state requests. The second requires an
algorithmic reduction in the number and cost of states evaluated.

## 2. Measured Baseline

### 2.1 Environment and request

- .NET 9 Release build, using
  `/snap/dotnet-sdk-90/current/usr/lib/dotnet/dotnet`.
- Local PostgreSQL and HTTP API on the development workstation.
- League 7, team 1, empty roster, 800 initial credits.
- 515 available players:
  - P: 63
  - D: 181
  - C: 183
  - A: 88
- Lineup: 1-4-3-3.
- `creditsDistribution = 1`, default strategy weights, `numTeams = 1`.
- Auction trial: player 252 at 3 credits.

An empty roster is intentionally close to a worst-case solve because all 25
slots still need to be filled.

### 2.2 Results

| Scenario | Release latency | Meaning |
|---|---:|---|
| Cold base request | 9.04 s | Stage 1 DP + combination + preparation |
| Base request with all DP tables cached | 4.03 s | Mostly cross-role combination and scoring |
| Full base-result cache hit | 8-36 ms | Queries + cache lookup + serialization |
| First auction request after base warm-up | 8.84 s | Base/potential/without paths, partial DP reuse |
| Repeated auction request with DP tables warm | 6.72 s | Three combination paths still run |

The cold base response and its cache-hit response were byte-identical. The two
auction responses were also byte-identical.

### 2.3 What the measurements establish

- Database commands were reported at 0-1 ms each.
- The league-player endpoint completed in approximately 6-11 ms when warm.
- Switching from Debug to Release changed the cold solve only slightly.
- With every role DP table cached, combination alone still costs about 4 seconds.
- The bottleneck is therefore CPU work and allocation in Stage 1 and combination,
  not PostgreSQL or JSON serialization.
- The base-result cache and auction scenario cache make repeated requests cheap;
  cold unseen base requests remain the main latency gap.

### 2.4 Current state-space estimate

With default role caps P=80, D=240, C=480 and A=480, the Stage 1 loops visit
approximately:

| Role | Candidates | Slots | Budget cells | Candidate visits |
|---|---:|---:|---:|---:|
| P | 63 | 3 | 80 | 15,120 |
| D | 181 | 8 | 240 | 347,520 |
| C | 183 | 8 | 480 | 702,720 |
| A | 88 | 6 | 480 | 253,440 |
| **Total** | | | | **1,318,800** |

Many visits are rejected quickly, but accepted transitions allocate lists and
invoke the complete scorer. Combination then performs Cartesian products over
dense budget tables and invokes the scorer again for each feasible pair.

### 2.5 Phase 1 benchmark: exact combination memoization

Implemented on 2026-09-01 in `TeamSuggestionService.CombineRoleResults` and
`AssignSelectionIds`. Every dense budget state and the original nested-loop
order are preserved. Exact ordered player-ID selections receive request-local
IDs, and each `(previousSelectionId, currentSelectionId)` pair is built and
scored once. All capacity pairs still update the final table in their original
order, but duplicate pairs reuse the cached player IDs and `Score`.

| Scenario | Before | After | Reduction |
|---|---:|---:|---:|
| Cold base request | 9.04 s | 1.89-1.97 s | about 78-79% |
| Base request with DP tables cached | 4.03 s | 0.45-0.55 s | about 86-89% |
| First auction request after base warm-up | 8.84 s | 0.49-0.62 s | about 93-94% |
| Repeated identical auction request | 6.72 s | about 50 ms | about 99% |
| Two concurrent cold identical auction requests | not measured | 2.22 s each | one shared computation |

The auction results include role-local forced-player keys, request-local scoring
values, the allocation-light scorer, scenario caching and single-flight. A
forced defender reuses the base P/C/A tables; only affected role/capacity
variants need new work.

For a representative base path, exact memoization reduced complete scoring as
follows:

| Merge | Feasible capacity pairs | Unique score evaluations | Reduction |
|---|---:|---:|---:|
| D | 18,174 | 1,197 | 93% |
| C | 146,630 | 22,320 | 85% |
| A | 256,025 | 37,344 | 85% |
| **Total** | **420,829** | **60,861** | **86%** |

The optimized responses were byte-identical to the pre-change responses for:

- the default base request;
- the full Base/Potential/Without auction response;
- a discounted auction bid of 1 credit;
- Max Points (`reliabilityWeight = 0`);
- Mate Collector;
- Diversified;
- Deep Bench;
- an alternate 3-4-3 lineup with `creditsDistribution = 5`.

The five additional oracle comparisons improved from 5.0-5.5 seconds on the
still-running pre-change service to 0.45-0.83 seconds on the changed service. Those
figures include cold DP work for different request keys and are supporting
observations rather than controlled Release baselines.

## 3. Current Pipeline

The service currently performs the following work:

1. Load the team, all players, available players and format-specific prices.
2. Build `ScoringPlayer` projections.
3. For each role, run a budget-indexed DP for every required player count.
4. Fill each higher budget cell with the best selection seen at a lower budget.
5. Cross-combine the four role tables, memoizing repeated combined selections.
6. Score with request-local reliable values and allocation-light role buckets.
7. Backtrack and map the best result to DTOs.
8. For an auction request, reuse Base/Without scenarios and single-flight
  concurrent misses; only the changing Potential scenario needs recomputation.

The objective is not additive by role. Mate bonuses, club diversity, credit
spread and the back-four bonus mean that role scores cannot simply be added.
Any optimization that assumes pure additivity changes the scoring model.

## 4. Exact Optimization: Memoize Duplicate Combination Evaluations

### 4.1 What a duplicate selection is

`PrecomputeRoleValues` first finds the best selection for a particular player
count and budget capacity. It then fills gaps by copying the best earlier
selection into every larger budget:

```text
capacity 70 -> [A, B, C], actual cost 68
capacity 71 -> [A, B, C], actual cost 68
capacity 72 -> [A, B, C], actual cost 68
capacity 73 -> [A, B, C], actual cost 68
capacity 74 -> [A, B, D], actual cost 74
```

The first four entries are one decision represented four times. During
combination, all four copies are cross-multiplied with every state from the next
role. Their capacity effects must be preserved, but rebuilding and fully
rescoring the same player selection is redundant.

### 4.2 Why deleting the duplicate states is not exact

For identical ordered player IDs under the same request context:

- the score is identical;
- the actual acquisition cost is identical;
- the effect on all cross-role scoring terms is identical.

It is nevertheless unsafe to retain only the lowest capacity copy. The
combination algorithm keeps only one partial team per exact budget, while the
objective has non-additive mate, diversity and lineup interactions. Removing a
capacity copy changes which partial team collides with and replaces another
partial team before later roles are added.

This was observed with Biraghi forced at 1 credit:

| Solver | Potential price | Potential score |
|---|---:|---:|
| Original dense states | 797 | 53.6646927938139 |
| Lowest-capacity state removal | 800 | 53.675144683842845 |

The state-removal result happened to score 0.01045 higher, but it selected a
different defense. It is an algorithm change, not a lossless optimization, and
was removed.

### 4.3 Implemented exact representation

For each merge, both dense dictionaries are projected to entries containing:

```csharp
(int Budget, PlayerSelectionResult Selection, int SelectionId)
```

`SelectionId` is assigned from the exact ordered player-ID sequence. Ordering is
preserved because stable scorer ties and response ordering are observable.

The original loops still visit every feasible `(currentBudget, previousBudget)`
pair in the same order. A request-local dictionary caches:

```csharp
(PreviousSelectionId, CurrentSelectionId) -> PlayerSelectionResult
```

On the first encounter, the service concatenates IDs, resolves players and
calls `ScoringEngine.CalculateScore`. Later capacity pairs with the same two
selections reuse that immutable result. The final table update and strict
greater-than comparison are unchanged.

### 4.4 Effect

The optimization removes 85-93% of complete score calculations in the measured
merge stages without removing any budget state. Warm-DP base and auction calls
are now below one second. Cold unseen scenarios remain above the target because
Stage 1 still dominates them.

### 4.5 Metrics to add

Log or collect per role and merge stage:

- feasible capacity pairs;
- unique combined-selection evaluations;
- evaluation reuse ratio;
- pairs rejected by budget;
- complete score calls;
- combination elapsed time and allocated bytes.

## 5. Exact Optimization: Compile the Scoring Context

Implemented on 2026-09-01 in `ScoringContext` and the context-aware path of
`ScoringEngine.CalculateScore`. The public scorer remains available as a
fallback, while the suggestion pipeline uses the optimized path.

### 5.1 Precompute player invariants once per request

For fixed strategy weights and league settings, precompute for every player:

- reliable value;
- adjusted expected performance;
- base expected performance;
- acquisition cost;
- compact integer role index;
- compact integer squad index;
- mate target player/name index;
- whether the player can affect the back-four threshold.

Reliable value is calculated once per player per computation path and reused as:

```text
V = expectedPerformance
    * (regularness / 100)^(reliabilityWeight / 10)
    * integrityTilt
```

The value does not change within a request, so `Math.Pow` is no longer called
inside the Stage 1 and combination score loops.

### 5.2 Replace allocation-heavy scoring

The optimized path scores a selection using:

- four fixed role buckets rather than `GroupBy` dictionaries;
- stable in-place ordering by precomputed reliable value;
- direct starter and bench accumulation;
- direct top-three defender tracking for the back-four threshold;
- integer squad counters rather than `GroupBy((Role, Squad))`.

The scorer still constructs the small starter/bench lists, mate set and squad
dictionaries needed by the current objective. It is allocation-light, not
literally allocation-free.

The output must still be the same `Score` components. This is an implementation
change, not an objective-function change.

### 5.3 Future incremental combination summaries

Each compressed role state can carry a summary:

- starter and bench reliable-value sums;
- starter cost sum and squared-cost sum;
- selected IDs;
- squad counts by role and globally;
- selected names and mate edges;
- goalkeeper/top-defender base values for the back-four rule.

Combining two summaries can update most score components without reconstructing
and regrouping the whole team. Cross-role terms must still be evaluated exactly.

## 6. Five-Credit Budget Buckets

### 6.1 Why this is attractive

Budget is currently represented one credit at a time. With a bucket size of 5:

- Stage 1 has approximately one fifth as many budget cells.
- A naive dense combination has approximately one twenty-fifth as many budget
  pairs.

Using the measured 5.02-second Stage 1 portion and 4.03-second combination
portion gives this first-order projection:

| Bucket size | Projected Stage 1 + combination |
|---:|---:|
| 1 | 9.04 s measured |
| 2 | 3.53 s |
| 5 | 1.18 s |
| 10 | 0.55 s |

These are scaling estimates, not benchmark results. Fixed overhead, compression,
cache behavior and candidate counts will change the real result.

### 6.2 Correctness boundary

Five-credit buckets are approximate. Rounding each player cost upward is budget
safe but can reject a feasible combination near a cap. Rounding downward can
admit an over-budget team unless exact costs are checked later.

Recommended approach:

1. Keep exact acquisition cost in every state.
2. Use a coarse bucket only as the search index.
3. Never return a team whose exact cost exceeds its role or global cap.
4. Keep several unique alternatives per coarse bucket, not only one, so rounding
   does not discard all near-boundary choices.
5. Exactly rescore and rank the final candidate teams.
6. Optionally refine exact one-credit cells around the best coarse budgets and
   perform local player-swap repair.

The bucket size should be configuration-driven so it can be set to 1 for exact
comparison and emergency rollback.

## 7. Candidate Pruning

### 7.1 Why naive top-K is unsafe

A player with weak standalone reliable value can still be useful as:

- a minimum-cost roster filler;
- a starter or substitute completing a mate pair;
- a player from a less represented club who avoids diversity penalties;
- a high-base-performance goalkeeper or defender who activates the back-four
  bonus;
- a forced or favorite player;
- a different cost point that improves the credit-spread term.

Consequently, neither `ExpectedPerformance / MarketValue` nor reliable value
alone is a safe global ranking.

### 7.2 Measured quota-aware dominance experiment

The following heuristic was tested:

> Prune player `p` only when at least `slotsForRole` distinct players in the same
> role are no more expensive and have equal-or-greater reliable value, with at
> least one strict inequality.

For the default Balanced request, it reduced the live pool as follows:

| Role | Original | Retained |
|---|---:|---:|
| P | 63 | 10 |
| D | 181 | 35 |
| C | 183 | 26 |
| A | 88 | 27 |
| **Total** | **515** | **98** |

All 25 players selected by the Balanced baseline were retained. This is strong
evidence that the pool contains many weak standalone candidates, but it is not a
correctness proof.

The same filter was tested against the Mate Collector preset
(`strategyWeight = 3`, `mateWeight = 10`). It would have removed six players
from the selected team. Several were intentionally weak standalone choices that
completed valuable mate pairs. The plain quota-dominance rule is therefore not
safe across supported strategy settings.

### 7.3 Context-aware shortlist experiment

A broader experimental union retained:

- the quota-aware value frontier;
- every outgoing and incoming mate participant;
- cheap fillers per role;
- the best value and efficiency representative from each club.

Using complete mate metadata, it retained 323 of 515 players but still omitted
one player selected by the Mate Collector result. Widening the cheap-filler
guard retained 347 players and still omitted that player.

Conclusion: no tested shortlist is yet lossless for all existing presets.

### 7.4 Required always-keep set

Any production shortlist must retain at least:

- current roster players;
- the forced auctioned player;
- favorites;
- enough cheapest players per role to guarantee slot feasibility;
- both sides of potentially valuable mate relationships when mate scoring is
  active;
- candidates needed by the back-four rule;
- candidates representing underrepresented squads when diversity scoring is
  active;
- all players tied at a pruning boundary.

The shortlist must use the request's actual reliability, strategy, diversity,
mate and credit-distribution weights. A static list generated at import time is
not sufficient by itself.

### 7.5 Safer adaptive strategy

Use pruning as an initial search, not an irreversible deletion:

1. Build a conservative shortlist from the rules above.
2. Solve and obtain an incumbent score.
3. Compute an optimistic upper bound for every excluded player that includes
   its maximum plausible mate, diversity, spread and back-four contribution.
4. If an excluded player can beat the incumbent margin, expand the shortlist
   and solve again.
5. Fall back to the full pool whenever the bound is inconclusive.

This branch-and-bound style approach preserves an exact fallback while making
ordinary Balanced-like requests much cheaper.

An easier first rollout is heuristic mode behind a feature flag, with the exact
solver running in shadow mode on sampled requests and reporting quality deltas.

## 8. Cache and Concurrency Improvements

Caching does not make the first unseen scenario faster, but it is essential for
live-auction latency and server throughput.

### 8.1 Reuse complete scenario components

- **DONE:** Base result is independent of `AuctionedPlayer`; an auction request
  reuses the exact cached base result when available.
- **DONE:** Without result depends on the excluded player and team/request state,
  but not on bid price. It is cached separately.
- **DONE:** Potential result depends on the forced player and acquisition price.
  The complete scenario is cached for exact repeats and short-lived frontend
  retries.

When only the bid changes, the service calculates only Potential rather than
repeating Base and Without. The dedicated `OptimalScenarioCache` has a 100-entry,
ten-minute bounded cache and does not compete with the 25-entry DP cache.

### 8.2 Single-flight computation

**DONE:** Concurrent identical scenario misses share an in-flight `Task` per
deterministic key so later callers await the first calculation. Failed and
cancelled tasks are removed from the map.

**DONE:** DP-table misses use a keyed `Lazy<RoleValueTable>` single-flight map.
This avoids duplicate `Task.Run` work when Base, Potential and Without reach an
equivalent key concurrently. The existing `IMemoryCache` remains responsible
for the stored DP table and TTL.

### 8.3 DP cache correctness

**DONE:** DP keys include the player-data version. Re-importing a season with the
same IDs but changed statistics can no longer serve stale DP tables under the
old key.

**DONE:** The forced-player component affects only the forced player's role.
P/C/A keys are reusable when a defender is forced.

**DONE:** Result and DP keys include ordered roster context and acquisition costs.
Role-mate costs and forced bid price are included because credit-spread scoring
depends on them. This prevents a table calculated for one team's paid prices
from being reused for another team's state.

Consider caching a table at a canonical maximum budget and reading a prefix for
smaller bid-dependent caps. Exact `maxBudget` keys unnecessarily fragment reuse.

## 9. Optimizations Requiring Caution

### 9.1 Per-role saved-credit caps

The older performance document proposes adding only savings earned within role R
to role R's budget cap. That is not generally equivalent to the current model.
A goalkeeper bought below market value can free credits that are legitimately
spent on a midfielder or attacker. Savings are global unless the product rules
explicitly make them role-bound.

Tighter caps are valid only when derived from a global feasibility bound that
does not prevent cross-role reallocation.

### 9.2 Adding role scores

Do not replace complete scoring with the sum of role-local scores. Mate edges,
global squad counts and the back-four interaction cross role boundaries.
Incremental summaries are valid only if they reconstruct those terms exactly.

### 9.3 Unconditional zero-regularness removal

At full reliability weight, a regularness-zero player's reliable value is zero,
but the player can still be a cheap filler or complete a mate pair. At
`reliabilityWeight = 0`, regularness does not discount value at all. This is not
a safe universal filter.

## 10. Recommended Implementation Order

### Phase 0: Instrumentation and regression corpus

1. **PARTIAL:** add score-reuse and cache hit/miss logs. Per-stage elapsed time,
  allocation bytes and automated tests remain open.
2. **DONE FOR THIS CHANGE:** capture exact baseline responses for a matrix of
  representative requests.
3. Automated tests remain open; current validation uses Release HTTP oracle
  comparisons.

### Phase 1: Exact reductions

1. **DONE:** memoize exact combined-selection evaluations while retaining every
  dense budget state and original iteration order.
2. **DONE:** include player-data version, ordered roster costs, role-mate costs
  and forced bid price in cache keys; scope forced-player IDs to the affected
  role.
3. **DONE:** precompute request-specific reliable values.
4. **DONE:** replace the hot scorer's grouping/sorting path with stable role
  buckets, direct accumulation and integer squad counters.
5. **DONE:** reuse cached Base and Without results in auction requests.
6. **DONE:** add scenario and DP single-flight protection.

Physical state removal and actual-cost normalization are separate algorithmic
experiments because they can change which partial teams survive.

The exact Phase 1 work brings warm base and auction calls below one second. Cold
base requests remain about 1.9 seconds on this workstation, so Phase 2 is only
needed if that cold-start target is required.

### Phase 2: Five-credit coarse search

1. Add configurable bucket size, defaulting to 1 initially.
2. Keep exact costs and several unique alternatives per bucket.
3. Add exact final validation and reranking.
4. Run bucket sizes 2 and 5 against the regression corpus.
5. Enable size 5 only if quality and latency gates pass.

### Phase 3: Adaptive candidate shortlist

1. Implement the always-keep set.
2. Add a conservative request-aware frontier.
3. Add excluded-player upper bounds and automatic expansion/fallback.
4. Shadow-compare against the full solver before serving heuristic results.

## 11. Validation Matrix

At minimum, compare exact baseline and optimized output across:

- all eight documented strategy presets;
- `creditsDistribution` values 0, 1 and 5;
- goal-bonus rule on and off;
- 3-4-3 and 4-3-3 lineups;
- empty, partial and nearly complete rosters;
- each forced-player role;
- below-market, market and above-market bids;
- requests with favorites;
- at least two supported price formats;
- repeated and concurrent identical requests;
- player import with reused IDs and changed values.

### 11.1 Invariants for exact phases

- Identical selected player IDs and scores.
- Exact role counts and lineup behavior.
- Exact total acquisition cost and no cap violation.
- Forced and favorite players handled identically.
- Market-price auction symmetry remains within the existing tolerance.
- No stale result after import.

### 11.2 Acceptance criteria for approximate phases

Agree on these limits before enabling five-credit buckets or pruning. Proposed
starting gates:

- no illegal roster or budget result;
- no missing forced/favorite player;
- p50 under 1.0 second for an unseen base request;
- p95 under 1.5 seconds on the target machine;
- repeated exact scenario under 50 ms;
- total-score loss no greater than 0.1% against the exact solver;
- separately report selected-player overlap, because a tiny score loss can still
  produce a visibly different roster;
- zero violations of auction delta direction in the regression corpus.

Do not use score tolerance alone to conceal a materially different team. Record
both score delta and player-set delta.

## 12. Expected Path to the Target

Five-credit buckets alone have an optimistic first-order estimate of about
1.18 seconds from the old baseline, but that estimate predates Phase 1. Candidate
reduction alone is risky under strong interaction weights. The current exact
results are:

1. cold base: 1.89-1.97 seconds;
2. first auction after base precompute: 0.49-0.62 seconds;
3. repeated identical auction: about 50 milliseconds;
4. concurrent identical cold auction requests: about 2.22 seconds each, sharing
  the same computation.

The next credible route below one second for cold base is five-credit coarse
indexing with exact final checks, followed by adaptive candidate pruning only if
the quality gates pass.

This order minimizes quality risk and provides a rollback boundary at every
stage.

## 13. Related Findings

These are not required for the sub-second effort but were exposed during the
trials:

1. `numTeams` affects work and cache keys, and backtracking calls
   `Take(numTeams)`, but `CombineAndBacktrack` returns only `results[0]`.
2. The manual league-market DTO projection omits `Mate`, while the global player
   endpoint and suggestion results include it. The engine itself receives mate
   data correctly, but clients of `GET /api/leagues/{id}/players` do not.
3. The existing database query shape should not be prioritized without new
   evidence; observed query times were already 0-1 ms.

## 14. Relevant Code

- `Fantahelp.API/Services/TeamSuggestionService.cs`
- `Fantahelp.API/Services/Helpers/ScoringEngine.cs`
- `Fantahelp.API/Services/Helpers/RoleValueTable.cs`
- `Fantahelp.API/Services/Helpers/FinalCombinationResult.cs`
- `Fantahelp.API/Services/TeamPrecomputer.cs`
- `Fantahelp.API/Controllers/LeaguesController.cs`
- `docs/performance-improvements.md`
- `docs/suggestion-engine-caching-plan.md`
- `docs/suggestion-engine-refactoring.md`