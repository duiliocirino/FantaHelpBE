using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fantahelp.API.Services
{
    /// <summary>
    /// In-memory precomputer for base-state team suggestions (see <see cref="ITeamPrecomputer"/>).
    ///
    /// Lifecycle: the suggestion service stores the result of every base-state run and
    /// remembers the request params per team. Roster mutations and player imports trigger
    /// a debounced recompute, which runs the normal suggestion pipeline in its own DI scope.
    /// The debounce coalesces rapid mutation sequences (e.g. a transfer = remove + add)
    /// and cancels superseded runs.
    /// </summary>
    public class TeamPrecomputer : ITeamPrecomputer, IDisposable
    {
        private readonly IMemoryCache _results;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TeamPrecomputer> _logger;

        /// <summary>Last requested params per team (source for recompute after mutations).</summary>
        private readonly ConcurrentDictionary<int, SuggestionRequest> _lastParams = new();

        /// <summary>One pending (debounced) recompute per team.</summary>
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _pending = new();

        /// <summary>Bumped on every player import; part of the result cache key.</summary>
        private long _dataVersion;

        /// <summary>Result TTL, aligned with the DP table cache (10 minutes).</summary>
        private static readonly TimeSpan ResultTtl = TimeSpan.FromMinutes(10);

        /// <summary>Debounce window for coalescing rapid roster mutations.</summary>
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

        public TeamPrecomputer(IServiceScopeFactory scopeFactory, ILogger<TeamPrecomputer> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;

            // Dedicated cache instance: result entries must not compete with the shared
            // DP table cache, which is registered with a global SizeLimit of 25.
            _results = new MemoryCache(new MemoryCacheOptions());
        }

        public long DataVersion => Interlocked.Read(ref _dataVersion);

        public SuggestionResult? GetCachedResult(string cacheKey)
            => _results.TryGetValue<SuggestionResult>(cacheKey, out var result) ? result : null;

        public void StoreResult(int teamId, string cacheKey, SuggestionRequest request, SuggestionResult result)
        {
            _results.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow + ResultTtl,
            });

            // Remember the params (base-state view) so a later recompute can rebuild the request.
            _lastParams[teamId] = new SuggestionRequest
            {
                TeamId = teamId,
                NumTeams = request.NumTeams,
                FavoritePlayerIds = request.FavoritePlayerIds,
                LineUp = request.LineUp,
                CreditsDistribution = request.CreditsDistribution,
                BudgetAllocation = request.BudgetAllocation,
                Weights = request.Weights,
                AuctionedPlayer = null,
            };
        }

        public void NotifyTeamRosterChanged(int teamId) => ScheduleRefresh(teamId);

        public void NotifyPlayerDataChanged()
        {
            Interlocked.Increment(ref _dataVersion);
            _logger.LogInformation("Player data changed: precomputed results invalidated (version {Version}).", DataVersion);

            foreach (var teamId in _lastParams.Keys)
                ScheduleRefresh(teamId);
        }

        /// <summary>Cancels any pending recompute for the team and schedules a new debounced one.</summary>
        private void ScheduleRefresh(int teamId)
        {
            // Nothing to precompute for a team that never ran a base-state suggestion.
            if (!_lastParams.ContainsKey(teamId))
                return;

            if (_pending.TryRemove(teamId, out var old))
                old.Cancel();

            var cts = new CancellationTokenSource();
            _pending[teamId] = cts;
            _ = DebouncedRefreshAsync(teamId, cts);
        }

        private async Task DebouncedRefreshAsync(int teamId, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(DebounceDelay, cts.Token);

                if (!_lastParams.TryGetValue(teamId, out var last))
                    return;

                // Recompute through the normal pipeline; it stores the fresh result itself.
                // A new scope is required because ITeamSuggestionService is scoped.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var suggestionService = scope.ServiceProvider.GetRequiredService<ITeamSuggestionService>();
                var result = await suggestionService.GetOptimalTeamSuggestionAsync(last);

                if (result.Success)
                    _logger.LogInformation("Precomputed optimal team for team {TeamId} (version {Version}).", teamId, DataVersion);
                else
                    _logger.LogWarning("Precompute for team {TeamId} failed: {Error}.", teamId, result.ErrorMessage);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer mutation -- expected, not an error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Precompute for team {TeamId} threw.", teamId);
            }
            finally
            {
                // Only remove the entry if it is still ours (a newer schedule may have replaced it).
                if (_pending.TryGetValue(teamId, out var current) && ReferenceEquals(current, cts))
                    _pending.TryRemove(teamId, out _);
            }
        }

        public void Dispose()
        {
            foreach (var cts in _pending.Values)
                cts.Cancel();
        }
    }
}
