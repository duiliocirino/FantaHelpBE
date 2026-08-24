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

**Context:** The suggestion engine recomputes Stage 1 DP tables on every request. During live auctions, the frontend fires rapid repeated calls with overlapping team states.

**Phase 1 — Core cache: DONE** (`2f97eba`)

- `IMemoryCache` injected into `TeamSuggestionService` (capacity 25 entries, ~5 MB, 10min TTL, size-based LRU eviction)
- Per-role `RoleValueTable` cache key: `(role, rolePlayerIdsHash, slots, maxBudget, forcedPlayerId, currentRoleMateIdsHash, lineup, creditsDistribution, budgetAllocation)`
- Per-role player hash (not global pool hash) → P/C/A entries survive when only a defender is purchased
- Current role-mates included in Stage 1 scoring context → base and potential paths evaluate against the same role-unit composition (post-purchase score matches pre-purchase potential, gap 0.057 → 0.0007)
- No explicit invalidation on team change — cache key encodes enough context; LRU handles eviction naturally

**Impact:**
- Within-request (3 paths): ~50% reduction (6 computed, 6 hits)
- Across-request (post-purchase): ~75% reduction (P/C/A entries survive)

**Phase 2 — Invalidation on import: Open**

- Create `ICacheVersion` singleton service
- Include version in cache key
- Increment version in `PlayerService.ImportPlayersFromCsvAsync` after successful commit
- Plan: `docs/suggestion-engine-caching-plan.md`

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

See `docs/performance-improvements.md` for detailed analysis, bottleneck list and proposed improvements.

**Status:** Tracking. No implementation yet.

**Next steps:**
- Apply tighter per-role budget caps
- Candidate pruning per role
- Score memoisation

Reference: `docs/performance-improvements.md`

---

## Low Priority

### Unused Imports

`TeamSuggestionService.cs` imports `System.Reflection.Emit` and `System.Runtime.ConstrainedExecution` which appear unused.

---

## Changelog

| Date | Item | Status |
|------|------|--------|
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
