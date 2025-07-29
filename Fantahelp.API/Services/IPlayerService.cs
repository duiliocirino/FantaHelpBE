namespace Fantahelp.API.Services
{
    public interface IPlayerService
    {
        Task<ServiceResult<IEnumerable<Player>>> GetAllPlayersAsync();
        Task<ServiceResult<Player?>> GetPlayerByIdAsync(int id);
        Task<ServiceResult<bool>> ImportPlayersFromCsvAsync(IEnumerable<PlayerCreateDto> players);
    }
}