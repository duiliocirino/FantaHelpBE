# Future Steps

Track of pending work, ordered by priority.

---

## High Priority

### AuctionedPlayer Scoring Logic (Non-blocking, Tracked)

**Context:** The frontend sends `auctionedPlayer` (with `playerId` and `acquisitionPrice`) in the suggestion request to compute a "potential score" that includes this player. Currently the DTO field is deserialized correctly, but the `TeamSuggestionService` does not use it — the potential score returns the same value as the base score, making the convenience delta always zero.

**Files involved:**
- `Fantahelp.API/Services/TeamSuggestionService.cs`
- `Fantahelp.API/Models/Dtos/SuggestionRequest.cs`
- `Fantahelp.API/Models/Dtos/AuctionedPlayerInfo.cs`

**What to do:** When `suggestionRequest.AuctionedPlayer` is not null, look up the player by ID, set its `ExpectedPrice` to `acquisitionPrice`, and inject it into the scoring calculation alongside the current roster so the potential score reflects the added player.

---

## Medium Priority

### Role_M Handling in PlayerService

**File:** `Fantahelp.API/Services/PlayerService.cs:46`

The `Role_M` field is currently stubbed as `new List<string> { p.Role }`. Needs proper handling once the data model supports multiple roles per player.

---

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
| 2026-08-06 | AuctionedPlayer scoring logic | Open |
| 2026-08-06 | Role_M handling | Open |
| 2026-08-06 | Tests | Open |
| 2026-08-06 | Auth middleware | Open |
| 2026-08-06 | Connection string via env vars | Open |
