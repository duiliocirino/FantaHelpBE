using DotNetEnv;
using Microsoft.EntityFrameworkCore;

Env.Load(); // This loads variables from .env into environment variables

// 1. Create a WebApplication builder.
var builder = WebApplication.CreateBuilder(args);

// 2. Load the connection string from appsettings.json or environment variables
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// 3. Register the database context with dependency injection
builder.Services.AddDbContext<FantahelpContext>(options =>
    options.UseNpgsql(connectionString) // Tell EF Core to use PostgreSQL
);

// 4. Add services to the dependency injection container.
builder.Services.AddOpenApi();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// *** This is where you register your own services ***
// For example:
// builder.Services.AddScoped<IPlayerService, PlayerService>();
// builder.Services.AddScoped<ITeamSuggestionService, TeamSuggestionService>();

// 5. Build the application.
var app = builder.Build();

// 6. Configure the HTTP request pipeline (middleware).
// This defines how a request is handled.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers(); // This tells the app to use your controller endpoints.

// 7. Run the application.
app.Run();
