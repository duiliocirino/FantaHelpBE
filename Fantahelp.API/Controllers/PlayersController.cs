using Microsoft.AspNetCore.Mvc;
using Fantahelp.API.Services;
using Fantahelp.API.Utils;

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
        public async Task<ActionResult<IEnumerable<PlayerReadDto>>> GetAllPlayers()
        {
            var players = await _playerService.GetAllPlayersAsync();
            return Ok(players);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetPlayerById(int id)
        {
            var player = await _playerService.GetPlayerByIdAsync(id);
            if (player == null)
            {
                return NotFound();
            }
            return Ok(player);
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