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
│   └── Helpers/          # ScoringEngine, ScoringPlayer record
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
Default ports: HTTP `http://localhost:60001`, HTTPS `https://localhost:60000`. Swagger at HTTPS + `/swagger`.

> **Known environment issue (as of 2026-08-23):** the default `dotnet` on PATH (`/snap/bin/dotnet`, the `dotnet` snap) has a broken targeting pack (`MSB4018 ... FrameworkList.xml` not found). Use the intact `dotnet-sdk-90` snap instead:
> `/snap/dotnet-sdk-90/current/usr/lib/dotnet/dotnet build|run|...`

See README.md for the full Quick Start and Full Setup guides.

---

## ML Pipeline Contract (26-27+)

The ML pipeline (FantaHelpML repo) produces **one CSV per league format** in `data/final/{season}/`:
`players_800_8.csv`, `players_1000_8.csv`, `players_1000_10.csv`. The BE imports them via `POST /api/players/import` (form field `files`, one entry per file). The format is derived from the file name.

**Import is destructive** — `PlayerService.ImportPlayersFromCsvAsync` wipes all existing players **and** `PlayerPrice` rows before inserting. It validates that all files cover the same player ids with identical base data (only `expprice`/`expstd` may differ) and fails fast otherwise.

**CSV headers** (case-insensitive match via CsvHelper), 15 columns:
`id, role, role_m, name, squad, price, age, myrating, mate, regularness, integrity, fvm, expmf, expprice, expstd`

**Nullable fields:** Age, MyRating, Mate, Regularness, ExpMf, Integrity may be empty. The DTO uses nullable types with `?? 0` fallbacks in the service mapping — **except `Integrity`, where null stays null** (1-5 consensus, null = unknown, never a default).

**Per-format prices:** `expprice`/`expstd` are format-specific and land in the `PlayerPrice` table (one row per player × format). `Player.ExpectedPrice`/`ExpectedStd` are a deprecated bridge, populated at import from the reference format **800_8**. The suggestion engine resolves the league format from `(League.InitialBudget, lineup total starters)` — exact match, else closest by credits then starters.

**Role_M parsing:** ML outputs sub-positions as semicolon-separated string (e.g., `M;C`, `B;Ds;E`). Service splits on `;` into `List<string>`. Falls back to `[Role]` if empty.

Full contract: `docs/ml-be-contract.md`

---

## Coding Conventions

- **DTOs over entities:** Controllers and services use DTOs. Never expose EF entities directly.
- **Static mappers:** Mapping lives in `Models/Mappers/` (e.g., `PlayerMapper.ToReadDto()`).
- **Service interface pattern:** Every service has an interface (`IPlayerService`) injected into controllers.
- **ServiceResult wrapper:** Service methods return `ServiceResult<T>` with `Success`/`Data`/`Message`. Controllers unwrap `.Data` and return it directly (not wrapped in ServiceResult).
- **ScoringPlayer record:** The suggestion engine uses `ScoringPlayer` (not `Player` entity) to separate `MarketValue` from `AcquisitionCost`. Never mutate EF entities in-flight.
- **Minimal diffs:** Change only what the task requires. Leave unrelated code alone.
- **Read before edit:** Always read a file before modifying it in the same session.

---

## FE-BE Coordination

Frontend team tracks requested changes in:
`/home/duilio999/StudioProjects/FantaHelpFE/docs/backend-changes-needed.md`

Check this file before implementing new BE features to align with FE expectations.

---

## Pending Items

Track in `docs/FUTURE_STEPS.md`. Check before starting work to avoid duplicating effort.

Key open items:
- `Mate` stored as player name (string) — consider `MateId` (int?) instead
- No unit/integration tests
- No authentication middleware
- Connection string should use env vars cleanly
- Suggestion engine caching Phase 2: invalidation on player import (tracked in FUTURE_STEPS.md)

---

## Annual Season Flow

Every year the cycle is:
1. FantaHelpML generates new season data
2. FantaHelpBE imports via `POST /api/players/import`
3. FantaHelpFE consumes the API during live auctions

See `docs/ml-be-contract.md` § Annual Checklist for the full procedure.
