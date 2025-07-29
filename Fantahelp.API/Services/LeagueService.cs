using Microsoft.EntityFrameworkCore;

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
            var leagues = await _context.Leagues.ToListAsync();
            return ServiceResult<IEnumerable<League>>.SuccessResult(leagues);
        }

        public async Task<ServiceResult<League>> GetLeagueByIdAsync(int id)
        {
            var league = await _context.Leagues.FindAsync(id);
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

            return ServiceResult<League>.SuccessResult(league);
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

            return ServiceResult<League>.SuccessResult(league);
        }

        public async Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsFromLeagueAsync(int leagueId)
        {
            var teams = await _context.Teams
                .Where(t => t.LeagueId == leagueId)
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
    }
}