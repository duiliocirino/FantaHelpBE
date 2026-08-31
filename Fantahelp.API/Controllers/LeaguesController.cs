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

        /// <summary>
        /// Toggles the league's per-role goal bonus (GK 6 / DEF 5 / MID 4 / ATT 3 per goal).
        /// When on, the engine adjusts both the effective player values and the expected
        /// market prices (D +10%, C +5%) and the league-scoped player endpoint reports them.
        /// </summary>
        [HttpPut("{leagueId}/goal-bonus")]
        public async Task<ActionResult<LeagueReadDto>> SetGoalBonus(int leagueId, [FromBody] LeagueGoalBonusUpdateDto leagueGoalBonusUpdateDto)
        {
            var result = await _leagueService.SetGoalBonusAsync(leagueId, leagueGoalBonusUpdateDto.GoalBonusPerRole);

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

        /// <summary>
        /// League-scoped market view: available players with the same price format the
        /// suggestion engine resolves for this league (credits from the league budget,
        /// starters from the optional query param) and the league's goal-bonus adjustment
        /// applied to expected price/std/performance. <c>baseExpectedPrice</c> exposes the
        /// raw ML estimate before the adjustment.
        /// </summary>
        [HttpGet("{idLeague}/players")]
        public async Task<ActionResult<IEnumerable<PlayerReadDto>?>> GetAllAvailablePlayers(int idLeague, [FromQuery] int? starters)
        {
            var result = await _leagueService.GetLeagueMarketDataAsync(idLeague, starters);
            if (result.Success != true)
                return NotFound();
            var market = result.Data;
            if (market == null)
                return NoContent();
            var playerDtos = market.Players.Select(p =>
            {
                var (basePrice, baseStd) = market.PriceLookup != null && market.PriceLookup.TryGetValue(p.Id, out var pp)
                    ? (pp.Price, pp.Std)
                    : ((int)p.ExpectedPrice, p.ExpectedStd);
                return new PlayerReadDto
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
                    ExpectedPerformance = ScoringEngine.EffectiveExpectedPerformance(p.ExpectedPerformance, p.Role, market.League),
                    ExpectedStd = ScoringEngine.EffectivePriceStd(baseStd, p.Role, market.League),
                    ExpectedPrice = ScoringEngine.EffectiveMarketPrice(basePrice, p.Role, market.League),
                    BaseExpectedPrice = basePrice
                };
            });
            return Ok(playerDtos);
        }
    }
}