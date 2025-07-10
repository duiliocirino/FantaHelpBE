public class User
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public required string Email { get; set; }
    public required string Password { get; set; }

    // --- Navigation Properties ---
    public ICollection<Team> Teams { get; set; } = new List<Team>();
}
