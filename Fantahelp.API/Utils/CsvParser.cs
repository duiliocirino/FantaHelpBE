using CsvHelper;
using System.Globalization;

namespace Fantahelp.API.Utils
{
    public class CsvParser
    {
        public static IEnumerable<PlayerCreateDto> ParsePlayers(Stream fileStream)
        {
            using var reader = new StreamReader(fileStream);
            var config = new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
            {
                PrepareHeaderForMatch = args => args.Header.ToLower(),
            };
            using var csv = new CsvReader(reader, config);
            var records = csv.GetRecords<PlayerCreateDto>().ToList();
            return records;
        }
    }
}