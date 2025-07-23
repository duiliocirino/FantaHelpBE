using Microsoft.EntityFrameworkCore;

namespace Fantahelp.API.Services
{
    public class PlayerService : IPlayerService
    {
        private readonly FantahelpContext _context;

        // The DbContext is "injected" into the service via the constructor.
        public PlayerService(FantahelpContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Player>> GetAllPlayersAsync()
        {
            // For now, "available" just means all players in the DB.
            // Later, this logic will become more complex (e.g., players not yet on a team).
            return await _context.Players.ToListAsync();
        }

        public async Task<Player?> GetPlayerByIdAsync(int id)
        {
            // FindAsync is an efficient way to get an entity by its primary key.
            return await _context.Players.FindAsync(id);
        }

        public async Task ImportPlayersFromCsvAsync(IEnumerable<PlayerCreateDto> players)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // 1. WIPE: Delete all existing players.
                // We use ExecuteDeleteAsync for a fast, bulk delete operation.
                await _context.Players.ExecuteDeleteAsync();
                // 2. PREPARE: Map the DTOs to our domain model.
                var newPlayers = players.Select(p => new Player
                {
                    Id = p.Id,
                    Name = p.Name,
                    Squad = p.Squad,
                    Role = p.Role,
                    Role_M = new List<string> { p.Role }, //TODO: handle correctly
                    Price = p.Price,
                    Rating = p.MyRating,
                    Mate = p.Mate,
                    Regularness = p.Regularness,
                    FVM = p.FVM,
                    ExpectedPrice = p.ExpPrice,
                    ExpectedPerformance = p.ExpMf,
                    ExpectedStd = p.ExpStd,
                }).ToList();
                // 3. REPLACE: Add the new list of players to the context.
                await _context.Players.AddRangeAsync(newPlayers);
                // 4. SAVE: Commit the transaction.
                await _context.SaveChangesAsync();
                // If everything was successful, commit the transaction.
                await transaction.CommitAsync();
            }
            catch (Exception){
                // If something went wrong, roll back the transaction.
                await transaction.RollbackAsync();
                throw; // Re-throw the exception to be handled by the caller.
            }
        }
    }
}