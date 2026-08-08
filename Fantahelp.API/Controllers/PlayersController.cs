using Microsoft.AspNetCore.Mvc;
using Fantahelp.API.Services;
using Fantahelp.API.Utils;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Fantahelp.API.Controllers
{

    [ApiController]
    [Route("api/[controller]")]
    public class PlayersController : ControllerBase
    {
        private readonly IPlayerService _playerService;

        // The IPlayerService is injected here. The controller doesn't know or care
        // how it's implemented, only that it fulfills the contract.
        public PlayersController(IPlayerService playerService)
        {
            _playerService = playerService;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<PlayerReadDto>?>> GetAllPlayers()
        {
            var result = await _playerService.GetAllPlayersAsync();
            var players = result.Data;
            if (players == null)
                return NoContent();
            var playerDtos = players.Select(p => new PlayerReadDto
            {
                Id = p.Id,
                Name = p.Name,
                Squad = p.Squad,
                Role = p.Role,
                Role_M = p.Role_M,
                Price = p.Price,
                Age = p.Age,
                Rating = p.Rating,
                Mate = p.Mate,
                Regularness = p.Regularness,
                FVM = p.FVM,
                ExpectedPerformance = p.ExpectedPerformance,
                ExpectedStd = p.ExpectedStd,
                ExpectedPrice = p.ExpectedPrice
            });
            return Ok(playerDtos);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetPlayerById(int id)
        {
            var result = await _playerService.GetPlayerByIdAsync(id);
            if (result.Success == false)
            {
                return NotFound();
            }
            var player = result.Data;
            if (player == null)
                return Conflict("This means that there is an error in the service that puts null when it should not.");
            PlayerReadDto playerDto = new PlayerReadDto
            {
                Id = player.Id,
                Name = player.Name,
                Squad = player.Squad,
                Role = player.Role,
                Role_M = player.Role_M,
                Price = player.Price,
                Age = player.Age,
                Rating = player.Rating,
                Mate = player.Mate,
                Regularness = player.Regularness,
                FVM = player.FVM,
                ExpectedPerformance = player.ExpectedPerformance,
                ExpectedStd = player.ExpectedStd,
                ExpectedPrice = player.ExpectedPrice
            };
            return Ok(playerDto);
        }

        [HttpPost("import")]
        public async Task<IActionResult> ImportPlayersFromCsv(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded.");
            }
            if (Path.GetExtension(file.FileName).ToLower() != ".csv")
            {
                return BadRequest("Invalid file type. Please upload a CSV file.");
            }

            try
            {
                // 1. Parse the CSV file into a list of DTOs
                using var stream = file.OpenReadStream();
                var playerDtos = CsvParser.ParsePlayers(stream);
                // 2. Call the service to perform the import logic
                await _playerService.ImportPlayersFromCsvAsync(playerDtos);
                // 3. Return a success response
                return Ok("Players imported successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
}