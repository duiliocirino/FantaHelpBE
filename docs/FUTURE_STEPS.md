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

**Mate detail:** Currently stored as player name string. Open question: should BE resolve names to IDs post-import (two-pass), or should ML output mate's SkyBet ID directly?

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

## Low Priority

### Unused Imports

`TeamSuggestionService.cs` imports `System.Reflection.Emit` and `System.Runtime.ConstrainedExecution` which appear unused.

---

## Changelog

| Date | Item | Status |
|------|------|--------|
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
