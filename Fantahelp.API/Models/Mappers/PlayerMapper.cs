public static class PlayerMapper
{
    public static PlayerReadDto ToReadDto(Player player)
    {
        return new PlayerReadDto
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
    }

    public static List<PlayerReadDto> ToReadDtos(IEnumerable<Player> players)
    {
        return players.Select(ToReadDto).ToList();
    }
}