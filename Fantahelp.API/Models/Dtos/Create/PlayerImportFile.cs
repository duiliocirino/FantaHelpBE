/// <summary>
/// One per-format CSV file parsed during a season import.
/// <see cref="Credits"/> and <see cref="Starters"/> come from the file name
/// (e.g. <c>players_800_8.csv</c> -> 800, 8); <see cref="Rows"/> is its parsed content.
/// </summary>
public record PlayerImportFile(int Credits, int Starters, IReadOnlyList<PlayerCreateDto> Rows);
