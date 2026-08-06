public static class LeagueMapper
{
    public static LeagueReadDto ToReadDto(League league)
    {
        return new LeagueReadDto
        {
            Id = league.Id,
            Name = league.Name,
            InitialBudget = league.InitialBudget,
            GoalBonusPerRole = league.GoalBonusPerRole,
            Teams = [.. league.Teams.Select(TeamMapper.ToReadDto)]
        };
    }

    public static List<LeagueReadDto> ToReadDtos(IEnumerable<League> leagues)
    {
        return leagues.Select(ToReadDto).ToList();
    }
}