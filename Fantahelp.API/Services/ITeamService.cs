namespace Fantahelp.API.Services
{
    public interface ITeamService
    {
        Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsAsync();
        Task<ServiceResult<Team>> GetTeamByIdAsync(int teamId);
        Task<ServiceResult<Team>> CreateTeamAsync(TeamCreateDto team);
        Task<ServiceResult<bool>> DeleteTeamAsync(int teamId);
        Task<ServiceResult<Team>> AddPlayerToTeamAsync(int teamId, TeamPlayerCreateDto teamPlayerCreateDto);
        Task<ServiceResult<Team>> RemovePlayerFromTeamAsync(int teamId, int playerId);
    }
}