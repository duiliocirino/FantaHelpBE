// 1. Create a WebApplication builder.
var builder = WebApplication.CreateBuilder(args);

// 2. Add services to the dependency injection container.
builder.Services.AddOpenApi();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// *** This is where you register your own services ***
// For example:
// builder.Services.AddScoped<IPlayerService, PlayerService>();
// builder.Services.AddScoped<ITeamSuggestionService, TeamSuggestionService>();

// 3. Build the application.
var app = builder.Build();

// 4. Configure the HTTP request pipeline (middleware).
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

// 5. Run the application.
app.Run();
