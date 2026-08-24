namespace Fantahelp.API.Services
{
    public interface IPlayerService
    {
        Task<ServiceResult<IEnumerable<Player>>> GetAllPlayersAsync();
        Task<ServiceResult<Player?>> GetPlayerByIdAsync(int id);
        /// <summary>
        /// Imports a season from one or more per-format CSV files (e.g. 800_8, 1000_8, 1000_10).
        /// Destructive: wipes all players and player prices, then rebuilds both.
        /// </summary>
        Task<ServiceResult<bool>> ImportPlayersFromCsvAsync(IEnumerable<PlayerImportFile> files);
    }
}