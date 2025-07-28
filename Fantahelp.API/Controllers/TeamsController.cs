using Microsoft.AspNetCore.Mvc;
using Fantahelp.API.Services;
using Fantahelp.API.Utils;

namespace Fantahelp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TeamsController : ControllerBase
    {
        private readonly ITeamService _teamService;

        public TeamsController(ITeamService teamService)
        {
            _teamService = teamService;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<TeamReadDto>?>> GetAllTeams()
        {
            var result = await _teamService.GetAllTeamsAsync();
            var teams = result.Data;

            if (teams == null)
                return NoContent();

            var teamDtos = teams.Select(t => new TeamReadDto
            {
                Id = t.Id,
                Name = t.Name,
                Players = [.. t.Players.Select(tp => new TeamPlayerReadDto
                {
                    PlayerId = tp.PlayerId,
                    PlayerName = tp.Player.Name,
                    AuctionPrice = tp.AuctionPrice
                })]
            });

            return Ok(teamDtos);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<TeamReadDto>> GetTeamById(int id)
        {
            var result = await _teamService.GetTeamByIdAsync(id);
            var team = result.Data;

            if (team == null)
                return NoContent();

            return Ok(new TeamReadDto
            {
                Id = team.Id,
                Name = team.Name,
                Players = [.. team.Players.Select(tp => new TeamPlayerReadDto
                {
                    PlayerId = tp.PlayerId,
                    PlayerName = tp.Player.Name,
                    AuctionPrice = tp.AuctionPrice
                })]
            });
        }

        [HttpPost]
        public async Task<ActionResult<TeamReadDto>> CreateTeam([FromBody] TeamCreateDto teamCreateDto)
        {
            var result = await _teamService.CreateTeamAsync(teamCreateDto);

            if (!result.Success || result.Data == null)
                return BadRequest(result.ErrorMessage);

            var team = result.Data;

            var teamReadDto = new TeamReadDto
            {
                Id = team.Id,
                Name = team.Name,
                Players = [.. team.Players.Select(tp => new TeamPlayerReadDto
                {
                    PlayerId = tp.PlayerId,
                    PlayerName = tp.Player.Name,
                    AuctionPrice = tp.AuctionPrice
                })]
            };

            return CreatedAtAction(nameof(GetTeamById), new { id = team.Id }, teamReadDto);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteTeam(int id)
        {
            var result = await _teamService.DeleteTeamAsync(id);

            if (!result.Success)
                return NotFound(result.ErrorMessage);

            return NoContent();
        }

        [HttpPost("{teamId}/players")]
        public async Task<ActionResult<TeamReadDto>> AddPlayerToTeam(int teamId, [FromBody] TeamPlayerCreateDto teamPlayerCreateDto)
        {
            var result = await _teamService.AddPlayerToTeamAsync(teamId, teamPlayerCreateDto);

            if (!result.Success || result.Data == null)
                return BadRequest(result.ErrorMessage);

            var team = result.Data;

            var teamReadDto = new TeamReadDto
            {
                Id = team.Id,
                Name = team.Name,
                Players = [.. team.Players.Select(tp => new TeamPlayerReadDto
                {
                    PlayerId = tp.PlayerId,
                    PlayerName = tp.Player.Name,
                    AuctionPrice = tp.AuctionPrice
                })]
            };

            return Ok(teamReadDto);
        }

        [HttpDelete("{teamId}/players/{playerId}")]
        public async Task<ActionResult<TeamReadDto>> RemovePlayerFromTeam(int teamId, int playerId)
        {
            var result = await _teamService.RemovePlayerFromTeamAsync(teamId, playerId);

            if (!result.Success || result.Data == null)
                return BadRequest(result.ErrorMessage);

            var team = result.Data;

            var teamReadDto = new TeamReadDto
            {
                Id = team.Id,
                Name = team.Name,
                Players = [.. team.Players.Select(tp => new TeamPlayerReadDto
                {
                    PlayerId = tp.PlayerId,
                    PlayerName = tp.Player.Name,
                    AuctionPrice = tp.AuctionPrice
                })]
            };

            return Ok(teamReadDto);
        }
    }
}