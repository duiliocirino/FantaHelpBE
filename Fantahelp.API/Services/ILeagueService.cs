namespace Fantahelp.API.Services
{
    public interface ILeagueService
    {
        Task<ServiceResult<IEnumerable<League>>> GetAllLeaguesAsync();
        Task<ServiceResult<League>> GetLeagueByIdAsync(int id);
        Task<ServiceResult<League>> CreateLeagueAsync(LeagueCreateDto league);
        Task<ServiceResult<bool>> DeleteLeagueAsync(int id);
        Task<ServiceResult<League>> SetGoalBonusAsync(int id, bool goalBonusPerRole);
        Task<ServiceResult<League>> AddTeamToLeagueAsync(int leagueId, int teamId);
        Task<ServiceResult<League>> RemoveTeamFromLeagueAsync(int leagueId, int teamId);
        Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsFromLeagueAsync(int leagueId);

        Task<ServiceResult<IEnumerable<Player>>> GetAllAvailablePlayersAsync(int leagueId);
        Task<ServiceResult<LeagueMarketData>> GetLeagueMarketDataAsync(int leagueId, int? starters);
        //Task<bool> UpdateLeagueAsync(League league);
    }

    /// <summary>
    /// A league's available players plus the market data the engine uses for that league:
    /// the format-resolved price lookup (credits from the league, starters from the request)
    /// and the league itself (goal-bonus rule). The controller applies the league adjustments.
    /// </summary>
    public record LeagueMarketData(
        League League,
        IReadOnlyList<Player> Players,
        Dictionary<int, (int Price, double Std)>? PriceLookup);
}