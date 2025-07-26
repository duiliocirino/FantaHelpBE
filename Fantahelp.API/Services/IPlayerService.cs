namespace Fantahelp.API.Services
{
    public interface IPlayerService
    {
        Task<ServiceResult<IEnumerable<Player>>> GetAllPlayersAsync();
        Task<ServiceResult<IEnumerable<Player>>> GetAllAvailablePlayersAsync(int leagueId);
        Task<ServiceResult<Player?>> GetPlayerByIdAsync(int id);
        Task<ServiceResult<bool>> ImportPlayersFromCsvAsync(IEnumerable<PlayerCreateDto> players);
    }
}