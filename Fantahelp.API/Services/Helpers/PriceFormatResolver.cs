using Microsoft.EntityFrameworkCore;

namespace Fantahelp.API.Services.Helpers
{
    /// <summary>
    /// Resolves which per-format price data (credits, starters) a league should use.
    /// Shared by the suggestion engine and the league-scoped player read endpoint so
    /// both always see the same prices.
    /// </summary>
    public static class PriceFormatResolver
    {
        /// <summary>
        /// The exact (credits, starters) format when ML data exists for it, otherwise the
        /// closest available format (by credits distance, then starters distance; when
        /// starters is unknown, by credits distance then ascending starters). Returns null
        /// when no per-format price data exists at all (legacy single-format import);
        /// callers then fall back to Player.ExpectedPrice/ExpectedStd.
        /// </summary>
        public static async Task<(int Credits, int Starters)?> ResolveAsync(FantahelpContext context, int? starters, int credits)
        {
            var rows = await context.PlayerPrices
                .Select(pp => new { pp.Credits, pp.Starters })
                .Distinct()
                .ToListAsync();
            var formats = rows.GroupBy(r => (r.Credits, r.Starters)).Select(g => g.Key).ToList();
            if (formats.Count == 0)
                return null;
            return formats
                .OrderBy(f => Math.Abs(f.Credits - credits))
                .ThenBy(f => starters.HasValue ? Math.Abs(f.Starters - starters.Value) : 0)
                .ThenBy(f => f.Credits) // deterministic tie-break
                .ThenBy(f => f.Starters)
                .First();
        }

        /// <summary>Loads per-player (expected price, std) for the resolved format. Null when the format is null.</summary>
        public static async Task<Dictionary<int, (int Price, double Std)>?> LoadLookupAsync(FantahelpContext context, (int Credits, int Starters)? priceFormat)
        {
            if (priceFormat == null)
                return null;
            var rows = await context.PlayerPrices
                .Where(pp => pp.Credits == priceFormat.Value.Credits && pp.Starters == priceFormat.Value.Starters)
                .Select(pp => new { pp.PlayerId, pp.Price, pp.Std })
                .ToListAsync();
            return rows.ToDictionary(r => r.PlayerId, r => (r.Price, r.Std));
        }
    }
}
