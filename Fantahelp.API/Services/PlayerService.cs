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
    }
}