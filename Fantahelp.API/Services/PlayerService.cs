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

        public async Task<ServiceResult<IEnumerable<Player>>> GetAllPlayersAsync()
        {
            var players = await _context.Players.ToListAsync();
            return ServiceResult<IEnumerable<Player>>.SuccessResult(players);
        }

        public async Task<ServiceResult<Player?>> GetPlayerByIdAsync(int id)
        {
            // FindAsync is an efficient way to get an entity by its primary key.
            var player = await _context.Players.FindAsync(id);
            if (player == null)
                return ServiceResult<Player?>.FailureResult("The given id is not present in the database.");
            return ServiceResult<Player?>.SuccessResult(player);
        }

        public async Task<ServiceResult<bool>> ImportPlayersFromCsvAsync(IEnumerable<PlayerCreateDto> players)
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
                    // ML outputs sub-positions as semicolon-separated string (e.g., "Dd;Ds;Dc").
                    // Fallback to macro role if Role_M is absent or empty.
                    Role_M = !string.IsNullOrWhiteSpace(p.Role_M)
                        ? p.Role_M.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                        : new List<string> { p.Role },
                    Price = p.Price,
                    Age = (int)(p.Age ?? 0),
                    Rating = p.MyRating ?? 0,
                    Mate = p.Mate,
                    Regularness = p.Regularness ?? 0,
                    FVM = p.FVM,
                    ExpectedPrice = p.ExpPrice,
                    ExpectedPerformance = p.ExpMf ?? 0,
                    ExpectedStd = p.ExpStd,
                }).ToList();
                // 3. REPLACE: Add the new list of players to the context.
                await _context.Players.AddRangeAsync(newPlayers);
                // 4. SAVE: Commit the transaction.
                await _context.SaveChangesAsync();
                // If everything was successful, commit the transaction.
                await transaction.CommitAsync();
            }
            catch (Exception ex) {
                // If something went wrong, roll back the transaction.
                await transaction.RollbackAsync();
                return ServiceResult<bool>.FailureResult($"Error while loading players. Generated error was:{ex.Message}");
            }
            return ServiceResult<bool>.SuccessResult(true);
        }
    }
}