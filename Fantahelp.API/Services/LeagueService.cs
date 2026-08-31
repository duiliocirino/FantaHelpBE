using Microsoft.EntityFrameworkCore;
using Fantahelp.API.Services.Helpers;

namespace Fantahelp.API.Services
{
    public class LeagueService : ILeagueService
    {
        private readonly FantahelpContext _context;

        public LeagueService(FantahelpContext context)
        {
            _context = context;
        }

        public async Task<ServiceResult<League>> CreateLeagueAsync(LeagueCreateDto league)
        {
            var new_league = new League
            {
                Name = league.Name,
                InitialBudget = league.InitialBudget
            };

            await _context.Leagues.AddAsync(new_league);
            await _context.SaveChangesAsync();

            return ServiceResult<League>.SuccessResult(new_league);
        }

        public async Task<ServiceResult<League>> SetGoalBonusAsync(int id, bool goalBonusPerRole)
        {
            var league = await _context.Leagues.FindAsync(id);
            if (league == null)
                return ServiceResult<League>.FailureResult("No League is associated to the given leagueId.");

            league.GoalBonusPerRole = goalBonusPerRole;
            await _context.SaveChangesAsync();

            var updated = await _context.Leagues
                .Include(l => l.Teams)
                    .ThenInclude(t => t.Players)
                        .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(l => l.Id == id);
            return ServiceResult<League>.SuccessResult(updated!);
        }

        public async Task<ServiceResult<bool>> DeleteLeagueAsync(int id)
        {
            var league = await _context.Leagues.FindAsync(id);
            if (league == null)
                return ServiceResult<bool>.FailureResult("No League is associated to the given leagueId.");

            _context.Leagues.Remove(league);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.SuccessResult(true);
        }

        public async Task<ServiceResult<IEnumerable<League>>> GetAllLeaguesAsync()
        {
            var leagues = await _context.Leagues
                .Include(l => l.Teams)
                    .ThenInclude(t => t.Players)
                        .ThenInclude(tp => tp.Player)
                .ToListAsync();
            return ServiceResult<IEnumerable<League>>.SuccessResult(leagues);
        }

        public async Task<ServiceResult<League>> GetLeagueByIdAsync(int id)
        {
            var league = await _context.Leagues
                .Include(l => l.Teams)
                    .ThenInclude(t => t.Players)
                        .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(l => l.Id == id);
            if (league == null)
                return ServiceResult<League>.FailureResult("The given id returned 0 results.");
            return ServiceResult<League>.SuccessResult(league);
        }


        public async Task<ServiceResult<League>> AddTeamToLeagueAsync(int leagueId, int teamId)
        {
            var team = await _context.Teams.FindAsync(teamId);
            if (team == null)
                return ServiceResult<League>.FailureResult("No instance of Team was found with the given teamId.");

            var league = await _context.Leagues.FindAsync(leagueId);
            if (league == null)
                return ServiceResult<League>.FailureResult("No instance of League was found with the given leagueId.");

            league.Teams.Add(team);
            await _context.SaveChangesAsync();

            var updatedLeague = await _context.Leagues
                .Include(l => l.Teams)
                    .ThenInclude(t => t.Players)
                        .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(l => l.Id == leagueId);
            return ServiceResult<League>.SuccessResult(updatedLeague!);
        }

        public async Task<ServiceResult<League>> RemoveTeamFromLeagueAsync(int leagueId, int teamId)
        {
            var team = await _context.Teams.FindAsync(teamId);
            if (team == null)
                return ServiceResult<League>.FailureResult("No instance of Team was found with the given teamId.");

            var league = await _context.Leagues.FindAsync(leagueId);
            if (league == null)
                return ServiceResult<League>.FailureResult("No instance of League was found with the given leagueId.");

            league.Teams.Remove(team);
            await _context.SaveChangesAsync();

            var updatedLeague = await _context.Leagues
                .Include(l => l.Teams)
                    .ThenInclude(t => t.Players)
                        .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(l => l.Id == leagueId);
            return ServiceResult<League>.SuccessResult(updatedLeague!);
        }

        public async Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsFromLeagueAsync(int leagueId)
        {
            var teams = await _context.Teams
                .Where(t => t.LeagueId == leagueId)
                .Include(t => t.Players)
                .ThenInclude(tp => tp.Player)
                .ToListAsync();
            return ServiceResult<IEnumerable<Team>>.SuccessResult(teams);
        }

        public async Task<ServiceResult<IEnumerable<Player>>> GetAllAvailablePlayersAsync(int leagueId)
        {
            var players = await _context.Players
                .Where(p => p.TeamPlayers.All(tp => tp.LeagueId != leagueId))
                .ToListAsync();
            return ServiceResult<IEnumerable<Player>>.SuccessResult(players);
        }

        /// <summary>
        /// League-scoped market view: the available players plus the same price format the
        /// suggestion engine resolves for this league (credits from the league budget, starters
        /// from the request when provided). Used by the league players endpoint so displayed
        /// prices match the engine's.
        /// </summary>
        public async Task<ServiceResult<LeagueMarketData>> GetLeagueMarketDataAsync(int leagueId, int? starters)
        {
            var league = await _context.Leagues.FindAsync(leagueId);
            if (league == null)
                return ServiceResult<LeagueMarketData>.FailureResult("No League is associated to the given leagueId.");

            var players = await _context.Players
                .Where(p => p.TeamPlayers.All(tp => tp.LeagueId != leagueId))
                .ToListAsync();

            var priceFormat = await PriceFormatResolver.ResolveAsync(_context, starters, league.InitialBudget);
            var priceLookup = await PriceFormatResolver.LoadLookupAsync(_context, priceFormat);

            return ServiceResult<LeagueMarketData>.SuccessResult(new LeagueMarketData(league, players, priceLookup));
        }
    }
}