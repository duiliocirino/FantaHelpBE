# Future Steps

Track of pending work, ordered by priority.

---

## High Priority

### Import Pipeline Improvements

**Context:** The CSV import (`POST /api/players/import`) now handles Age and Role_M correctly. Mate field still stores player names (string) rather than IDs.

**Issues:**

| Field | Status | Detail |
|---|---|---|
| `Role_M` | **DONE** | Parsed from semicolon-separated CSV string to `List<string>`. Exposed in `PlayerReadDto`. |
| `Age` | **DONE** | Added to `PlayerCreateDto`, mapped through to `Player` entity. Exposed in `PlayerReadDto`. |
| `Mate` | Open | Stored as mate's **name** (string). Fragile: names are ambiguous ("Ederson D.s."), change over time. Consider storing as **player ID** (`MateId int?`) instead. |

**Mate detail:** Currently stored as player name string. Open question: should BE resolve names to IDs post-import (two-pass), or should ML output mate's SkyBet ID directly.

### Per-Format Prices (26-27+ contract)

**Context:** From 26-27, ML produces one CSV per league format (`players_{credits}_{starters}.csv`); `expprice`/`expstd` are format-specific. The BE now stores them in the `PlayerPrice` table (one row per player x format) and the suggestion engine resolves the league's format from `(League.InitialBudget, lineup total starters)` -- exact match, else closest by credits then starters. `Player.ExpectedPrice`/`ExpectedStd` are a deprecated bridge, populated at import from the reference format **800_8**.

**Done (26-27 import readiness):**

| Item | Status |
|---|---|
| `Player.Integrity` nullable int (1-5, null = unknown) | **DONE** |
| `PlayerPrice` table (design B: player x format) + migration | **DONE** |
| Multi-file import (`files` form field, format from file name, cross-file consistency check) | **DONE** |
| Suggestion engine format-aware pricing (+ format in DP cache key) | **DONE** |

**Open:**

| Item | Detail |
|---|---|
| Format-aware read endpoints | `GET /api/players` / `GET /api/leagues/{id}/players` always return the bridge (800_8) values. Add a format query param (FE coordination) so the FE can display per-format expected prices. |
| Remove bridge columns | Drop `Player.ExpectedPrice`/`ExpectedStd` once the FE consumes per-format data. Requires FE migration + a migration. |
| FE: `integrity` display | `PlayerReadDto.integrity` is exposed; FE can show the robustness consensus (null = unknown). |

---

### Regularness 0-100 scale (26-27+)

**Context:** From the 26-27 season ML ships `regularness` as the **expected starting % (0-100, multiples of 5)** instead of int 1-5 (source: LLM squad assessment; the community 1-5 consensus is now the validation anchor, not the data source). The refreshed %-scale files ship with the post-market Quotazioni refresh -- a single import, no double import.

**Impact assessment (2026-08-23):**
- No schema/DTO change: `Player.Regularness` is `int`, 0-100 fits. The import `?? 0` fallback is now a real value (bench player), not a missing-data marker.
- FE is already compatible: `parseRegularnessValue` (`PlayerMapper.kt`) handles the 0-100 scale via its `else` branch.
- **The `ScoringEngine` regularness formulas are calibrated on the 1-5 scale** (`5*(avg-4)` starters / `avg-3` subs) and MUST be re-derived before the %-scale import -- otherwise scores inflate by ~20x (e.g. a 90% starter would score `5*(90-4)=430`).

**Done (2026-08-23):** percentage-native formulas in `ScoringEngine.ComputeStrategiesScore`:

```csharp
starters: (avgStarters% - 80) / 4   // 1 point per 4% of deviation from the 80% baseline
subs:     (avgSubs% - 60) / 20      // 1 point per 20% of deviation from the 60% baseline
```

Anchors are percentage-native (starters 80% = reliable starter, subs 60% = reliable sub); the slopes preserve the magnitude of the legacy 1-5 formulas (`5*(avg-4)` / `(avg-3)`) under the 1↔20% ... 5↔100% mapping, so the relative tuning of the other strategy terms is unchanged. The slopes are one-line knobs if re-tuning.

**Verified:** with data rescaled ×20 (1-5 → 20-100), the new formulas produce a **bit-identical** total score and identical suggested players to the legacy formulas on the original 1-5 data. Real %-scale files (0-95, multiples of 5) imported and scored sanely (35.14 vs 35.59 baseline -- the gap is the real distribution, e.g. bench players at 0%).

| Item | Status |
|---|---|
| Percentage-native `ScoringEngine` regularness formulas | **DONE** |
| Validate %-scale distribution on import (spot-check: Barella ~90, Dybala ~65, bench 0) | **DONE** (0-95, filled 515/515, real spread) |

---

### Scoring Engine — Personalization Weights & Goal-Bonus Rework

**Context:** Deep analysis of the objective function (2026-08-24) showed the three blocks were effectively 96/3/1 (starters/bench/strategy) because raw block scales (~15:1:1) weren't compensated by the 0.6/0.3/0.1 multipliers — bench and personal preferences were decision-irrelevant. The same-club penalty had a dead zone (a 3rd same-club defender was free — the "3 Roma defenders" case). The goal-bonus league rule was a small strategy term applying the full bonus (base 3 is already in expmf) with no price impact.

**Done (2026-08-24):**
- `StrategyWeights` on `SuggestionRequest` (optional): `StarterWeight` 6 / `BenchWeight` 3 / `StrategyWeight` 1 (defaults = legacy) + sub-knobs `SquadDiversity` 2 / `MateWeight` 1 / `ReliabilityWeight` 5 (all 0–10). Absent = legacy behavior, exact reproduction verified.
- Graduated same-club penalty: per (role, club) `max(0, n−1)`, per club `2·max(0, N−3)`, scaled by `SquadDiversity/5` — no dead zone.
- Goal-bonus rework (league flag + `PUT /api/leagues/{id}/goal-bonus` toggle), two parts: (1) direct per-player value — net bonus over base 3 injected up-front, DEF ×1.02 / MID ×1.03 on expmf, price side DEF ×1.10 / MID ×1.05 on expected price (roster players keep paid auction price); old strategy term removed. (2) back-4 defense bonus in the starter block — only for 4+DEF lineups, when keeper + top 3 defenders average 6+ **on the base performance (without the goal-bonus adjustment)** the starters get k = ⌊(avg−6)/0.25⌋+1 (6–6.25 → 1, 6.25–6.5 → 2, 6.5–6.75 → 3, …). Verified exact: 3-4-3 adds 0, 4-3-3 adds k.
- 8 reference presets finalized from the trial matrix (Balanced/Max Points/Deep Bench/Full Depth/Diversified/Reliable/Mate Collector/Safe) — tracked in `FantaHelpFE/docs/backend-changes-needed.md` #12.
- `GET /api/leagues/{id}/players?starters=N`: format-aware (closes #9 league-scoped part) + goal-bonus adjusted, new `baseExpectedPrice` field (raw estimate).
- Weights + flag in DP cache key, precompute result key and lastParams.
- Trial matrix run (SquadDiversity 1–10, block weights, mate weight, flag on/off): all knobs move the team in the intended direction.

**Open:**
- FE implementation of the presets + goal-bonus toggle + market-tab changes (`FantaHelpFE/docs/backend-changes-needed.md` #12–#14).
- Price-uplift factors (D +10% / C +5%) and value factors (D +2% / C +3%) are engine constants in `ScoringEngine` — expose as per-league settings if real-world calibration suggests.
- DP role-local approximation: the engine scores role-local units during search, so cross-role aggregates (global regularness average, cross-role mates) are approximated in pruning; final teams can be ~0.1% below the true optimum of the stated formula. Accepted.

### Scoring Engine — Reliability-Aware Player Value (V)

**Context (data findings, 26-27 import):**
- `expmf` is the quality **when playing** — flat across regularness buckets (D: 5.93 at reg 0 → 6.16 at reg 80+; user-confirmed no start-probability content). The old engine scored raw expmf, so a reg-25 defender (Fortini, 5.07 pts/cr) beat a reg-95 one (1.04 pts/cr) — "phantom bargains": the market prices availability (D mean price 1.6cr reg-0 → 19.0cr reg-80+), the engine didn't.
- `expstd` is the **std of observed auction prices** (ML `pipeline/price_models.py`, clipped [1,150]) — price uncertainty, not on-pitch reliability; correlation with integrity 0.005. Not usable in the objective.
- `integrity` (1–5, 18 nulls) is independent: corr(expmf) −0.162 (slightly anti-correlated — robust players are less explosive, a real risk/return axis), corr(reg) −0.019.
- The old bench ratio B is structurally pinned (3.58–3.65 across all teams; per-role 0.84–0.97) — it measures the pool's talent curve, not team quality, and moves the wrong way under budget pressure (tight D cap → B up while S crashes). Meanwhile S + Σexpmf(bench) ≈ 162.5–163.2 (±0.35): total squad value is allocation-invariant, so absolute bench value is the right scale.

**Done (2026-08-24):**
- Every player is scored by the reliable value `V = expmf' × (regularness/100)^α × (1 + 0.03·α·(integrity−3))` (integrity null → factor 1), with α = `ReliabilityWeight`/10 and expmf' the goal-bonus-adjusted performance. S = ΣV(starters) + back-4 (threshold still on the **base** expmf, unchanged); B = **0.5 × ΣV(bench)** (rotation factor: a bench player plays about half the fixtures — `ScoringEngine.BenchRotationFactor`); T = mates + graduated squad penalty + credit spread (the old regularness term removed — it double-counts what V now prices). Price side unchanged.
- `ReliabilityWeight` default 5 → **10** (full expected contribution — the economically consistent reading); 0 = pure quality-when-playing. `ScoringPlayer` gained `Integrity` (4 construction sites).
- **Why the 0.5 rotation factor**: with B = ΣV (no discount) line and bench points are worth the same and the line/bench split is endogenous (top-k by V), so the S/B weight ratio is mathematically degenerate — verified: 6/3, 6/5, 5/7, 5/5 all return the same team. With ρ < 1 the ratio becomes a (weak) real trade-off: effective value per point is `StarterWeight/10` (line) vs `BenchWeight×0.5/10` (bench). Verified behavior: the ratio flips only marginal picks and **saturates** once `BenchWeight×0.5 ≥ StarterWeight` (2/7 ≡ 4/5 ≡ 9/9); the team is dominated by V, the T knobs and the budget allocation. Accepted — a point is a point, and the allocation caps are the real line-vs-bench budget lever.
- Presets re-tuned to the knobs that actually move the team; all 8 verified distinct on 26-27 data: Balanced (default 6/3/1 div2 mate1 rel10), Max Points (7/2/1 div1 rel0), Star Line (8/2/2), Deep Bench (4/8/2), Diversified (6/3/3 div10), Mate Collector (6/3/3 mate10 — buys ~11 mate pairs, S 71.5→64.8), Tempered (rel5), Mate & Variety (6/3/3 div5 mate5). See `FantaHelpFE/docs/backend-changes-needed.md` #12.
- Verified component-exact (S/B/T recomputed from the response, diff ≤ 1e-13): w=0, default, flag on/off; back-4 exact (3-4-3 → +0.0000, 4-3-3 → +3.0000); 1-3-2-2 and the auction path work.

**Open:**
- `BenchRotationFactor` (0.5) is a single engine constant — calibrate against real rotation data or expose as a league setting if the team's injury/rotation patterns suggest.
- Integrity tilt strength (0.03 per level at rel 10: int-5 +6% / int-1 −6%) is a constant — revisit if the 1–5 scale changes.
- Note the `expstd` semantics (auction-price std) in `docs/ml-be-contract.md` so nobody reaches for it as a reliability signal again.

### Precompute optimal teams per team state

**Context:** During live auctions the FE repeatedly calls `POST /api/teams/getOptimal`; the first call for a state pays the full DP cost (~0.8-1.5s). Goal: always keep the optimal team precomputed for each team's current state and re-trigger on state changes (e.g. a player is added/removed from the team).

**Planned design (draft, pending sign-off):**
- Result cache (IMemoryCache) keyed by `(teamId, stateHash)` where state = roster + lineup + creditsDistribution + numTeams + favorites. Precompute the **base state** (no auctionedPlayer) -- the auctioned player changes constantly during an auction and is already cheap thanks to the shared DP tables.
- Remember the last requested params per team (captured from `getOptimal`) so the precomputer knows *what* to compute for that team.
- Triggers: team roster mutation (add/remove player) -> invalidate + recompute (debounced, superseded runs cancelled); player import -> invalidate all.
- `getOptimal` fast path: exact state match in cache -> return immediately.

**Design decisions (signed off 2026-08-23):** base state only (no auctionedPlayer); in-memory only (single instance); triggers on roster mutation + player import (manual league changes are rare -- the FE's next request refreshes anyway).

**Done (2026-08-23):**
- `ITeamPrecomputer` / `TeamPrecomputer` (singleton): dedicated result cache (10-min TTL, aligned with the DP cache) keyed by `(dataVersion, teamId, leagueId, roster, lineup, creditsDistribution, numTeams, favorites, budgetAllocation)`; per-team last-request memory; debounced (500ms) recompute with cancellation of superseded runs, running the normal pipeline in its own DI scope.
- `TeamSuggestionService`: fast path returns the cached base-state result (measured 11-16ms vs ~1-4s full run); stores the result of every base-state run (auctioned runs never touch the cache).
- Triggers: `TeamService.AddPlayerToTeamAsync` / `RemovePlayerFromTeamAsync` → `NotifyTeamRosterChanged`; `PlayerService.ImportPlayersFromCsvAsync` → `NotifyPlayerDataChanged` (bumps the data version, which is part of the cache key, so a re-import can never return a stale result even when player ids are reused across seasons).

**Verified:** repeated base call 16ms with identical result; add/remove player → debounced recompute → next call 11ms; import → invalidation + recompute; auction path unaffected (potential/without scores computed, never cached).

---

### Suggestion Engine — Completed Refactoring

**Context:** The suggestion engine (`POST /api/teams/getOptimal`) underwent a comprehensive refactoring to fix mathematical anomalies (negative deltas at market price) and improve architecture. Full writeup: [`docs/suggestion-engine-refactoring.md`](suggestion-engine-refactoring.md).

| Area | Item | Status |
|------|------|--------|
| **Math** | Budget double-counting fix | **DONE** |
| **Math** | Context-aware Stage 1 DP (forced player included in scoring) | **DONE** |
| **Math** | Performance-based starter selection (ExpectedPerformance, not cost) | **DONE** |
| **Arch** | Unified `ComputeSingleSuggestionAsync` pipeline | **DONE** |
| **Arch** | Concurrent twin-run via `Task.WhenAll` | **DONE** |
| **Arch** | `ScoringPlayer` record (MarketValue ≠ AcquisitionCost) | **DONE** |
| **Arch** | `ScoringEngine` extraction (static class) | **DONE** |
| **Arch** | DTO dual binding (`price` + `acquisitionPrice`) | **DONE** |
| **Quality** | O(1) DP lookups (`playerById` dict) | **DONE** |
| **Quality** | Structured logging (`ILogger`) | **DONE** |
| **Quality** | Dead code removal | **DONE** |

**Guarantees:**
- Market price bid → $\Delta = 0.00$
- Below-market bid → $\Delta > 0.00$
- Overpaying → $\Delta < 0.00$

---

### Suggestion Engine Caching

**Context:** The suggestion engine serves repeated base and auction requests during live auctions. Exact Phase 1 reuse now covers DP tables and complete scenarios.

**Phase 1 — Core cache: DONE** (`2f97eba`)

- `IMemoryCache` injected into `TeamSuggestionService` (capacity 25 entries, ~5 MB, 10min TTL, size-based LRU eviction)
- Per-role `RoleValueTable` cache key includes the player-data version, role pool, slots, budget, role-scoped forced player and bid cost, role-mate acquisition costs, lineup, credits distribution, budget allocation, price format, weights and goal-bonus state
- Per-role player hash (not global pool hash) → P/C/A entries survive when only a defender is purchased
- Current role-mates included in Stage 1 scoring context → base and potential paths evaluate against the same role-unit composition (post-purchase score matches pre-purchase potential, gap 0.057 → 0.0007)
- No explicit invalidation on team change — cache key encodes enough context; LRU handles eviction naturally
- Dedicated scenario cache reuses Base, Potential and Without-player results; bid changes recalculate only Potential
- Scenario and DP single-flight prevents concurrent identical misses from duplicating work

**Impact:**
- Warm base and auction requests are below one second on the benchmark machine
- Repeated identical auction requests are approximately 50ms
- Concurrent identical misses share one computation

**Phase 2 — Invalidation on import: DONE**

- `ITeamPrecomputer.DataVersion` is included in result and DP-derived keys
- `PlayerService.ImportPlayersFromCsvAsync` notifies the precomputer after a successful commit
- Old entries become unreachable after import, including when player IDs are reused

Detailed measurements and the remaining cold-start work are tracked in
[`docs/suggestion-engine-subsecond-plan.md`](suggestion-engine-subsecond-plan.md).

### Unit / Integration Tests

No tests exist yet. Priority areas:
- `TeamSuggestionService` scoring logic
- Mapper correctness (TeamMapper, LeagueMapper, TeamPlayerMapper, PlayerMapper)
- Controller endpoints

---

### Authentication Middleware

No auth is configured. Needed before exposing the API externally.

---

### Connection String Configuration

`appsettings.json` ships with an empty `DefaultConnection`. The local copy is set manually. Consider wiring it through environment variables (`Configuration["ConnectionStrings:DefaultConnection"]`) so the committed file stays clean.

---

## Performance Improvements

### TeamSuggestionService optimisation

See [`docs/suggestion-engine-subsecond-plan.md`](suggestion-engine-subsecond-plan.md)
for the current benchmarks, exact optimizations, five-credit coarse-search design,
candidate-pruning constraints and validation rollout. The older
[`docs/performance-improvements.md`](performance-improvements.md) is retained as
historical context.

**Status:** Exact Phase 1 optimizations implemented and benchmarked.

**Next steps:**
- Add automated regression tests and per-stage elapsed/allocation metrics
- Validate five-credit buckets against the exact regression corpus if cold-start
	latency below one second is required
- Develop adaptive candidate pruning only after the exact baseline is covered

**Measured Release baseline (2026-09-01, empty 800-credit team, 515-player
pool):** 9.04s cold base, 4.03s with DP tables cached, 8-36ms full-result hit,
6.72s repeated auction request with DP tables warm.

**After complete Phase 1:** 1.89-1.97s cold base, 0.45-0.55s base with DP
tables cached, 0.49-0.62s first auction request after base precompute, and about
50ms for a repeated identical auction. A representative base combination reused
60,861 unique evaluations across 420,829 feasible capacity pairs (86% fewer
complete score calculations). Scenario and DP single-flight was verified with
two concurrent cold auction requests completing together in about 2.22s each.
Default, discounted auction and five interaction-heavy variants were
byte-identical to the pre-change service.

---

## Low Priority

### Unused Imports

`TeamSuggestionService.cs` imports `System.Reflection.Emit` and `System.Runtime.ConstrainedExecution` which appear unused.

---

## Changelog

| Date | Item | Status |
|------|------|--------|
| 2026-08-24 | Scoring engine rework: `StrategyWeights` (defaults = legacy), graduated same-club penalty, goal-bonus rework (value + price uplift, per-league toggle `PUT /api/leagues/{id}/goal-bonus`), format-aware + adjusted league market endpoint (`?starters=N`, `baseExpectedPrice`) | DONE |
| 2026-08-24 | Reliability-aware player value: `V = expmf' × (reg/100)^(Rel/10) × integrity tilt` (Rel default 10), S = ΣV + back-4, B = 0.5·ΣV (rotation factor), T loses the regularness term; 8 presets re-tuned (all distinct) | DONE |
| 2026-08-23 | Precompute optimal teams per team state (`TeamPrecomputer`, base-state fast path) | DONE |
| 2026-08-23 | 26-27 per-format contract: `Player.Integrity`, `PlayerPrice` table, multi-file import, format-aware suggestion pricing | DONE |
| 2026-08-10 | Suggestion engine caching Phase 1 + uniform scoring | DONE (`2f97eba`) |
| 2026-08-10 | Suggestion engine refactoring (math fixes + architecture) | DONE |
| 2026-08-09 | Suggestion engine caching (tracked) | Planned → superseded by Phase 1 |
| 2026-08-08 | Role_M import + ReadDto exposure | DONE (`faddf05`) |
| 2026-08-08 | Age import + ReadDto exposure | DONE (`faddf05`) |
| 2026-08-08 | Nullable CSV fields (Age, MyRating, Mate, Regularness, ExpMf) | DONE (`faddf05`) |
| 2026-08-08 | ML-BE contract document | DONE (`0dcf540`) |
| 2026-08-06 | Mate stored as name (consider MateId) | Open -- decision needed |
| 2026-08-06 | Tests | Open |
| 2026-08-06 | Auth middleware | Open |
| 2026-08-06 | Connection string via env vars | Open |
