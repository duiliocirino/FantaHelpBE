public class UserCreateDto
{
    public required string UserName { get; set; }
    public required string Email { get; set; }
    public required string Password { get; set; }
    public bool IsFake { get; set; } = false;
}
