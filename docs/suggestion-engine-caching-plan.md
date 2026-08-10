# Suggestion Engine Caching — Implementation Plan

## Goal

Reduce DP table recomputation during live auctions by caching Stage 1 `RoleValueTable` results per role. The cache serves two purposes:

1. **Within-request reuse** — base, potential, and without paths share DP tables
2. **Across-request reuse** — "without player" tables pre-warm for post-purchase calls

---

## Problem

Each `POST /api/teams/getOptimal` call with an auctioned player computes 12 DP tables (4 roles × 3 paths). During live auctions, the frontend fires rapid repeated calls as the user browses players and adjusts bids. Most of these tables are redundant.

---

## Design

### Cache Granularity

Per-role `RoleValueTable` entries, not full results. DP tables are the expensive unit (~tens of ms each) and the most reusable across paths and requests.

### Cache Key

```
(string role, long rolePlayerIdsHash, int slots, int maxBudget, int? forcedPlayerId, string lineupHash)
```

| Component | Purpose | Example |
|-----------|---------|---------|
| `role` | Role identifier | `"D"` |
| `rolePlayerIdsHash` | Hash of available player IDs for this role only | `12847...` |
| `slots` | Players to buy for this role | `7` |
| `maxBudget` | DP table budget dimension | `240` |
| `forcedPlayerId` | Forced player included in scoring context (null for base/without) | `254` |
| `lineupHash` | Lineup configuration (defenders-midfielders-attackers) | `"4-4-3"` |

**Per-role player hash** (not global pool hash): when a defender is purchased, only the D hash changes. P/C/A entries remain valid and reusable.

### Storage

`IMemoryCache` (ASP.NET Core DI), injected into `TeamSuggestionService`.

- **Capacity:** 25 entries (size-based eviction)
- **Estimated memory:** ~5 MB (25 × ~200 KB average per table)
- **Eviction:** LRU via `MemoryCache` size-based removal
- **TTL:** 10 minutes absolute expiration (safety net for stale entries)
- **Scope:** Application-lifetime (persists across requests, cleared on data events)

### Invalidation

| Event | Action | Rationale |
|-------|--------|-----------|
| Player purchased/sold from team | **Nothing** — per-role hash handles it naturally | Only the sold player's role hash changes; other roles' entries stay valid |
| Player re-import (`POST /api/players/import`) | **Clear all** | Every player's stats may have changed → all DP tables stale |
| App restart | N/A — in-process cache dies with process | — |

No team-specific scoping needed — the cache key encodes enough context (player pool, slots, budget, lineup) that entries from different teams coexist without conflict.

---

## Impact Analysis

### Within a single request (auctioned player = Dimarco, role D)

| Path | P | D | C | A | Result |
|------|---|---|---|---|--------|
| Base | miss | miss | miss | miss | 4 computed |
| Potential | **hit** | miss | **hit** | **hit** | 1 computed, 3 hits |
| Without | **hit** | miss | **hit** | **hit** | 1 computed, 3 hits |
| **Total** | | | | | **6 computed, 6 hits (50% reduction)** |

- P/C/A are identical across base and without paths (no forced player, same per-role pool)
- D differs per path: base (no forced, 8 slots), potential (forced Dimarco, 7 slots), without (no forced, 7 slots, Dimarco excluded from pool)

### Across requests (after purchasing Dimarco)

| Next request (base only) | P | D | C | A | Result |
|--------------------------|---|---|---|---|--------|
| Team has Dimarco, pool excludes him | **hit** | miss | **hit** | **hit** | 1 computed, 3 hits |

The "without" path's P/C/A tables from the previous request are still cached with the correct per-role hashes. D recomputes with 7 slots (new hash). **~75% reduction.**

---

## Implementation Details

### Files to modify

| File | Change |
|------|--------|
| `Fantahelp.API/Services/TeamSuggestionService.cs` | Inject `IMemoryCache`, add cache lookup/insert in `PrecomputeRoleValues`, add cache key builder |
| `Fantahelp.API/Services/PlayerService.cs` | Clear cache after successful import |
| `Fantahelp.API/Program.cs` | Register `IMemoryCache` (already available via `AddMemoryCache()`) |

### Cache key builder (static helper)

```csharp
private static string BuildDpCacheKey(
    string role,
    IReadOnlyList<int> rolePlayerIds,
    int slots,
    int maxBudget,
    int? forcedPlayerId,
    LineUp lineup)
{
    // Deterministic hash from sorted player IDs
    var hash = rolePlayerIds.OrderBy(id => id).Aggregate(0, (h, id) => h ^ id.GetHashCode() * 31);
    var lineupKey = $"{lineup.Defenders}-{lineup.Midfielders}-{lineup.Attackers}";
    return $"dp:{role}:{hash}:{slots}:{maxBudget}:{forcedPlayerId ?? -1}:{lineupKey}";
}
```

### Cache lookup in `PrecomputeRoleValues`

```csharp
// Before DP computation:
var cacheKey = BuildDpCacheKey(role, players.Select(p => p.Id).ToList(), slots, maxBudget, forcedPlayer?.Id, suggestionRequest.LineUp);
if (_cache.TryGetValue(cacheKey, out RoleValueTable? cached))
{
    _logger.LogDebug("[Cache hit] {CacheKey}", cacheKey);
    return cached;
}

// ... existing DP computation ...

// After DP computation:
_cache.Set(cacheKey, result, new MemoryCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(10),
    Size = 1,
});
```

### Cache clear on import

In `PlayerService.ImportPlayersFromCsvAsync`, after successful commit:

```csharp
// Inject IMemoryCache into PlayerService
await _cache.ClearAsync(); // or iterate and remove
```

`IMemoryCache` doesn't have `Clear()`. Options:
1. **Cache version token** — store a `long` version in cache; increment on import; include version in key. Old entries become unreachable.
2. **`IHostApplicationLifetime` callback** — register a signal from PlayerService.
3. **`ICacheInvalidator` interface** — shared service with a `Clear()` method wrapping the underlying cache.

**Recommended: Option 1 (version token).** Simplest, no cross-service coupling.

```csharp
// In TeamSuggestionService:
private long CacheVersion => _cache.GetOrCreate("cache_version", e => _cacheVersion.Value);

// In cache key: prepend version
return $"v{version}:dp:{role}:...";

// In PlayerService (after import):
_cache.Set("cache_version", Interlocked.Increment(ref _sharedVersion), ...);
```

Actually, simpler: use a shared `volatile int` or `Lazy<int>` via a small `ICacheVersion` service registered as singleton. Both services reference the same version. On import, increment. On lookup, read.

---

## Phased Rollout

### Phase 1 — Core cache (Target: this session)

- [ ] Add `IMemoryCache` injection to `TeamSuggestionService`
- [ ] Implement `BuildDpCacheKey` helper
- [ ] Add cache lookup/insert in `PrecomputeRoleValues`
- [ ] Add cache hit/miss logging (`_logger.LogDebug`)
- [ ] Verify build, smoke test with auctioned player scenario

### Phase 2 — Invalidation on import

- [ ] Create `ICacheVersion` singleton service
- [ ] Include version in cache key
- [ ] Increment version in `PlayerService.ImportPlayersFromCsvAsync` after successful commit
- [ ] Verify old entries become unreachable after import

### Phase 3 — Observability (Deferred)

- [ ] Expose cache hit rate via `ILogger` periodic summary or metrics endpoint
- [ ] Tune capacity/TTL based on observed patterns

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Hash collision (different player sets → same hash) | Incorrect cache hit, wrong results | Use stronger hash (e.g., `xxHash` or include count + sum + XOR combo). Low probability with current player count (~530). |
| Memory pressure at 25 entries | ~5 MB is small, but large DP tables could exceed | `MemoryCache` size-based eviction handles this. Monitor. |
| Stale entries (player stats changed without import) | Wrong results | Import is the only mutation path. TTL safety net covers edge cases. |
| CreditsDistribution / BudgetAllocation not in key | Wrong cache hit when these change | Include `CreditsDistribution` and `BudgetAllocation` hash in the key. These affect scoring, which affects DP results. |

**Action item:** `CreditsDistribution` and `BudgetAllocation` must be part of the cache key. Different distribution values produce different DP table scores. Same for budget allocation (changes `maxBudget`).

Updated cache key:
```
(string role, long rolePlayerIdsHash, int slots, int maxBudget, int? forcedPlayerId, string lineupHash, int creditsDistribution, long? budgetAllocHash)
```

`maxBudget` already captures budget allocation indirectly, but `creditsDistribution` affects scoring inside the DP loop and is NOT captured by `maxBudget`. Must be explicit.

---

## Verification Plan

### Build
```bash
dotnet build Fantahelp.API
```

### Smoke test (auctioned player scenario)

1. Start API: `dotnet run --project Fantahelp.API`
2. Call `POST /api/teams/getOptimal` with Dimarco at 50
3. Check logs for cache hit/miss pattern:
   - Base: 4 misses
   - Potential: 3 hits (P/C/A), 1 miss (D)
   - Without: 3 hits (P/C/A), 1 miss (D)
4. Purchase Dimarco
5. Call again (base only)
6. Check logs: 3 hits (P/C/A from without path), 1 miss (D)

### Regression check

- Call without `auctionedPlayer` → verify scores match pre-cache baseline
- Call with different `creditsDistribution` → verify cache doesn't return wrong entry
- Call with different `budgetAllocation` → verify cache doesn't return wrong entry

---

## Changelog

| Date | Item | Status |
|------|------|--------|
| 2026-08-10 | Initial plan drafted | Draft |
