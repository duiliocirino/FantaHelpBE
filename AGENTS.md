# AGENTS.md — FantaHelpBE

Persistent instructions for any AI assistant working in this repo.

---

## Project Overview

FantaHelpBE is a .NET 9 ASP.NET Core Web API for Fantacalcio (Italian fantasy football). It consumes ML-predicted player data (from the FantaHelpML pipeline) and serves it to a frontend application during live auctions.

**Tech stack:** .NET 9, ASP.NET Core, Entity Framework Core, PostgreSQL (via Npgsql)

---

## Repository Structure

```
Fantahelp.API/
├── Controllers/          # REST endpoints
├── Models/
│   ├── Dtos/             # Create and Read DTOs (never expose entities)
│   ├── Mappers/          # Static mapping classes (PlayerMapper, TeamMapper, etc.)
│   └── Player.cs, Team.cs, etc.   # EF entities
├── Services/             # Business logic (IPlayerService, ITeamService, etc.)
├── Utils/                # Helpers (CsvParser, etc.)
└── Properties/launchSettings.json  # Dev profiles (HTTP port 60001)
```

---

## Running the Project

All commands from repo root (`FantaHelpBE/`).

### Prerequisites
- .NET 9 SDK
- PostgreSQL 17 running in podman pod `fantahelp-pod` (port 5432)
- Start script: `./start_pod.sh` (reads `.env` for credentials)

### Start the API
Set the connection string in `Fantahelp.API/appsettings.json` (see README for details). This file is in `.gitignore` — never commit it with real credentials. Then:
```bash
dotnet tool restore
dotnet tool run dotnet-ef database update --project Fantahelp.API
dotnet run --project Fantahelp.API
```
Default ports: HTTP `http://localhost:5102`, HTTPS `https://localhost:7202`. Swagger at HTTPS + `/swagger`.

See README.md for the full Quick Start and Full Setup guides.

---

## ML Pipeline Contract

The ML pipeline (FantaHelpML repo) produces `data/final/{season}/players.csv`. The BE imports it via `POST /api/players/import`.

**Import is destructive** — `PlayerService.ImportPlayersFromCsvAsync` wipes all existing players before inserting.

**CSV headers** (case-insensitive match via CsvHelper):
`id, role, role_m, name, squad, price, age, myrating, mate, regularness, fvm, expmf, expprice, expstd`

**Nullable fields:** Age, MyRating, Mate, Regularness, ExpMf may be empty for players without historical stats. The DTO uses nullable types with `?? 0` fallbacks in the service mapping.

**Role_M parsing:** ML outputs sub-positions as semicolon-separated string (e.g., `M;C`, `B;Ds;E`). Service splits on `;` into `List<string>`. Falls back to `[Role]` if empty.

Full contract: `docs/ml-be-contract.md`

---

## Coding Conventions

- **DTOs over entities:** Controllers and services use DTOs. Never expose EF entities directly.
- **Static mappers:** Mapping lives in `Models/Mappers/` (e.g., `PlayerMapper.ToReadDto()`).
- **Service interface pattern:** Every service has an interface (`IPlayerService`) injected into controllers.
- **ServiceResult wrapper:** Service methods return `ServiceResult<T>` with `Success`/`Data`/`Message`. Controllers unwrap `.Data` and return it directly (not wrapped in ServiceResult).
- **Minimal diffs:** Change only what the task requires. Leave unrelated code alone.
- **Read before edit:** Always read a file before modifying it in the same session.

---

## Pending Items

Track in `docs/FUTURE_STEPS.md`. Check before starting work to avoid duplicating effort.

Key open items:
- `Mate` stored as player name (string) — consider `MateId` (int?) instead
- No unit/integration tests
- No authentication middleware
- Connection string should use env vars cleanly
- Suggestion engine caching (tracked in FUTURE_STEPS.md)

---

## Annual Season Flow

Every year the cycle is:
1. FantaHelpML generates new season data
2. FantaHelpBE imports via `POST /api/players/import`
3. FantaHelpFE consumes the API during live auctions

See `docs/ml-be-contract.md` § Annual Checklist for the full procedure.
