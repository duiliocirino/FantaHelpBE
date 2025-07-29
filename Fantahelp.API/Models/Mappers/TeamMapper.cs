public static class TeamMapper
{
    public static TeamReadDto ToReadDto(Team team)
    {
        return new TeamReadDto
        {
            Id = team.Id,
            Name = team.Name,
            Players = TeamPlayerMapper.ToReadDtos(team.Players)
        };
    }

    public static List<TeamReadDto> ToReadDtos(IEnumerable<Team> teams)
    {
        return teams.Select(ToReadDto).ToList();
    }
}