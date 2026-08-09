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

### AuctionedPlayer Scoring Logic ✅ DONE

**Context:** The frontend sends `auctionedPlayer` (with `playerId` and `acquisitionPrice`) in the suggestion request to compute a "potential score" that includes this player.

**Implementation:** When `AuctionedPlayer` is set, the service forces the player into the roster at the given acquisition price, deducts from budget and slots for that role, recomputes Stage 1 only for the affected role, and reuses cached tables for the other three roles. Both base and potential scores are returned in a single call via `SuggestionResult.PotentialScore`.

**Optimization:** Stage 1 DP tables (the expensive part) run once. Only the affected role's table is recomputed for the potential path. Stage 2+3 (cheap combination + backtrack) run twice.

**Edge cases handled:** Player not found → `PotentialScore` is null. Acquisition price exceeds budget → `PotentialScore` is null.

---

### Suggestion Engine Caching

**Context:** The suggestion engine recomputes Stage 1 DP tables on every request (~1 MB, tens of ms). During live auctions, the frontend may fire rapid repeated calls with the same team state.

**What to do:** Add an in-process LRU cache keyed by a hash of `(teamId, availablePlayerIds, teamComposition)`. Cache the Stage 1 `RoleValueTable` results per role. Invalidate when the team changes (player added/removed). Small capacity (5 entries, ~5 MB max) is sufficient — the within-request optimization already avoids redundant DP work for the auctioned player path.

**Options:** `Microsoft.Extensions.Caching.Memory.MemoryCache` with size-based eviction, or a simple `ConcurrentDictionary` with manual LRU logic.

---

## Medium Priority

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
| 2026-08-09 | AuctionedPlayer forced inclusion + single-call dual score | In progress |
| 2026-08-09 | Suggestion engine caching (tracked) | Planned |
| 2026-08-08 | Role_M import + ReadDto exposure | DONE (`faddf05`) |
| 2026-08-08 | Age import + ReadDto exposure | DONE (`faddf05`) |
| 2026-08-08 | Nullable CSV fields (Age, MyRating, Mate, Regularness, ExpMf) | DONE (`faddf05`) |
| 2026-08-08 | ML-BE contract document | DONE (`0dcf540`) |
| 2026-08-06 | Mate stored as name (consider MateId) | Open -- decision needed |
| 2026-08-06 | Tests | Open |
| 2026-08-06 | Auth middleware | Open |
| 2026-08-06 | Connection string via env vars | Open |
