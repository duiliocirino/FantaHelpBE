namespace Fantahelp.API.Services
{
    public interface ILeagueService
    {
        Task<ServiceResult<IEnumerable<League>>> GetAllLeaguesAsync();
        Task<ServiceResult<League>> GetLeagueByIdAsync(int id);
        Task<ServiceResult<League>> CreateLeagueAsync(LeagueCreateDto league);
        Task<ServiceResult<bool>> DeleteLeagueAsync(int id);
        Task<ServiceResult<League>> AddTeamToLeagueAsync(int leagueId, int teamId);
        Task<ServiceResult<League>> RemoveTeamFromLeagueAsync(int leagueId, int teamId);
        Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsFromLeagueAsync(int leagueId);

        Task<ServiceResult<IEnumerable<Player>>> GetAllAvailablePlayersAsync(int leagueId);
        //Task<bool> UpdateLeagueAsync(League league);
    }
}