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
