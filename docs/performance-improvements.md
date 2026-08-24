# Performance Improvements – TeamSuggestionService

## Context
Current implementation: per-role 0/1 knapsack DP with non-additive scoring, cross-role combine, 3-path suggestion Base / Potential / WithoutPlayer.
Typical request: 4 roles, ~130 candidates per role, maxBudget ~500, slots 3-8.
Current latency: ~0.8-1.5s per 3-path call on dev hardware.

## Current bottlenecks
1. **Over-sized DP tables** – `dpMaxPerRole = baseCap + savedFromCurrent` adds total saved credits to every role.
2. **Full candidate set** – DP iterates over all players per role, ~130.
3. **Repeated scoring** – `ScoringEngine.CalculateScore` is called for every DP cell and again in combine.
4. **Combine rescoring** – cross-role combine re-scores whole team for each budget pair.
5. **Cache key granularity** – `currentRoleMateIds` in key reduces hit rate after purchases.

## Why GPU is not applicable
* Data size tiny: 4k cells per role, 529 players total.
* Scoring is branching, sorting, averaging, dictionary lookups – not matrix ops.
* Latency sensitive single request, transfer overhead dominates.
* C# / .NET 9 code base, no existing GPU pipeline.

## Proposed improvements

### 1. Tighter per-role budget caps
**Current:** `dpMaxPerRole = baseCap + totalSaved`
**Proposed:** `Cap(R) = BaseCap(R) + SavedCredits_R` where `SavedCredits_R` is max additional budget role R can absorb given total saved.
Implementation: compute per-role saved from current players and forced player discount, use `Math.Min(baseCap + saved, globalUpperBound)`.
Impact: 40-85% smaller tables, same correctness.

### 2. Candidate pruning per role
Pre-filter `availablePlayersByRole` to top-K by cheap proxy `ExpectedPerformance / MarketValue`.
Recommended K = 60-80 for D/C/A, 40 for P.
Impact: linear reduction of inner loop.

### 3. Budget granularity
Coarsen budget steps to 5 credits during DP, then refine around best 3-5 cells.
Impact: ~5x fewer iterations, minimal score loss.

### 4. Score memoisation
LRU cache for `ScoringEngine.CalculateScore` keyed by sorted player IDs hash.
Impact: avoids duplicate scoring of same selection in DP and combine.

### 5. Avoid rescoring in combine
Store role scores in `RoleValueTable`. Combine using sum of role scores + cheap cross-role penalties.
Impact: reduces expensive full-team scoring.

### 6. Cache optimisation
* Keep current `IMemoryCache` for DP tables.
* Add secondary cache for base tables without `currentRoleMateIds` and recompute mate-dependent part incrementally.
* Phase 2 invalidation on player import via `ICacheVersion`.

### 7. Allocation reduction
Reuse lists/buffers in DP loops, avoid repeated `ToList()` and LINQ allocations.

## Implementation priority
1. Tighter caps – low risk, high gain
2. Candidate pruning – low risk
3. Score memoisation – low risk
4. Budget granularity – medium risk, needs validation
5. Combine rescoring refactor – medium risk

## Metrics to track
* Request latency p50/p95 for 3-path call
* DP table size per role: `slots * maxBudget`
* Cache hit rate per role
* Number of `CalculateScore` calls per request

## References
* `Fantahelp.API/Services/TeamSuggestionService.cs`
* `Fantahelp.API/Services/Helpers/ScoringEngine.cs`
* `docs/suggestion-engine-caching-plan.md`
* `docs/FUTURE_STEPS.md`

Last updated: 2026-08-10
