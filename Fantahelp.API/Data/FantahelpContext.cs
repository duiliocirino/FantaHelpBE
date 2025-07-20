using Microsoft.EntityFrameworkCore;

public class FantahelpContext : DbContext
{
    public FantahelpContext(DbContextOptions<FantahelpContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<League> Leagues { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<TeamPlayer> TeamPlayers { get; set; }
    public DbSet<Player> Players { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasMany(u => u.Teams)
            .WithOne(t => t.Owner)
            .HasForeignKey(t => t.OwnerId);

        modelBuilder.Entity<League>()
            .HasMany(l => l.Teams)
            .WithOne(t => t.League)
            .HasForeignKey(t => t.LeagueId);

        modelBuilder.Entity<TeamPlayer>()
            .HasKey(tp => new { tp.TeamId, tp.PlayerId });

        modelBuilder.Entity<TeamPlayer>()
            .HasOne(tp => tp.Team)
            .WithMany(t => t.Players)
            .HasForeignKey(tp => tp.TeamId);

        modelBuilder.Entity<TeamPlayer>()
            .HasOne(tp => tp.Player)
            .WithMany(p => p.TeamPlayers)
            .HasForeignKey(tp => tp.PlayerId);

        modelBuilder.Entity<TeamPlayer>()
            .HasOne(tp => tp.League)
            .WithMany()
            .HasForeignKey(tp => tp.LeagueId);
    }
}