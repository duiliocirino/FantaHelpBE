# FantaHelpBE

Backend for a **Fantacalcio** (Italian fantasy football) helper application. Helps users build optimal fantasy teams during the auction phase through a dynamic programming-based suggestion engine.

## Quick Start (Come Back to the Project)

All commands below are run from the repository root (`FantaHelpBE/`).

If you've worked on this project before and just need to get running again:

```bash
# 1. Install .NET 9 SDK (if not already installed)
#    See "Prerequisites" below for the command

# 2. Set up environment
cp .env.example .env
# Edit .env if you want custom DB credentials

# 3. Start PostgreSQL
bash start_pod.sh

# 4. Configure the connection string
#    Edit Fantahelp.API/appsettings.json and set "DefaultConnection" to match your .env:
#    "DefaultConnection": "Host=localhost;Port=5432;Database=FantacalcioDb;Username=fantahelp;Password=fantahelp_password"

# 5. Install local tools (EF Core CLI)
dotnet tool restore

# 6. Apply migrations (creates tables if the DB is new)
dotnet tool run dotnet-ef database update --project Fantahelp.API

# 7. Run the API
dotnet run --project Fantahelp.API

# The API is available at https://localhost:7202 (HTTPS) or http://localhost:5102 (HTTP)
# Swagger UI: https://localhost:7202/swagger
```

## What This Is

This is the backend API for a Fantacalcio auction helper. The core problem it solves:

During a Fantacalcio auction, users have a limited budget and must buy real Serie A players to form their fantasy team. The challenge is finding the optimal combination of players that maximizes expected performance within budget constraints, respecting role requirements and formation rules.

This backend provides:

- **CRUD operations** for Leagues, Teams, Players, and Users
- **CSV import** of player data (including ML-predicted performance metrics)
- **Team suggestion engine** -- a dynamic programming algorithm that computes optimal team compositions based on budget, available players, and scoring strategy

## Architecture

```
FantaHelpBE/
├── .config/dotnet-tools.json         # Local tools manifest (EF Core CLI)
├── start_pod.sh                      # Script to start PostgreSQL via Podman pod
├── .env.example                      # Environment variables template
└── Fantahelp.API/                    # .NET 9 Web API
    ├── Program.cs                    # Entry point + DI registration
    ├── Common/
    │   └── ServiceResult.cs          # Generic result wrapper (Success/Data/Error)
    ├── Controllers/                  # REST API endpoints
    │   ├── LeaguesController.cs
    │   ├── PlayersController.cs
    │   ├── TeamsController.cs
    │   └── UsersController.cs
    ├── Data/
    │   └── FantahelpContext.cs       # EF Core DbContext
    ├── Migrations/                   # EF Core migrations
    ├── Models/
    │   ├── League.cs                 # Domain entity
    │   ├── Player.cs                 # Domain entity
    │   ├── Team.cs                   # Domain entity
    │   ├── TeamPlayer.cs             # Join entity (Team <-> Player)
    │   ├── User.cs                   # Domain entity
    │   ├── Dtos/                     # Data Transfer Objects
    │   │   ├── Create/               # DTOs for POST/PUT requests
    │   │   ├── Read/                 # DTOs for GET responses
    │   │   ├── LineUp.cs             # Formation config (3-4-3, 4-3-3, etc.)
    │   │   ├── Score.cs              # Scoring breakdown
    │   │   ├── SuggestionRequest.cs  # Team suggestion input
    │   │   └── SuggestionResult.cs   # Team suggestion output
    │   └── Mappers/                  # Entity <-> DTO mapping
    ├── Services/                     # Business logic (interface + implementation)
    │   ├── IPlayerService / PlayerService.cs
    │   ├── ITeamService / TeamService.cs
    │   ├── ILeagueService / LeagueService.cs
    │   ├── IUserService / UserService.cs
    │   ├── ITeamSuggestionService / TeamSuggestionService.cs  # <-- Core algorithm
    │   └── Helpers/                  # Algorithm helper types
    └── Utils/
        └── CsvParser.cs              # CsvHelper-based player import
```

### Domain Model

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `User` | End user | Id, UserName, Email, Password, IsFake |
| `League` | Fantasy league | Id, Name, InitialBudget (default 800), GoalBonusPerRole |
| `Team` | User's fantasy team | Id, Name, RemainingBudget, OwnerId, LeagueId |
| `Player` | Real football player | Id, Name, Squad, Role (P/D/C/A), Price, Rating, ExpectedPerformance, ExpectedStd, ExpectedPrice |
| `TeamPlayer` | Join: Team <-> Player | TeamId, PlayerId, AuctionPrice, LeagueId (composite key) |

**Relationships:**

- `User` 1-to-many `Team`
- `Team` belongs to `League`
- `Team` many-to-many `Player` via `TeamPlayer` (with auction price)
- `TeamPlayer` also references `League` for query optimization

### Tech Stack

| Component | Technology |
|-----------|-----------|
| Framework | ASP.NET Core 9 (Web API) |
| ORM | Entity Framework Core 9 |
| Database | PostgreSQL 17 |
| CSV parsing | CsvHelper |
| Container runtime | Podman (pod) |
| API docs | Swagger/OpenAPI |

## Prerequisites (First-Time Setup)

On a fresh Linux machine, you need:

### 1. .NET 9 SDK

On x64 Ubuntu 22.04+, the recommended approach is the `dotnet` snap:

```bash
sudo snap install --classic dotnet
dotnet-installer install sdk 9.0
```

Verify: `dotnet --version` should show `9.0.x`.

For non-x64 architectures or older Ubuntu versions, see the official guide: <https://ubuntu.com/developers/docs/howto/dotnet-setup>

### 2. Podman

```bash
sudo apt install -y podman
```

Verify: `podman --version`.

### 3. Git (for cloning)

```bash
sudo apt install -y git
```

## Full Setup (From Scratch)

All commands below are run from the repository root (`FantaHelpBE/`).

```bash
# 1. Clone the repository and enter it
git clone <repo-url> FantaHelpBE
cd FantaHelpBE

# 2. Restore dependencies
dotnet restore

# 3. Set up environment
cp .env.example .env
# Edit .env if you want custom DB credentials

# 4. Start PostgreSQL
bash start_pod.sh

# Wait a few seconds for the container to initialize
sleep 5

# 5. Configure the connection string
# Edit Fantahelp.API/appsettings.json:
#   "DefaultConnection": "Host=localhost;Port=5432;Database=FantacalcioDb;Username=fantahelp;Password=fantahelp_password"

# 6. Install local tools (EF Core CLI)
dotnet tool restore

# 7. Apply EF Core migrations (creates the schema)
dotnet tool run dotnet-ef database update --project Fantahelp.API

# 8. Run the API
dotnet run --project Fantahelp.API
```

The API starts on two ports by default:
- **HTTP:** `http://localhost:5102`
- **HTTPS:** `https://localhost:7202`

Swagger UI is available at the HTTPS URL + `/swagger`.

### Managing the Database

```bash
# Start the database (first run creates the pod; subsequent runs just start it)
bash start_pod.sh

# Stop the pod (preserves data in volume)
podman pod stop fantahelp-pod

# Start the pod again
podman pod start fantahelp-pod

# Remove the pod and containers (preserves data volume)
podman pod rm -f fantahelp-pod

# Remove data volume too (WARNING: destroys all data)
podman volume rm fantahelp-postgres-data

# View logs
podman logs -f fantahelp-postgres

# Connect to the database shell
podman exec -it fantahelp-postgres psql -U fantahelp -d FantacalcioDb
```

## API Endpoints

### Players

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/players` | Get all players |
| GET | `/api/players/{id}` | Get player by ID |
| POST | `/api/players/import` | Import players from CSV (wipes existing data) |

### Teams

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/teams` | Get all teams |
| GET | `/api/teams/{id}` | Get team by ID |
| POST | `/api/teams` | Create a new team |
| DELETE | `/api/teams/{id}` | Delete a team |
| POST | `/api/teams/{id}/players` | Add a player to a team |
| DELETE | `/api/teams/{id}/players/{playerId}` | Remove a player from a team |
| POST | `/api/teams/getOptimal` | Get optimal team suggestion |

### Leagues

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/leagues` | Get all leagues |
| GET | `/api/leagues/{id}` | Get league by ID |
| POST | `/api/leagues` | Create a league |
| PUT | `/api/leagues/{id}` | Update a league |
| DELETE | `/api/leagues/{id}` | Delete a league |
| POST | `/api/leagues/{id}/teams/{teamId}` | Add a team to a league |
| DELETE | `/api/leagues/{id}/teams/{teamId}` | Remove a team from a league |
| GET | `/api/leagues/{id}/teams` | Get teams in a league |
| GET | `/api/leagues/{id}/available-players` | Get players available for auction |

### Users

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/users` | Get all users |
| GET | `/api/users/{id}` | Get user by ID |
| POST | `/api/users` | Create a user |
| PUT | `/api/users/{id}` | Update a user |
| DELETE | `/api/users/{id}` | Delete a user |

## The Suggestion Engine

The `TeamSuggestionService` implements a 3-stage dynamic programming algorithm:

1. **Precompute per-role values** -- for each role (Goalkeeper, Defender, Midfielder, Forward), compute the value of each possible player selection
2. **Combine across roles** -- find optimal combinations that respect budget and lineup constraints
3. **Backtrack for top-N teams** -- reconstruct the best team suggestions

### Constraints

- **Role requirements:** GK=3, DEF=8, MID=8, FWD=6 players per team
- **Budget caps per role:** GK=10%, DEF=30%, MID=60%, FWD=60% of initial budget
- **One player per team per league** enforced at the `TeamPlayer` level

### Scoring

| Factor | Weight | Description |
|--------|--------|-------------|
| Starter performance | 60% | Expected performance of starting XI |
| Bench | 30% | Expected performance of bench players |
| Strategy bonuses | 10% | Defense bonus, mate bonus, same-team bonus, regularness, credit distribution |

Player data includes ML-predicted fields: `ExpectedPerformance`, `ExpectedStd`, `ExpectedPrice`. An external ML pipeline generates the CSV that feeds this system.

## Current Limitations & Planned Work

- **No authentication/authorization** -- the User model exists with a Password field, but no auth middleware is configured
- **No tests** -- no unit or integration test project exists
- **CSV import is destructive** -- `POST /api/players/import` wipes all existing players before importing
- **No caching** -- the suggestion engine runs on every request; a hybrid caching approach (state-key based) is planned
- **No background jobs** -- heavy computations block the request thread
- **Frontend is separate** -- this repo is backend-only

## Useful Commands

All commands are run from the repository root (`FantaHelpBE/`).

```bash
# Run the API
dotnet run --project Fantahelp.API

# Run with a specific environment
dotnet run --project Fantahelp.API --environment Development

# Install/update local tools (EF Core CLI)
dotnet tool restore

# Create a new migration
dotnet tool run dotnet-ef migrations add <MigrationName> --project Fantahelp.API

# Apply pending migrations
dotnet tool run dotnet-ef database update --project Fantahelp.API

# Remove the last migration
dotnet tool run dotnet-ef migrations remove --project Fantahelp.API

# Build
dotnet build
```

## Notes

- The project was bootstrapped with guidance from an LLM session documented in `docs/chat.md`
- Connection string is configured in `Fantahelp.API/appsettings.json` -- the `.env` file is loaded by `DotNetEnv` but the connection string itself is set in appsettings (not via env var)
