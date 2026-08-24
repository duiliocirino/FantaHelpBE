using Microsoft.AspNetCore.Mvc;
using Fantahelp.API.Services;

namespace Fantahelp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LeaguesController : ControllerBase
    {
        private readonly ILeagueService _leagueService;

        public LeaguesController(ILeagueService leagueService)
        {
            _leagueService = leagueService;
        }

        [HttpPost]
        public async Task<ActionResult<LeagueReadDto>> CreateLeague([FromBody] LeagueCreateDto leagueCreateDto)
        {
            var result = await _leagueService.CreateLeagueAsync(leagueCreateDto);

            if (result.Success != true)
                return NotFound();

            var league = result.Data;
            if (league == null)
                return NoContent();

            var leagueReadDto = LeagueMapper.ToReadDto(league);
            return Ok(leagueReadDto);
        }

        [HttpDelete("{leagueId}")]
        public async Task<ActionResult<bool>> DeleteLeague(int leagueId)
        {
            var result = await _leagueService.DeleteLeagueAsync(leagueId);

            if (result.Success != true)
                return NotFound();

            var league = result.Data;
            if (league == false)
                return NoContent();

            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<LeagueReadDto>>> GetAllLeagues()
        {
            var result = await _leagueService.GetAllLeaguesAsync();

            if (result.Success != true)
                return NotFound();

            var leagues = result.Data;
            if (leagues == null)
                return NoContent();

            var leagueReadDtos = LeagueMapper.ToReadDtos(leagues);
            return Ok(leagueReadDtos);
        }

        [HttpGet("{leagueId}")]
        public async Task<ActionResult<LeagueReadDto>> GetLeagueById(int leagueId)
        {
            var result = await _leagueService.GetLeagueByIdAsync(leagueId);

            if (result.Success != true)
                return NotFound();

            var league = result.Data;
            if (league == null)
                return NoContent();

            var leagueReadDto = LeagueMapper.ToReadDto(league);
            return Ok(leagueReadDto);
        }

        [HttpPost("{leagueId}/teams/{teamId}")]
        public async Task<ActionResult<LeagueReadDto>> AddTeamToLeague(int leagueId, int teamId)
        {
            var result = await _leagueService.AddTeamToLeagueAsync(leagueId, teamId);
            if (result.Success != true)
                return NotFound();

            var league = result.Data;
            if (league == null)
                return NoContent();

            var leagueReadDto = LeagueMapper.ToReadDto(league);
            return Ok(leagueReadDto);
        }

        [HttpDelete("{leagueId}/teams/{teamId}")]
        public async Task<ActionResult<LeagueReadDto>> RemoveTeamFromLeague(int leagueId, int teamId)
        {
            var result = await _leagueService.RemoveTeamFromLeagueAsync(leagueId, teamId);
            if (result.Success != true)
                return NotFound();

            var league = result.Data;
            if (league == null)
                return NoContent();

            var leagueReadDto = LeagueMapper.ToReadDto(league);
            return Ok(leagueReadDto);
        }

        [HttpGet("{leagueId}/teams")]
        public async Task<ActionResult<IEnumerable<TeamReadDto>?>> GetAllTeamsFromLeague(int leagueId)
        {
            var result = await _leagueService.GetAllTeamsFromLeagueAsync(leagueId);
            if (result.Success != true)
                return NotFound();

            var teams = result.Data;
            if (teams == null)
                return NoContent();

            var teamDtos = teams.Select(t => TeamMapper.ToReadDto(t));
            return Ok(teamDtos);
        }

        [HttpGet("{idLeague}/players")]
        public async Task<ActionResult<IEnumerable<PlayerReadDto>?>> GetAllAvailablePlayers(int idLeague)
        {
            var result = await _leagueService.GetAllAvailablePlayersAsync(idLeague);
            if (result.Success != true)
                return NotFound();
            var players = result.Data;
            if (players == null)
                return NoContent();
            var playerDtos = players.Select(p => new PlayerReadDto
            {
                Id = p.Id,
                Name = p.Name,
                Squad = p.Squad,
                Role = p.Role,
                Price = p.Price,
                Rating = p.Rating,
                Regularness = p.Regularness,
                Integrity = p.Integrity,
                FVM = p.FVM,
                ExpectedPerformance = p.ExpectedPerformance,
                ExpectedStd = p.ExpectedStd,
                ExpectedPrice = p.ExpectedPrice
            });
            return Ok(playerDtos);
        }
    }
}