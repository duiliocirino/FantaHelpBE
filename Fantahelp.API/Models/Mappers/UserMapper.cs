public static class UserMapper
{
    public static UserReadDto ToReadDto(User user)
    {
        return new UserReadDto
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email
        };
    }

    public static List<UserReadDto> ToReadDtos(IEnumerable<User> users)
    {
        return users.Select(ToReadDto).ToList();
    }
}