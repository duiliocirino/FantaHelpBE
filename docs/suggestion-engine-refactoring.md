# Suggestion Engine: Refactoring & Algorithmic Fixes

## Objective

Resolve mathematical anomalies in the auction suggestion engine (`POST /api/teams/getOptimal`), specifically where forced auctioned players produced negative or inflated score deltas ($\Delta$), and streamline the service architecture for maintainability and performance.

---

## 1. Mathematical & Algorithmic Fixes

### A. Budget Double-Counting Resolution

**Issue:** The potential path previously subtracted `acquisitionPrice` from `remainingBudget` while simultaneously seeding `currentPlayersTotalPrice` with the forced player's cost. This created a "phantom penalty" where the forced player's cost was deducted twice.

**Fix:** Standardized both Base and Potential combination phases to evaluate against `league.InitialBudget`. The combination phase seeds `currentPlayersTotalPrice` (which includes `AcquisitionCost` for all owned + forced players), enforcing $\sum \text{Cost} \le \text{InitialBudget}$ cleanly without double-deduction.

### B. Context-Aware Stage 1 DP Scoring

**Issue:** Stage 1 DP (`PrecomputeRoleValues`) precomputes the best $k$-player combinations per budget cell within a role. Previously, for a forced role with 8 slots (1 forced + 7 to buy), Stage 1 evaluated 7-player candidate sets in isolation without the forced player. Because lineup starters, defense bonuses ($\ge 6.0$ average of top 3 defenders + GK), and bench ratios depend on the whole role unit, Stage 1 picked 7 defenders that looked best on their own, rather than the 7 defenders that complemented the forced player best. When merged in Stage 2, this caused an artificial negative delta even at full market price.

**Fix:** Updated `PrecomputeRoleValues` to accept `forcedPlayer`. During Stage 1 evaluation of the forced role, candidate $k$-player sets are scored together with the forced player (`candidatePlayers + forcedPlayer`).

**Result:**
- At market price ($\text{AcquisitionPrice} = \text{ExpectedPrice}$): $\mathbf{\Delta = 0.00}$ (exact mathematical symmetry with the base path).
- At an auction discount ($\text{AcquisitionPrice} < \text{ExpectedPrice}$): $\mathbf{\Delta > 0.00}$ (freed credits expand budget depth across other positions).

### C. Performance-Based Lineup Starter Selection

**Issue:** Starters within a role were previously sorted by `AcquisitionCost` descending. When a top player (e.g. Dimarco, performance 6.8) was bid on at a low price (e.g. 50), sorting by cost pushed them to 5th place, benching them behind weaker defenders.

**Fix:** Updated `ScoringEngine` to select starters by `ExpectedPerformance` descending. Starters are chosen based on who delivers the highest points on the pitch.

---

## 2. Architectural & DTO Improvements

### A. Unified Twin-Run Engine Pipeline

**Old Structure:** Complex custom `ComputePotentialScore` method with manual array splicing, custom cap clamping, and duplicated combination calls.

**New Structure:** Extracted a single, stateless calculation pipeline `ComputeSingleSuggestionAsync`.

**Execution:** Runs `ComputeSingleSuggestionAsync` concurrently via `Task.WhenAll` for both Base and Potential paths. The Potential path simply adds the forced player to `currentPlayers` at `AcquisitionCost = auctionedPlayer.AcquisitionPrice` and removes them from the available pool.

### B. Immutable ScoringPlayer Projection

**Fix:** Introduced `ScoringPlayer` record separating `MarketValue` (ML prediction) from `AcquisitionCost` (price paid / bid price).

**Impact:** Completely eliminated manual EF `Player` entity cloning and fragile `ExpectedPrice` property mutation.

### C. DTO Dual Property Name Binding

**Issue:** `AuctionedPlayerInfo.cs` had `[JsonPropertyName("price")]` on `AcquisitionPrice`. If clients sent `"acquisitionPrice": 76` in JSON, System.Text.Json defaulted `AcquisitionPrice` to 0, causing the engine to simulate acquiring players for $0$ credits.

**Fix:** Configured `AuctionedPlayerInfo` to map both `"price"` and `"acquisitionPrice"` JSON keys to `AcquisitionPrice`.

### D. O(1) DP Lookups & Structured Logging

- Replaced $O(n)$ `.First(p => p.Id == id)` inside the inner DP loops with a `playerById` dictionary.
- Replaced all 22 `Console.WriteLine` calls with ASP.NET Core `ILogger<TeamSuggestionService>` (`LogInformation`, `LogDebug`, `LogWarning`).

---

## 3. Summary of System Guarantees

| Scenario | AcquisitionPrice vs MarketValue | Expected Delta ($\Delta$) | Engine Behavior |
|----------|--------------------------|---------------------|----------------|
| Market Price | $\text{AcquisitionPrice} = \text{MarketValue}$ | $\mathbf{\Delta = 0.00}$ | Potential roster & score match Base roster & score. |
| Auction Discount | $\text{AcquisitionPrice} < \text{MarketValue}$ | $\mathbf{\Delta > 0.00}$ | Freed credits reinvested in non-forced roles. |
| Overpaying | $\text{AcquisitionPrice} > \text{MarketValue}$ | $\mathbf{\Delta < 0.00}$ | Overspending constrains budget for remaining positions. |
