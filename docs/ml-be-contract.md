# ML-to-BE Data Contract

This document defines the contract between FantaHelpML (data producer) and FantaHelpBE (data consumer). The BE state is the source of truth; the ML pipeline must adapt its output to match.

---

## 0. 26-27 Contract Update (current)

From the 26-27 season the contract changed as follows (single `players.csv` -> per-format files):

| Change | Detail |
|---|---|
| **One CSV per league format** | `players_800_8.csv`, `players_1000_8.csv`, `players_1000_10.csv` (same 15 columns each). `expprice`/`expstd` are format-specific; all other columns must be identical across files. |
| **New nullable column `integrity`** | 15th column, after `regularness`. Injury-proneness consensus, 1-5 (higher = more robust). Null when no consensus -- the BE stores null, never a default. |
| **`mate` / `regularness` populated** | `regularness` is now the **expected starting %, 0-100 in multiples of 5** (titolarità; was int 1-5 through 25-26), filled for all players with real within-bucket spread; `mate` (ballottaggio partner) sparse by design. `mate` is **not always symmetric**: the third player in a 3-way rotation points one-way at the pair they rotate with (26-27: 170 strict symmetric pairs + 6 one-way links). Treat as purely informational -- no logic may depend on symmetry (confirmed by ML, 2026-08-23). |
| **Per-format prices in the BE** | New `PlayerPrice` table: one row per player x format (see §1.1). `Player.ExpectedPrice`/`ExpectedStd` are kept as a deprecated bridge populated from the reference format **800_8** at import. |

**Import endpoint (26-27+):** `POST /api/players/import` with form field `files` (multiple):

```bash
curl -X POST http://localhost:60001/api/players/import \
  -F "files=@players_800_8.csv" \
  -F "files=@players_1000_8.csv" \
  -F "files=@players_1000_10.csv"
```

- The format is derived from the file name `players_{credits}_{starters}.csv`; any non-empty subset of formats is accepted.
- A legacy bare `players.csv` is rejected (400).
- The import validates that all files cover the same player ids with identical base data (only `expprice`/`expstd` may differ) and fails fast (500) otherwise.
- Destructive: wipes all players and price rows, then rebuilds both in one transaction.

Sections below describe the base contract; where the 26-27 update differs, this section wins.

---

## 1. BE Player Entity (Source of Truth)

**File:** `Fantahelp.API/Models/Player.cs`

| # | Field | Type | Nullable | Source | Description |
|---|---|---|---|---|---|
| 1 | `Id` | `int` | No | CSV (SkyBet ID) | Unique player identifier from SkySport |
| 2 | `Name` | `string` (max 100) | No | CSV | Player name |
| 3 | `Squad` | `string` (max 50) | No | CSV | Team (e.g., "Atalanta") |
| 4 | `Role` | `string` | No | CSV | Primary role: `P`, `D`, `C`, `A` |
| 5 | `Role_M` | `List<string>` | No | CSV | Sub-positions as semicolon-separated CSV (e.g., `Dd;Ds;Dc`). Stored as PostgreSQL `text[]`. Parsed via `Split(';')` during import. Fallback to `[Role]` if absent. |
| 6 | `Price` | `int` | No | CSV | Official SkySport quotation |
| 7 | `Age` | `int` (0-100) | No | CSV | Player age. Imported from CSV. Defaults to 0 if absent. |
| 8 | `Rating` | `double` (0-5) | No | CSV as `MyRating` | Custom rating from preprocessing |
| 9 | `Mate` | `string` | **Yes** | CSV | Mate player name (empty string = no mate). Can be a one-way link (third man in a 3-way ballottaggio); informational only, symmetry not guaranteed. |
| 10 | `Regularness` | `int` (0-100) | No | CSV | Expected starting % (titolarità), multiples of 5. Was int 1-5 through 25-26. Feeds the ScoringEngine strategy bonus (formula calibrated to the % scale). |
| 11 | `FVM` | `int` | No | CSV | Market value rating |
| 12 | `ExpectedPerformance` | `double` | No | CSV as `ExpMf` | **ML-predicted mean fantasy score**. Core input to suggestion engine. |
| 13 | `ExpectedStd` | `double` | No | CSV as `ExpStd` | **ML-predicted std deviation of the auction price**. Per-player price uncertainty, aggregated as RSS into `TotalExpectedPriceStd` in suggestion results. |
| 14 | `ExpectedPrice` | `int` | No | CSV as `ExpPrice` | **ML-predicted auction price**. Used as budget constraint in suggestion engine. |
| 15 | `Integrity` | `int?` | **Yes** | CSV | Injury-proneness consensus, 1-5 (higher = more robust). Null when ML has no consensus; never defaulted. (26-27+) |

> **26-27+:** fields 13-14 (`ExpectedStd`, `ExpectedPrice`) are a **deprecated bridge**. They are populated at import from the reference format **800_8** for FE backward compatibility. The per-format source of truth is the `PlayerPrice` table (§1.1). The suggestion engine prices players from `PlayerPrice` using the league's format; the bridge columns are only a fallback when no `PlayerPrice` rows exist (legacy 25-26 data).

### ML-derived fields (12-14) -- consumption map

| Field | Used in | How |
|---|---|---|
| `ExpectedPerformance` | `TeamSuggestionService` (scoring) | Starter ordering, defense bonus calc, sub ratio, goal bonus projection. **Critical.** |
| `ExpectedPrice` | `TeamSuggestionService` (budget DP) | Budget constraint in knapsack DP, credit spread variance calc, player ordering within roles. **Critical.** |
| `ExpectedStd` | `TeamSuggestionService` (backtrack) | Per-player price std. Aggregated as `sqrt(sum(std^2))` into `TotalExpectedPriceStd` per suggested team. |
| All three | `PlayerReadDto` (API response) | Exposed via `GET /api/players` and nested inside league responses. FE reads them. |

### 1.1 PlayerPrice Entity (26-27+, per-format prices)

**File:** `Fantahelp.API/Models/PlayerPrice.cs`

| Field | Type | Nullable | Source | Description |
|---|---|---|---|---|
| `PlayerId` | `int` | No | FK | Player reference (composite key part 1). Cascade delete from `Players`. |
| `Credits` | `int` | No | File name | League total credits, e.g. 800, 1000 (composite key part 2). |
| `Starters` | `int` | No | File name | Number of starters, e.g. 8, 10 (composite key part 3). |
| `Price` | `int` | No | CSV as `expprice` | Expected auction price in this format's market. |
| `Std` | `double` | No | CSV as `expstd` | Expected auction price std in this format's market. |

**Consumption:** the suggestion engine resolves a request's format from `(League.InitialBudget, lineup total starters)` -- exact match first, then closest by credits distance, then starters distance -- and uses that format's `PlayerPrice` rows for `MarketValue`/`ExpectedStd` scoring, the `TotalExpectedPriceStd` aggregation, and the per-player prices in `SuggestionResult.SuggestedPlayers`.

---

## 2. BE CSV Import Pipeline

### Flow

```
POST /api/players/import (multipart, form field `files`, one players_{credits}_{starters}.csv per format)
  → PlayersController validates file names, extracts (credits, starters) per file
    → CsvParser.ParsePlayers(stream) per file
      → CsvHelper maps CSV columns → PlayerCreateDto (case-insensitive header match)
        → PlayerService.ImportPlayersFromCsvAsync(files)
          → VALIDATE: same player ids + identical base data across format files (fail fast)
          → WIPE all existing player prices and players
          → Map PlayerCreateDto → Player entity (base data from first file)
          → Map (expprice, expstd) per file → PlayerPrice rows (one per player x format)
          → INSERT players + price rows, COMMIT transaction
```

### PlayerCreateDto (CSV column contract)

**File:** `Fantahelp.API/Models/Dtos/Create/PlayerCreateDto.cs`

| CSV Column Name (case-insensitive) | DTO Property | Entity Field | Notes |
|---|---|---|---|
| `id` | `Id` (int) | `Id` | |
| `role` | `Role` (string) | `Role` | |
| `name` | `Name` (string) | `Name` | |
| `squad` | `Squad` (string) | `Squad` | |
| `price` | `Price` (int) | `Price` | |
| `myrating` | `MyRating` (float) | `Rating` | Renamed on import |
| `mate` | `Mate` (string) | `Mate` | |
| `regularness` | `Regularness` (int) | `Regularness` | |
| `integrity` | `Integrity` (float) | `Integrity` | **26-27+, nullable.** CSV carries float notation (e.g. `5.0`); null stays null (never defaulted). |
| `fvm` | `FVM` (int) | `FVM` | |
| `expmf` | `ExpMf` (float) | `ExpectedPerformance` | **Column name must be `expmf`** |
| `expprice` | `ExpPrice` (int) | `ExpectedPrice` | **Column name must be `expprice`** |
| `expstd` | `ExpStd` (int) | `ExpectedStd` | **Column name must be `expstd`** |

### Import mapping notes

| Note | Location | Detail |
|---|---|---|
| `Role_M` parsing | `PlayerService.cs:48` | Parsed via `Split(';')` with `RemoveEmptyEntries \| TrimEntries`. Fallback to `[Role]` if absent or empty. |
| `Age` import | `PlayerService.cs:52` | Imported as `(int)(p.Age ?? 0)`. Defaults to 0 if absent. |
| `Mate` stored as name | CSV + entity | ML outputs mate's **player name** (e.g., "Carnesecchi" → mate "Musso"). Names are fragile: ambiguous ("Ederson D.s."), change across seasons. **Recommended: ML outputs mate's SkyBet ID instead, BE stores `MateId int?`.** |
| Destructive import | `PlayerService.cs` | `ExecuteDeleteAsync()` wipes all player prices and players before insert. BE is a single-season knowledge base -- historical editions are archived as ML output CSVs, not managed by the BE. |
| File-name format | `PlayersController.cs` | 26-27+: format comes from the file name `players_{credits}_{starters}.csv` (e.g. `players_800_8.csv`). A legacy bare `players.csv` is rejected with a 400. |
| Cross-file consistency | `PlayerService.ValidateFilesConsistency` | All format files must cover the same player ids with identical base data (only `expprice`/`expstd` may differ). Mismatch → 500, nothing written. |
| Reference-format bridge | `PlayerService.cs` | `Player.ExpectedPrice`/`ExpectedStd` are populated at import from the **800_8** file (falls back to the first file if 800_8 is not uploaded). Deprecated: FE should migrate to per-format data. |

---

## 3. Current ML Pipeline (As-Is)

### 3.1 Pipeline Overview

The ML pipeline is a **sequential chain** of 3 active stages (0 → 1 → 2A → 2B). An archived multi-feature approach (`04_expected_price.ipynb`) exists but is not part of the active flow.

```
Stage 0: 00_data_preprocess_merge.ipynb
         → data/intermediate/{SEASON}/data_preprocess_merge.xlsx
            (raw player data + historical stats merged)
   ↓
Stage 1: 01_ratings.ipynb
         → data/intermediate/{SEASON}/output_rp.csv
            (ExpectedMf + MyRating computed)
   ↓
Stage 2A: 02_fvm_to_distribution.ipynb
          → models/fvm_distribution/{SEASON}/model_[mean,std]_[P,D,C,A].joblib
             (LinearRegression models trained from real auction data)
   ↓
Stage 2B: 03_regressors.ipynb
          → data/final/{SEASON}/players.csv  [ACTIVE FINAL OUTPUT]
             (predictions applied + BE-ready CSV with all 14 columns)
```

Archived (not active):
- `04_expected_price.ipynb` — old multi-feature Lasso inference path. Archived. Does not produce BE-ready column names.
- `explorations/expected_value_players.ipynb` — empirical inter-league price comparison for validation.

### 3.2 Stage 0 — `data_preprocess_merge.ipynb` (Data Ingestion)

```
Input:  Quotazioni_Fantacalcio_Stagione_2024_25.xlsx  (official SkySport quotations)
        stats/*.xlsx  (5-year historical Serie A match stats, 19_20 through 23_24)
        copyCsvReal.xlsx  (historical trial data)

Processing:
  → Maps FantaHelp column names to official Fantacalcio export names via fh_to_fc dictionary:
    Id→Id, Role→R, Role_M→RM, Name→Nome, Squad→Squadra, Price→Qt.A, FVM→FVM,
    Pg→Pv (matches played), Mv→Mv (pure rating), Mf→Fm (fantasy rating with bonus/malus)
  → Merges match counts (Pg), ratings (Mv), fantasy ratings (Mf) for each of 5 seasons
  → Increments player age by +1 from previous season's record

Output: 24-25/data_preprocess_merge.xlsx

> **Manual intervention required:** After running this notebook, `Age`, `Regularness`, and `Mate` fields must be manually updated before proceeding to Stage 1. These require subjective human assessment (Mate pairings, injury risk ratings). The notebook's final cell explicitly states: "Now remember to update the missing ages and regularness before passing to the ratings generation."
```

### 3.3 Stage 1 — `ratings.ipynb` (Performance Curves & Ratings)

```
Input:  24-25/data_preprocess_merge.xlsx  (from Stage 0)
        24-25/squads.csv                  (team → numeric value mapping)

Processing:
  → Age-Performance curves: groups players by sub-position (Role_M: Por, Dc, B, Dd, Ds, E, M, C, W, T, Pc, A)
    Fits 2nd-degree polynomial (Age ≤ 34) → avg Mf performance.
    Computes year-over-year delta: ΔE[Mf](Age) = E[Mf](Age) - E[Mf](Age-1)
  → ExpectedMf: weighted blend (50/50) of
    - FVM-based performance (polyfit regression: FVM → avg Mf score, per sub-position Role_M)
    - Age-based performance (gaussian curve fit: Age → performance, per sub-position Role_M)
    For experienced players (Pg23_24 >= 16): uses actual Mf23_24 + age-delta adjustment
    For unproven players: uses E[Mf](Age) directly
  → MyRating: Z-score normalization of performance and log-scaled FVM (log_1.2(FVM))
    Z-scores mapped to 1.0-5.0 scale. Blend: 0.4 × FVM_Rating + 0.6 × Stats_Rating

Output: 24-25/output_rp.csv
  Columns: Id, Role, Role_M, Name, Squad, Price, Age, MyRating, Mate, Regularness,
           FVM, ExpectedMf, [historical stats: Pg23_24..Mf19_20]
```

### 3.4 Stage 2A — `02_fvm_to_distribution.ipynb` (Model Training from Real Auction Data)

```
Input:  data/intermediate/{SEASON}/output_rp.csv  (player roster from Stage 1)
        data/raw/{SEASON}/Rose_*.xlsx  (real fantasy league auction data)

Processing:
  → Reads all Rose files: team rosters with actual auction prices paid by users
  → Groups all auction prices by player name across all teams
  → Computes empirical mean, std, count per player
  → Filters to players with count >= 8 (sufficient data points)
  → Merges with player FVM data
  → Filters by minimum FVM per role (e.g., D: FVM >= 10)
  → Trains 2 LinearRegression models per role (P, D, C, A):
    - model_mean: FVM → empirical auction price mean
    - model_std:  FVM → empirical auction price std
  → Saves models as .joblib files

Output: models/fvm_distribution/{SEASON}/model_[mean,std]_[P,D,C,A].joblib  (8 LinearRegression models)
```

**This is the bridge between real market data and model predictions.** The LinearRegression models are trained on empirical auction prices from real leagues, using FVM as the sole input feature. The models learn the FVM → price relationship from actual market behavior. These are the exact models loaded by `03_regressors.ipynb` (Stage 2B).

### 3.5 Stage 2B — `03_regressors.ipynb` (Inference + Final CSV)

```
Input:  data/intermediate/{SEASON}/output_rp.csv                        (from Stage 1)
        models/fvm_distribution/{SEASON}/model_[mean,std]_[P,D,C,A].joblib  (from Stage 2A)

Production cells (cells 0-11):
  → Loads 8 pre-trained joblib models (one mean + one std per role P/D/C/A)
  → For each player, predicts mean and std from FVM alone:
    - model_mean_[role].joblib → predicts expected auction price
    - model_std_[role].joblib  → predicts auction price std
  → Results clamped: mean >= 1
  → Selects columns via columns_to_keep: Id, Role, Role_M, Name, Squad, Price,
    Age, MyRating, Mate, Regularness, FVM, ExpectedMf, mean, std
  → Renames to BE contract names: mean→expprice, std→expstd, ExpectedMf→expmf,
    Role_M→role_m, Age→age, plus all others to lowercase
  → Final output: data/final/{SEASON}/players.csv (all 14 BE columns present)

Exploratory cells (cells 12-45):
  → Experiments with GPR, Ridge, Lasso, and SVM models on multi-feature inputs
  → Feature matrix: [Squad, Price, MyRating, Regularness, FVM]
  → Trains and saves models as .pkl files (e.g., lasso_regressor_model_A.pkl)
  → GPR deemed unsuitable (overfitting on sparse price data)
  → Ridge and Lasso show desired behavior, outperform plain LinearRegression on MAE
```

**Why FVM-only for production?** Earlier experiments with MultiOutput Lasso (5 features: Squad, Price, MyRating, Regularness, FVM) showed that FVM dominated the regression coefficients at ~99%, while other features had negligible or counterintuitive impact. The simpler FVM-only LinearRegression models (trained from real auction data in Stage 2A) were adopted as the active approach.

### 3.6 Archived — `04_expected_price.ipynb` (Old Multi-Feature Path)

**Status: Archived. Not part of the active pipeline.**

```
Input:  data/intermediate/24-25/output_rp.csv
        data/raw/24-25/squads.csv
        lasso_regressor_model_[P,D,C,A].pkl  (4 pre-trained MultiOutput Lasso models)

Processing:
  → Reads output_rp.csv
  → Converts Squad names to numeric values via squads.csv
  → Loads 4 MultiOutput Lasso models (one per Role P/D/C/A)
  → Features: [Squad, Price, MyRating, Regularness, FVM] (5 features)
  → Each model outputs 2 values simultaneously: [ExpectedPrice, ExpectedPriceStd]
  → Results clamped: mean >= 1, std >= 1
  → Output: data/final/24-25/players_lasso.csv
```

**Why archived:** Does not rename columns to BE contract names. FVM-dominated coefficients made multi-feature approach redundant. Missing model files (P and D) prevent execution without retraining.

### 3.7 Exploration — `explorations/expected_value_players.ipynb` (Post-Hoc Validation)

```
Input:  output_rp.csv  (player roster)
        Rose_*.xlsx    (real fantasy league exports with actual auction prices)

Processing:
  → Reads multiple real league datasets (Nicosia league + Friends league)
  → Groups all auction prices by player name across all teams
  → Computes empirical mean, std, count per player
  → Players not found in any team get default mean=1, std=1, count=0
  → NaN std values replaced with 1
  → Computes PriceDiff: how much each player's actual auction price deviates from
    market mean (accounting for std). Identifies overpriced and underpriced players.
  → Outputs: player_prices.xlsx, friends_prices.xlsx
```

**Purpose:** Validates model predictions against real market behavior and identifies inter-league price discrepancies.

### 3.8 What the BE Actually Imports

The BE's `PlayerCreateDto` expects these CSV columns (case-insensitive):

```
id, role, name, squad, price, age, myrating, mate, regularness, integrity, fvm, expmf, expprice, expstd, role_m
```

(26-27+: 15 columns including `integrity`, one file per format. 25-26 and earlier: 14 columns, single `players.csv`.)

The **active pipeline** (`03_regressors.ipynb` final output) produces all 14 columns via its rename step:
- `Id` → `id`, `Role` → `role`, `Name` → `name`, `Squad` → `squad`, `Price` → `price`
- `Age` → `age`, `MyRating` → `myrating`, `Mate` → `mate`, `Regularness` → `regularness`, `FVM` → `fvm`
- `ExpectedMf` → `expmf`, `mean` → `expprice`, `std` → `expstd`, `Role_M` → `role_m`

**The column names match the BE contract.** Output is `data/final/{SEASON}/players.csv`.

### 3.9 What `expPrice` and `expStd` Actually Represent

`expPrice` and `expStd` are grounded in **real auction data**, not pure model invention. The pipeline trains LinearRegression models on empirical auction prices from real fantasy leagues (Stage 2A), then applies those models to all players via FVM (Stage 2B).

- `expPrice` = predicted auction price (mean), derived from FVM via role-specific LinearRegression trained on real auction data
- `expStd` = predicted auction price std deviation, derived from FVM via role-specific LinearRegression trained on real auction data
- `expMf` = expected mean fantasy score, computed in `ratings.ipynb` from FVM + age performance curves

This maps cleanly to the BE's `ExpectedPrice` (int) and `ExpectedStd` (double) fields. **There is no ambiguity** — the active pipeline produces a single pair of (price, price_std) values per player.

### 3.9 Output File Locations

| File | Location | Source | Status |
|---|---|---|---|
| `Quotazioni_*.xlsx` | `data/raw/{SEASON}/` | Official SkySport | Stage 0 input |
| `player_dob.csv` | `data/utils/` | Player birth dates | Stage 0 input |
| `Quotazioni_*.xlsx` | `data/raw/historical/` | Historical Quotazioni (18-19 to 24-25) | Stage 0 input (FVM merge) |
| `stats/*.xlsx` | `data/historical/` | Historical Serie A stats | Stage 0 input |
| `data_preprocess_merge.xlsx` | `data/intermediate/{SEASON}/` | Stage 0 | Stage 0 output |
| `output_rp.csv` | `data/intermediate/{SEASON}/` | Stage 1 | Stage 1 output (active) |
| `Rose_*.xlsx` | `data/raw/{SEASON}/` | Fantasy league exports | Stage 2A input |
| `model_[mean,std]_[P,D,C,A].joblib` | `models/fvm_distribution/{SEASON}/` | Stage 2A | Stage 2A output |
| `players.csv` | `data/final/{SEASON}/` | Stage 2B | **Active final output** |
| `players_lasso.csv` | `data/final/24-25/` | 04_expected_price.ipynb | Archived |
| `player_prices.xlsx`, `friends_prices.xlsx` | `explorations/` | explorations notebook | Validation |

---

## 4. Contract Compliance

### What the BE needs vs what the ML produces

| BE Requires | ML Produces (03_regressors.ipynb output) | Status |
|---|---|---|
| `id` | `id` | OK |
| `role` | `role` | OK |
| `name` | `name` | OK |
| `squad` | `squad` | OK |
| `price` | `price` | OK |
| `age` | `age` | OK |
| `myrating` | `myrating` | OK |
| `mate` | `mate` | OK (stored as player name, not ID) |
| `regularness` | `regularness` | OK |
| `fvm` | `fvm` | OK |
| `expmf` | `expmf` | OK |
| `expprice` | `expprice` | OK |
| `expstd` | `expstd` | OK |
| `role_m` | `role_m` | OK |
| `integrity` (26-27+) | `integrity` | OK (nullable) |

**All columns are produced.** The contract holds.

### Known issues

| Issue | Location | Detail |
|---|---|---|
| `Mate` stored as name | ML + BE | ML outputs mate's player name. Names are fragile (ambiguous, change across seasons). Recommended: ML outputs mate's SkyBet ID, BE stores `MateId int?`. |

---

## 5. Current Pipeline Status

### 5.1 Output CSV format (current)

The active pipeline (`03_regressors.ipynb`) produces a final CSV with all 14 BE columns:

```
id, role, role_m, name, squad, price, age, myrating, mate, regularness, fvm, expmf, expprice, expstd
```

All columns are present and correctly renamed via the notebook's rename step. The contract holds.

### 5.2 Future improvements

| Improvement | Priority | Detail |
|---|---|---|
| Mate → MateId migration | FUTURE | ML should output mate's SkyBet ID instead of name. Requires BE schema change (`MateId int?`). |
| Stage 2A refactoring | PLANNED | Replace LinearRegression with Ridge([sqrt(FVM), FVM]), add quantile-based std, multi-season Rose training with recency weights. See `docs/improvement-roadmap.md` Phase 3. |
| Stage 2B residual correction | PLANNED | Add ExpectedMf-based residual correction in inference step. See `docs/improvement-roadmap.md` Phase 3. |
| Multi-league format support | FUTURE | Tag Rose data by league format (credits/players) to support multiple auction settings. |

### 5.3 Pipeline stages summary

```
Stage 0: 00_data_preprocess_merge.ipynb  → raw data + historical stats merged → data_preprocess_merge.xlsx
Stage 1: 01_ratings.ipynb                → ExpectedMf + MyRating → output_rp.csv
Stage 2A: 02_fvm_to_distribution.ipynb   → train FVM→price models from real auction data → .joblib models
Stage 2B: 03_regressors.ipynb            → apply models + BE-ready CSV → data/final/{SEASON}/players.csv  [ACTIVE]
```

Archived (not active):
- `04_expected_price.ipynb` — old multi-feature Lasso inference path. Archived.

Exploration (validation only):
- `explorations/expected_value_players.ipynb` — empirical inter-league price comparison for validation.

**See `FantaHelpML/docs/improvement-roadmap.md` for the full ML repo improvement plan.**

---

## 6. BE-Side Improvements (Optional)

These are BE changes that would improve the contract long-term, but are not blockers:

| Change | Priority | Detail |
|---|---|---|
| Mate → MateId | FUTURE | Store `MateId int?` instead of `Mate string`. Requires ML to output SkyBet ID. |
| Soft import mode | MEDIUM | Add a non-destructive import option (upsert by `Id` instead of wipe+insert) |
| Validate import data | LOW | Add range checks (e.g., `ExpMf` > 0, `ExpPrice` > 0) before DB write |

> **Note:** `Age` and `Role_M` are already properly imported. `PlayerService.cs` parses `Role_M` via `Split(';')` and imports `Age` from CSV.

---

## 7. Annual Flow Checklist

Every season, the flow is:

```
FantaHelpML (produce data)  →  FantaHelpBE (import + serve)  →  FantaHelpFE (consume via API)
```

### Step 1: ML produces new season data

- [ ] Set `SEASON` variable in each notebook to target season
- [ ] Run `00_data_preprocess_merge.ipynb` (Stage 0) → produces `data/intermediate/{SEASON}/data_preprocess_merge.xlsx`
- [ ] Manually update `Mate`, `Regularness` fields after Stage 0
- [ ] Run `01_ratings.ipynb` (Stage 1) → produces `data/intermediate/{SEASON}/output_rp.csv` with ExpectedMf and MyRating
- [ ] Run `02_fvm_to_distribution.ipynb` (Stage 2A) → trains FVM→price models from real auction data → `.joblib` files
- [ ] Run `03_regressors.ipynb` (Stage 2B) → applies models → produces `data/final/{SEASON}/players.csv`
- [ ] Verify output CSVs have all 15 columns: `id, role, role_m, name, squad, price, age, myrating, mate, regularness, integrity, fvm, expmf, expprice, expstd` (one file per format, named `players_{credits}_{starters}.csv`)
- [ ] Spot-check a few players for reasonable values (expPrice > 0, expStd > 0, expMf in plausible range)
- [ ] (Optional) Compare model-predicted expPrice/expStd against empirical values from exploration notebook

### Step 2: BE imports data

- [ ] BE is running (DB up, migrations applied)
- [ ] Upload all per-format CSVs via `POST /api/players/import` (form field `files`, or `curl` / Swagger)
- [ ] Verify import succeeded: `GET /api/players` returns expected count
- [ ] Spot-check ML fields: `ExpectedPerformance` is non-zero; per-format `PlayerPrice` rows exist (e.g. via a suggestion for a known league format)
- [ ] Spot-check `integrity`: some players have a 1-5 value, the rest null

### Step 3: FE-BE integration trial

- [ ] FE can fetch players: `GET /api/players`
- [ ] FE can fetch leagues: `GET /api/leagues`
- [ ] Suggestion endpoint works: `POST /api/teams/suggest`
- [ ] All DTOs deserialize without Moshi crashes

---

## Appendix: Current BE Database Schema

| Table | Key Columns | FK Relations |
|---|---|---|
| `Users` | `Id`, `Username`, `PasswordHash` | |
| `Leagues` | `Id`, `Name`, `InitialBudget`, `GoalBonusPerRole` | |
| `Teams` | `Id`, `Name`, `RemainingBudget`, `OwnerId`, `LeagueId` | `OwnerId`→Users, `LeagueId`→Leagues |
| `TeamPlayers` | `TeamId`, `PlayerId`, `AuctionPrice`, `LeagueId` | `TeamId`→Teams, `PlayerId`→Players, `LeagueId`→Leagues |
| `Players` | `Id`, `Name`, `Squad`, `Role`, `Role_M`, `Price`, `Age`, `Rating`, `Mate`, `Regularness`, `Integrity` (nullable), `FVM`, `ExpectedPerformance`, `ExpectedStd` (bridge), `ExpectedPrice` (bridge) | |
| `PlayerPrices` | `PlayerId`, `Credits`, `Starters`, `Price`, `Std` | PK `(PlayerId, Credits, Starters)`, `PlayerId`→Players (cascade) |
