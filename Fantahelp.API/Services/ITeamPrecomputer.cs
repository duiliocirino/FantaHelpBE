namespace Fantahelp.API.Services
{
    /// <summary>
    /// Keeps the optimal team suggestion for each team's current state always
    /// precomputed (base state only: no auctioned player). Results are cached
    /// in-memory and are invalidated + recomputed when a team's roster changes
    /// or when player data is re-imported.
    ///
    /// Design (see docs/FUTURE_STEPS.md, "Precompute optimal teams per team state"):
    /// - Base state only: the auctioned player changes constantly during a live
    ///   auction and those runs are already cheap (shared DP tables).
    /// - In-memory only: a restart just means the first call per state is slow again.
    /// - Triggers: roster mutation (add/remove player) and player import.
    ///   Manual league changes are rare and the FE's next request refreshes anyway.
    /// </summary>
    public interface ITeamPrecomputer
    {
        /// <summary>
        /// Current player-data version. Bumped on every player import and part of the
        /// result cache key, so a re-import can never return a stale result even when
        /// player ids are reused across seasons.
        /// </summary>
        long DataVersion { get; }

        /// <summary>Returns the cached base-state result for the given cache key, if present.</summary>
        SuggestionResult? GetCachedResult(string cacheKey);

        /// <summary>
        /// Stores a base-state result under the given cache key and remembers the
        /// request params for this team, so post-mutation recomputes know what to build.
        /// </summary>
        void StoreResult(int teamId, string cacheKey, SuggestionRequest request, SuggestionResult result);

        /// <summary>Debounced recompute trigger: a team's roster changed (player added/removed).</summary>
        void NotifyTeamRosterChanged(int teamId);

        /// <summary>
        /// Recompute trigger: all player data changed (import). Bumps the data version
        /// (invalidating every cached result) and refreshes all known teams.
        /// </summary>
        void NotifyPlayerDataChanged();
    }
}
