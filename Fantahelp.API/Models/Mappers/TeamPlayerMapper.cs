public static class TeamPlayerMapper
{
    public static TeamPlayerReadDto ToReadDto(TeamPlayer teamPlayer)
    {
        return new TeamPlayerReadDto
        {
            TeamId = teamPlayer.TeamId,
            PlayerId = teamPlayer.PlayerId,
            PlayerName = teamPlayer.Player.Name,
            PlayerRole = teamPlayer.Player.Role,
            AuctionPrice = teamPlayer.AuctionPrice
        };
    }

    public static List<TeamPlayerReadDto> ToReadDtos(IEnumerable<TeamPlayer> teamPlayers)
    {
        return teamPlayers.Select(ToReadDto).ToList();
    }
}