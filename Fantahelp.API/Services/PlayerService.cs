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

        public async Task<ServiceResult<IEnumerable<Player>>> GetAllPlayersAsync()
        {
            var players = await _context.Players.ToListAsync();
            return ServiceResult<IEnumerable<Player>>.SuccessResult(players);
        }

        public async Task<ServiceResult<Player?>> GetPlayerByIdAsync(int id)
        {
            // FindAsync is an efficient way to get an entity by its primary key.
            var player = await _context.Players.FindAsync(id);
            if (player == null)
                return ServiceResult<Player?>.FailureResult("The given id is not present in the database.");
            return ServiceResult<Player?>.SuccessResult(player);
        }

        /// <summary>
        /// Reference format used to populate the legacy single-format columns
        /// <c>Player.ExpectedPrice</c>/<c>Player.ExpectedStd</c> (kept for FE backward
        /// compatibility while consumers migrate to per-format <c>PlayerPrice</c> rows).
        /// </summary>
        private const int ReferenceFormatCredits = 800;
        private const int ReferenceFormatStarters = 8;

        public async Task<ServiceResult<bool>> ImportPlayersFromCsvAsync(IEnumerable<PlayerImportFile> files)
        {
            // Deterministic order: the first (sorted) file provides the base player data.
            var importFiles = files.OrderBy(f => (f.Credits, f.Starters)).ToList();
            if (importFiles.Count == 0)
                return ServiceResult<bool>.FailureResult("No import files provided.");

            // Fail fast: all format files must describe the same roster with identical base data
            // (only expprice/expstd may differ), otherwise the base data would be ambiguous.
            var consistencyError = ValidateFilesConsistency(importFiles);
            if (consistencyError != null)
                return ServiceResult<bool>.FailureResult(consistencyError);

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // 1. WIPE: Delete all existing prices and players.
                // We use ExecuteDeleteAsync for a fast, bulk delete operation.
                await _context.PlayerPrices.ExecuteDeleteAsync();
                await _context.Players.ExecuteDeleteAsync();
                // 2. PREPARE: Map the DTOs to our domain model. Base data comes from the
                // first file (validated identical across all format files).
                var referenceFile = importFiles
                    .FirstOrDefault(f => f.Credits == ReferenceFormatCredits && f.Starters == ReferenceFormatStarters);
                var referenceLookup = referenceFile?.Rows.ToDictionary(r => r.Id);
                var newPlayers = importFiles[0].Rows.Select(p =>
                {
                    // Legacy bridge columns: format-specific values from the reference format file
                    // when present, otherwise the first file's values.
                    var referenceRow = referenceLookup != null && referenceLookup.TryGetValue(p.Id, out var refRow)
                        ? refRow
                        : p;
                    return new Player
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Squad = p.Squad,
                        Role = p.Role,
                        // ML outputs sub-positions as semicolon-separated string (e.g., "Dd;Ds;Dc").
                        // Fallback to macro role if Role_M is absent or empty.
                        Role_M = !string.IsNullOrWhiteSpace(p.Role_M)
                            ? p.Role_M.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                            : new List<string> { p.Role },
                        Price = p.Price,
                        Age = (int)(p.Age ?? 0),
                        Rating = p.MyRating ?? 0,
                        Mate = p.Mate,
                        Regularness = p.Regularness ?? 0,
                        // Integrity is optional: null stays null (never defaulted to a middle value).
                        Integrity = (int?)p.Integrity,
                        FVM = p.FVM,
                        ExpectedPrice = referenceRow.ExpPrice,
                        ExpectedPerformance = p.ExpMf ?? 0,
                        ExpectedStd = referenceRow.ExpStd,
                    };
                }).ToList();
                // 3. PREPARE: One price row per player x format.
                var newPrices = importFiles
                    .SelectMany(f => f.Rows.Select(r => new PlayerPrice
                    {
                        PlayerId = r.Id,
                        Credits = f.Credits,
                        Starters = f.Starters,
                        Price = r.ExpPrice,
                        Std = r.ExpStd,
                    }))
                    .ToList();
                // 4. REPLACE: Add the new players and price rows to the context.
                await _context.Players.AddRangeAsync(newPlayers);
                await _context.PlayerPrices.AddRangeAsync(newPrices);
                // 5. SAVE: Commit the transaction.
                await _context.SaveChangesAsync();
                // If everything was successful, commit the transaction.
                await transaction.CommitAsync();
            }
            catch (Exception ex) {
                // If something went wrong, roll back the transaction.
                await transaction.RollbackAsync();
                return ServiceResult<bool>.FailureResult($"Error while loading players. Generated error was:{ex.Message}");
            }
            return ServiceResult<bool>.SuccessResult(true);
        }

        /// <summary>
        /// Checks that all format files cover the same player ids and carry identical base data
        /// (everything except expprice/expstd). Returns an error message, or null when consistent.
        /// </summary>
        private static string? ValidateFilesConsistency(List<PlayerImportFile> importFiles)
        {
            var baseFile = importFiles[0];
            var baseIds = baseFile.Rows.Select(r => r.Id).ToHashSet();
            var baseLookup = baseFile.Rows.ToDictionary(r => r.Id);

            for (int i = 1; i < importFiles.Count; i++)
            {
                var file = importFiles[i];
                var fileIds = file.Rows.Select(r => r.Id).ToHashSet();
                if (!fileIds.SetEquals(baseIds))
                    return $"Format files players_{baseFile.Credits}_{baseFile.Starters}.csv and " +
                           $"players_{file.Credits}_{file.Starters}.csv do not cover the same player ids.";

                foreach (var row in file.Rows)
                {
                    var baseRow = baseLookup[row.Id];
                    if (row.Role != baseRow.Role || row.Role_M != baseRow.Role_M ||
                        row.Name != baseRow.Name || row.Squad != baseRow.Squad ||
                        row.Price != baseRow.Price || row.Age != baseRow.Age ||
                        row.MyRating != baseRow.MyRating || row.Mate != baseRow.Mate ||
                        row.Regularness != baseRow.Regularness || row.Integrity != baseRow.Integrity ||
                        row.FVM != baseRow.FVM || row.ExpMf != baseRow.ExpMf)
                    {
                        return $"Base data for player {row.Id} ({row.Name}) differs between format files.";
                    }
                }
            }
            return null;
        }
    }
}