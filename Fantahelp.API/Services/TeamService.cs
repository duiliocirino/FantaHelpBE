using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fantahelp.API.Services
{
    public class TeamService : ITeamService
    {
        private readonly FantahelpContext _context;

        // The DbContext is "injected" into the service via the constructor.
        public TeamService(FantahelpContext context)
        {
            _context = context;
        }

        public async Task<ServiceResult<IEnumerable<Team>>> GetAllTeamsAsync()
        {
            var teams = await _context.Teams
                .Include(t => t.Players)
                .ThenInclude(tp => tp.Player)
                .ToListAsync();
            return ServiceResult<IEnumerable<Team>>.SuccessResult(teams);
        }

        public async Task<ServiceResult<Team>> GetTeamByIdAsync(int id)
        {
            Team? team = await _context.Teams
                .Include(t => t.Players)
                .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (team == null)
                return ServiceResult<Team>.FailureResult("The given id returned 0 results.");
            return ServiceResult<Team>.SuccessResult(team);
        }

        public async Task<ServiceResult<Team>> CreateTeamAsync(TeamCreateDto team)
        {
            var owner = await _context.Users.FindAsync(team.OwnerId);
            var league = await _context.Leagues.FindAsync(team.LeagueId);

            if (owner == null)
                return ServiceResult<Team>.FailureResult("Passed owner is not a valid User.");
            if (league == null)
                return ServiceResult<Team>.FailureResult("Passed league is not a valid League.");

            var new_team = new Team
            {
                Name = team.Name,
                RemainingBudget = team.InitialBudget,
                OwnerId = team.OwnerId,
                Owner = owner,
                LeagueId = team.LeagueId,
                League = league
            };

            var entry = await _context.Teams.AddAsync(new_team);
            await _context.SaveChangesAsync();

            return ServiceResult<Team>.SuccessResult(entry.Entity);
        }

        public async Task<ServiceResult<bool>> DeleteTeamAsync(int id)
        {
            var team = await _context.Teams.FindAsync(id);
            if (team == null)
                return ServiceResult<bool>.FailureResult("No team was found with given Id.");

            _context.Teams.Remove(team);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.SuccessResult(true);
        }

        public async Task<ServiceResult<Team>> AddPlayerToTeamAsync(int teamId, TeamPlayerCreateDto teamPlayerCreateDto)
        {
            var team = await _context.Teams.FindAsync(teamId);
            var player = await _context.Players.FindAsync(teamPlayerCreateDto.PlayerId);

            if (team == null)
                return ServiceResult<Team>.FailureResult("No Team was found with the given teamId.");
            if (player == null)
                return ServiceResult<Team>.FailureResult("No Player was found with the given teamId.");

            var teamPlayer = new TeamPlayer
            {
                TeamId = teamId,
                Team = team,
                PlayerId = teamPlayerCreateDto.PlayerId,
                Player = player,
                AuctionPrice = teamPlayerCreateDto.PurchasePrice,
                League = team.League,
                LeagueId = team.LeagueId
            };

            team.Players.Add(teamPlayer);
            await _context.SaveChangesAsync();

            return ServiceResult<Team>.SuccessResult(team);
        }

        public async Task<ServiceResult<Team>> RemovePlayerFromTeamAsync(int teamId, int playerId)
        {
            var team = await _context.Teams.FindAsync(teamId);
            var player = await _context.TeamPlayers.FindAsync(teamId, playerId);

            if (team == null)
                return ServiceResult<Team>.FailureResult("No Team was found with the given teamId.");
            if (player == null)
                return ServiceResult<Team>.FailureResult("No Player was found with the given teamId.");

            team.Players.Remove(player);
            await _context.SaveChangesAsync();

            return ServiceResult<Team>.SuccessResult(team);
        }
    }
}