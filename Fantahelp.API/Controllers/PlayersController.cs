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
                Integrity = p.Integrity,
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
                Integrity = player.Integrity,
                FVM = player.FVM,
                ExpectedPerformance = player.ExpectedPerformance,
                ExpectedStd = player.ExpectedStd,
                ExpectedPrice = player.ExpectedPrice
            };
            return Ok(playerDto);
        }

        // Per-format CSV file naming: players_{credits}_{starters}.csv (e.g. players_800_8.csv).
        // The format is derived from the file name; the file content carries the data.
        private static readonly System.Text.RegularExpressions.Regex FormatFileName =
            new(@"^players_(\d+)_(\d+)\.csv$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Imports a season from one or more per-format CSV files (form field <c>files</c>),
        /// e.g. <c>players_800_8.csv</c>, <c>players_1000_8.csv</c>, <c>players_1000_10.csv</c>.
        /// Destructive: replaces all players and per-format price rows.
        /// </summary>
        [HttpPost("import")]
        public async Task<IActionResult> ImportPlayersFromCsv(IFormFile[] files)
        {
            if (files == null || files.Length == 0)
            {
                return BadRequest("No files uploaded.");
            }

            var importFiles = new List<PlayerImportFile>();
            foreach (var file in files)
            {
                if (file.Length == 0)
                {
                    return BadRequest($"File '{file.FileName}' is empty.");
                }

                var match = FormatFileName.Match(Path.GetFileName(file.FileName));
                if (!match.Success)
                {
                    return BadRequest(
                        $"File '{file.FileName}' does not match the expected naming 'players_{{credits}}_{{starters}}.csv' (e.g. players_800_8.csv).");
                }

                var credits = int.Parse(match.Groups[1].Value);
                var starters = int.Parse(match.Groups[2].Value);
                if (importFiles.Any(f => f.Credits == credits && f.Starters == starters))
                {
                    return BadRequest($"Duplicate format: multiple files uploaded for {credits}_{starters}.");
                }

                try
                {
                    // Parse each file into DTOs; a malformed CSV is a client error.
                    using var stream = file.OpenReadStream();
                    importFiles.Add(new PlayerImportFile(credits, starters, CsvParser.ParsePlayers(stream).ToList()));
                }
                catch (Exception ex)
                {
                    return BadRequest($"Failed to parse '{file.FileName}': {ex.Message}");
                }
            }

            // Call the service to perform the import logic.
            var result = await _playerService.ImportPlayersFromCsvAsync(importFiles);
            // The service rolls back internally on failure, so a non-success result
            // means the database is unchanged. Report the error instead of a false success.
            if (result.Success != true)
                return StatusCode(500, result.ErrorMessage);
            return Ok("Players imported successfully.");
        }
    }
}