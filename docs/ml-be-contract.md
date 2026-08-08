# ML-to-BE Data Contract

This document defines the contract between FantaHelpML (data producer) and FantaHelpBE (data consumer). The BE state is the source of truth; the ML pipeline must adapt its output to match.

---

## 1. BE Player Entity (Source of Truth)

**File:** `Fantahelp.API/Models/Player.cs`

| # | Field | Type | Nullable | Source | Description |
|---|---|---|---|---|---|
| 1 | `Id` | `int` | No | CSV (SkyBet ID) | Unique player identifier from SkySport |
| 2 | `Name` | `string` (max 100) | No | CSV | Player name |
| 3 | `Squad` | `string` (max 50) | No | CSV | Team (e.g., "Atalanta") |
| 4 | `Role` | `string` | No | CSV | Primary role: `P`, `D`, `C`, `A` |
| 5 | `Role_M` | `List<string>` | No | **Stubbed** | Sub-positions as semicolon-separated CSV (e.g., `Dd;Ds;Dc`). Stored as PostgreSQL `text[]`. Currently stubbed as `[Role]` in import. Not exposed in ReadDto. |
| 6 | `Price` | `int` | No | CSV | Official SkySport quotation |
| 7 | `Age` | `int` (0-100) | No | **Not imported** | Player age. Present in entity, not in CSV import. Defaults to 0. |
| 8 | `Rating` | `double` (0-5) | No | CSV as `MyRating` | Custom rating from preprocessing |
| 9 | `Mate` | `string` | **Yes** | CSV | Mate player name (empty string = no mate) |
| 10 | `Regularness` | `int` | No | CSV | Injury risk indicator |
| 11 | `FVM` | `int` | No | CSV | Market value rating |
| 12 | `ExpectedPerformance` | `double` | No | CSV as `ExpMf` | **ML-predicted mean fantasy score**. Core input to suggestion engine. |
| 13 | `ExpectedStd` | `double` | No | CSV as `ExpStd` | **ML-predicted std deviation of the auction price**. Per-player price uncertainty, aggregated as RSS into `TotalExpectedPriceStd` in suggestion results. |
| 14 | `ExpectedPrice` | `int` | No | CSV as `ExpPrice` | **ML-predicted auction price**. Used as budget constraint in suggestion engine. |

### ML-derived fields (12-14) -- consumption map

| Field | Used in | How |
|---|---|---|
| `ExpectedPerformance` | `TeamSuggestionService` (scoring) | Starter ordering, defense bonus calc, sub ratio, goal bonus projection. **Critical.** |
| `ExpectedPrice` | `TeamSuggestionService` (budget DP) | Budget constraint in knapsack DP, credit spread variance calc, player ordering within roles. **Critical.** |
| `ExpectedStd` | `TeamSuggestionService` (backtrack) | Per-player price std. Aggregated as `sqrt(sum(std^2))` into `TotalExpectedPriceStd` per suggested team. |
| All three | `PlayerReadDto` (API response) | Exposed via `GET /api/players` and nested inside league responses. FE reads them. |

---

## 2. BE CSV Import Pipeline

### Flow

```
POST /api/players/import (multipart CSV)
  → CsvParser.ParsePlayers(stream)
    → CsvHelper maps CSV columns → PlayerCreateDto (case-insensitive header match)
      → PlayerService.ImportPlayersFromCsvAsync(dtos)
        → WIPE all existing players
        → Map PlayerCreateDto → Player entity
        → INSERT all new players
        → COMMIT transaction
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
| `fvm` | `FVM` (int) | `FVM` | |
| `expmf` | `ExpMf` (float) | `ExpectedPerformance` | **Column name must be `expmf`** |
| `expprice` | `ExpPrice` (int) | `ExpectedPrice` | **Column name must be `expprice`** |
| `expstd` | `ExpStd` (int) | `ExpectedStd` | **Column name must be `expstd`** |

### Import mapping quirks

| Issue | Location | Detail |
|---|---|---|
| `Role_M` stubbed | `PlayerService.cs:46` | Set to `new List<string> { p.Role }`. ML provides semicolon-separated sub-positions (e.g., `Dd;Ds;Dc`, `C;T`). Entity stores `List<string>` → PostgreSQL `text[]`. Import needs `Split(';')`. Not exposed in ReadDto. **See FUTURE_STEPS.md.** |
| `Age` not imported | `PlayerCreateDto` | Entity has `Age`, but DTO doesn't. Imports will set `Age = 0`. ML provides it. |
| `Mate` stored as name | CSV + entity | ML outputs mate's **player name** (e.g., "Carnesecchi" → mate "Musso"). Names are fragile: ambiguous ("Ederson D.s."), change across seasons. **Recommended: ML outputs mate's SkyBet ID instead, BE stores `MateId int?`.** |
| Destructive import | `PlayerService.cs:38` | `ExecuteDeleteAsync()` wipes all players before insert. BE is a single-season knowledge base -- historical editions are archived as ML output CSVs, not managed by the BE. |

---

## 3. Current ML Pipeline (As-Is)

### 3.1 Pipeline Overview

The ML pipeline is a **sequential chain** of 5 stages across multiple notebooks. The active production path runs through all stages. An alternative multi-feature approach (`expected_price.ipynb`) exists alongside the active path but is not used for the final output.

```
Stage 0: data_preprocess_merge.ipynb
         → data_preprocess_merge.xlsx
            (raw player data + 5-year historical stats merged)
   ↓
Stage 1: ratings.ipynb
         → output_rp.csv
            (ExpectedMf + MyRating computed)
   ↓
Stage 2A: fvm_to_distribution.ipynb
          → regressors/model_[mean,std]_[P,D,C,A].joblib
             (LinearRegression models trained from real auction data)
   ↓
Stage 2B: regressors.ipynb
          → players23_24_nostats.csv  [ACTIVE FINAL OUTPUT]
             (predictions applied + exploratory model training)
   ↓ (alternative inference, not active)
Stage 3:  expected_price.ipynb
          → players23_24_nostats.csv  [ALTERNATIVE]
             (MultiOutput Lasso inference, 5 features)
   ↓ (post-hoc analysis)
Stage 4:  explorations/expected_value_players.ipynb
          → player_prices.xlsx, friends_prices.xlsx
             (empirical inter-league price comparison)
```

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

### 3.4 Stage 2A — `fvm_to_distribution.ipynb` (Model Training from Real Auction Data)

```
Input:  24-25/output_rating.csv  (player roster from Stage 1)
        24-25/Rose_fantalega-nicosia.xlsx  (real fantasy league auction data)

Processing:
  → Reads real league data: team rosters with actual auction prices paid by users
  → Groups all auction prices by player name across all teams
  → Computes empirical mean, std, count per player
  → Filters to players with count >= 8 (sufficient data points)
  → Merges with player FVM data
  → Filters by minimum FVM per role (e.g., D: FVM >= 10)
  → Trains 2 LinearRegression models per role (P, D, C, A):
    - model_mean: FVM → empirical auction price mean
    - model_std:  FVM → empirical auction price std
  → Saves models as .joblib files

Output: regressors/model_[mean,std]_[P,D,C,A].joblib  (8 LinearRegression models)
        24-25/player_prices_[P,D,C,A].xlsx  (per-role price analysis)
```

**This is the bridge between real market data and model predictions.** The LinearRegression models are trained on empirical auction prices from real leagues, using FVM as the sole input feature. The models learn the FVM → price relationship from actual market behavior. These are the exact models loaded by `regressors.ipynb` (Stage 2B).

### 3.5 Stage 2B — `regressors.ipynb` (Inference + Model Experiments)

```
Input:  24-25/output_rp.csv                        (from Stage 1)
        regressors/24-25/model_[mean,std]_[P,D,C,A].joblib  (from Stage 2A)

Production cells (cells 0-11):
  → Loads 8 pre-trained joblib models (one mean + one std per role P/D/C/A)
  → For each player, predicts mean and std from FVM alone:
    - model_mean_[role].joblib → predicts expected auction price
    - model_std_[role].joblib  → predicts auction price std
  → Results clamped: mean >= 1
  → Selects columns via columns_to_keep: Id, Role, Name, Squad, Price, MyRating,
    Mate, Regularness, FVM, ExpectedMf, mean, std
  → Renames to BE contract names: mean→expPrice, std→expStd, ExpectedMf→expMf,
    plus all others to lowercase
  → Final output: players23_24_nostats.csv (filename is stale, should be updated per season)

Exploratory cells (cells 12-45):
  → Experiments with GPR, Ridge, Lasso, and SVM models on multi-feature inputs
  → Feature matrix: [Squad, Price, MyRating, Regularness, FVM]
  → Trains and saves models as .pkl files (e.g., lasso_regressor_model_A.pkl)
  → The saved .pkl models are loaded by expected_price.ipynb (Stage 3)
  → Cell 1 note: "Every year I have to put the mean of the previous years to increase the accuracy of the regressors"
    Models are retrained annually, accumulating historical data from prior seasons.
  → GPR deemed unsuitable (overfitting on sparse price data)
  → Ridge and Lasso show desired behavior, outperform plain LinearRegression on MAE
```

**Why FVM-only for production?** Earlier experiments with MultiOutput Lasso (5 features: Squad, Price, MyRating, Regularness, FVM) showed that FVM dominated the regression coefficients at ~99%, while other features had negligible or counterintuitive impact. The simpler FVM-only LinearRegression models (trained from real auction data in Stage 2A) were adopted as the active approach.

### 3.6 Stage 3 — `expected_price.ipynb` (Alternative Inference, Not Active)

```
Input:  output_rating.csv  (root-level, from older preprocessing — NOT output_rp.csv)
        squads.csv          (root-level)
        lasso_regressor_model_[P,D,C,A].pkl  (4 pre-trained MultiOutput Lasso models)

Processing:
  → Reads output_rating.csv
  → Converts Squad names to numeric values via squads.csv
  → Loads 4 MultiOutput Lasso models (one per Role P/D/C/A)
  → Features: [Squad, Price, MyRating, Regularness, FVM] (5 features)
  → Each model outputs 2 values simultaneously: [ExpectedPrice, ExpectedPriceStd]
  → Results clamped: mean >= 1, std >= 1
  → Saves: players23_24_trial.csv (full), players23_24_nostats.csv (trimmed, no stats columns)
```

**Connection to regressors.ipynb:** The Lasso models loaded here (`lasso_regressor_model_[P,D,C,A].pkl`) are trained by the exploratory cells in `regressors.ipynb` (Stage 2B). This notebook is an alternative inference path that was used before the FVM-only approach was adopted. It reads a different input file (`output_rating.csv` vs `output_rp.csv`) and uses a different model family.

**Current state:** Only `lasso_regressor_model_A.pkl` and `lasso_regressor_model_C.pkl` exist at root level. `lasso_regressor_model_D.pkl` and `lasso_regressor_model_P.pkl` are missing (present only in `24-25_trial/`). This notebook may not run as-is without retraining the missing models.

### 3.7 Stage 4 — `explorations/expected_value_players.ipynb` (Post-Hoc Analysis)

```
Input:  output_rating.csv  (player roster)
        Rose_*.xlsx        (real fantasy league exports with actual auction prices)

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

**Relationship to Stage 2A:** Both `fvm_to_distribution.ipynb` and this exploration notebook use the same empirical approach (computing mean/std from Rose_*.xlsx auction data). The key difference: Stage 2A trains the production LinearRegression models from this data; the exploration notebook computes per-player price comparisons across leagues for arbitrage analysis.

**Purpose:** Validates model predictions against real market behavior and identifies inter-league price discrepancies. **Integration strategy for improving production models is TBD** — to be revisited when historical work from the other laptop is available.

### 3.8 What the BE Actually Imports

The BE's `PlayerCreateDto` expects these CSV columns (case-insensitive):

```
id, role, name, squad, price, myrating, mate, regularness, fvm, expmf, expprice, expstd
```

The **active pipeline** (`regressors.ipynb` final output) produces exactly these columns via its rename step:
- `Id` → `id`, `Role` → `role`, `Name` → `name`, `Squad` → `squad`, `Price` → `price`
- `MyRating` → `myRating`, `Mate` → `mate`, `Regularness` → `regularness`, `FVM` → `fvm`
- `ExpectedMf` → `expMf`, `mean` → `expPrice`, `std` → `expStd`

**The column names match the BE contract.** The output filename (`players23_24_nostats.csv`) is stale and should be updated per season.

### 3.9 What `expPrice` and `expStd` Actually Represent

`expPrice` and `expStd` are grounded in **real auction data**, not pure model invention. The pipeline trains LinearRegression models on empirical auction prices from real fantasy leagues (Stage 2A), then applies those models to all players via FVM (Stage 2B).

- `expPrice` = predicted auction price (mean), derived from FVM via role-specific LinearRegression trained on real auction data
- `expStd` = predicted auction price std deviation, derived from FVM via role-specific LinearRegression trained on real auction data
- `expMf` = expected mean fantasy score, computed in `ratings.ipynb` from FVM + age performance curves

This maps cleanly to the BE's `ExpectedPrice` (int) and `ExpectedStd` (double) fields. **There is no ambiguity** — the active pipeline produces a single pair of (price, price_std) values per player.

### 3.10 Output File Locations

| File | Location | Source | Status |
|---|---|---|---|
| `Quotazioni_Fantacalcio_Stagione_2024_25.xlsx` | `24-25/` | Official SkySport | Stage 0 input |
| `stats/*.xlsx` | `stats/` | 5-year historical match stats | Stage 0 input |
| `data_preprocess_merge.xlsx` | `24-25/` | data_preprocess_merge.ipynb | Stage 0 output |
| `output_rp.csv` | `24-25/` | ratings.ipynb | Stage 1 output (active) |
| `output_rp.csv` | `24-25_trial/` | Old trial run | Stale |
| `output_rp.csv` | `24-25oldge/` | Old trial run | Stale |
| `Rose_*.xlsx` | `24-25/`, `24-25_trial/` | Fantasy league exports | Stage 2A + Stage 4 input |
| `model_[mean,std]_[P,D,C,A].joblib` | `regressors/24-25/` | fvm_to_distribution.ipynb | Stage 2A output |
| `players23_24_nostats.csv` | root | regressors.ipynb (active) | **Active final output** (filename stale) |
| `players23_24_trial.csv` | root | expected_price.ipynb | Stage 3 output (alternative) |
| `lasso_regressor_model_*.pkl` | root, `24-25_trial/` | regressors.ipynb exploratory | Stage 2B experiments |
| `ridge_regressor_model_*.pkl` | root, `24-25_trial/` | regressors.ipynb exploratory | Stage 2B experiments |
| `player_prices.xlsx` | root | explorations/expected_value_players.ipynb | Stage 4 output |
| `friends_prices.xlsx` | root | explorations/expected_value_players.ipynb | Stage 4 output |

---

## 4. Gap Analysis

### What the BE needs vs what the ML produces (new pipeline)

| BE Requires | ML Produces (regressors.ipynb output) | Gap |
|---|---|---|
| `id` | `id` | OK |
| `role` | `role` | OK |
| `name` | `name` | OK |
| `squad` | `squad` | OK |
| `price` | `price` | OK |
| `myrating` | `myRating` | OK (case-insensitive match) |
| `mate` | `mate` | OK (but stored as player name, not ID) |
| `regularness` | `regularness` | OK |
| `fvm` | `fvm` | OK |
| `expmf` | `expMf` | OK (case-insensitive match) |
| `expprice` | `expPrice` | OK (case-insensitive match) |
| `expstd` | `expStd` | OK (case-insensitive match) |
| `age` | `Age` in intermediate, **dropped from final output** | **Missing from final CSV** — regressors.ipynb drops Age when selecting columns |
| `role_m` | `Role_M` in intermediate, **dropped from final output** | **Missing from final CSV** — regressors.ipynb drops Role_M when selecting columns |

### Remaining gaps

| Gap | Location | Detail |
|---|---|---|
| `Age` dropped from final output | `regressors.ipynb` (columns_to_keep) | Age is present in `output_rp.csv` but not included in `columns_to_keep`. Add `"Age"` to the list. |
| `Role_M` dropped from final output | `regressors.ipynb` (columns_to_keep) | Role_M (semicolon-separated sub-positions) is present but not included. Add `"Role_M"` to the list and rename to `role_m`. |
| `Mate` stored as name | ML + BE | ML outputs mate's player name. Names are fragile (ambiguous, change across seasons). Recommended: ML outputs mate's SkyBet ID, BE stores `MateId int?`. |
| BE doesn't import `Age` | `PlayerCreateDto` | Entity has `Age`, but DTO doesn't. Even if ML includes it, BE won't import it until `Age` is added to `PlayerCreateDto`. |
| BE stubs `Role_M` | `PlayerService.cs:46` | Set to `new List<string> { p.Role }`. Needs `Split(';')` parsing. Also needs `Role_M` added to `PlayerCreateDto`. |
| Output filename stale | `regressors.ipynb` | Writes `players23_24_nostats.csv` regardless of season. Should use dynamic season-based naming. |

---

## 5. Required ML Pipeline Changes

### 5.1 Output CSV format (target)

The ML pipeline must produce a single final CSV with **at least these columns** (order doesn't matter, CsvHelper matches by name case-insensitively):

```
id, role, name, squad, price, myrating, mate, regularness, fvm, expmf, expprice, expstd, age, role_m
```

### 5.2 Specific changes

| Change | Priority | Detail |
|---|---|---|
| Include `Age` in final output | **HIGH** | Add `"Age"` to `columns_to_keep` in `regressors.ipynb`. Rename to lowercase `age` in the rename dict. |
| Include `Role_M` in final output | **HIGH** | Add `"Role_M"` to `columns_to_keep`. Rename to `role_m`. Semicolon-separated (e.g., `Dd;Ds;Dc`). |
| Dynamic output filename | MEDIUM | Replace hardcoded `players23_24_nostats.csv` with season-based naming (e.g., `players24_25.csv`). |
| Strip historical stats from final | LOW | Columns `Pg23_24` through `Mf19_20` are not consumed by BE. Already handled by `columns_to_keep` filtering. |
| Mate → MateId migration | FUTURE | ML should output mate's SkyBet ID instead of name. Requires BE schema change (`MateId int?`). |

### 5.3 Pipeline stages summary

```
Stage 0: data_preprocess_merge.ipynb  → raw data + 5-year stats merged → data_preprocess_merge.xlsx
Stage 1: ratings.ipynb                → ExpectedMf + MyRating → output_rp.csv
Stage 2A: fvm_to_distribution.ipynb   → train LinearRegression from real auction data → .joblib models
Stage 2B: regressors.ipynb            → apply models + exploratory training → players23_24_nostats.csv  [ACTIVE]
Stage 3:  expected_price.ipynb        → MultiOutput Lasso inference → players23_24_nostats.csv  [ALTERNATIVE]
Stage 4:  explorations/expected_value_players.ipynb  → inter-league price comparison  [VALIDATION]
```

`expected_price.ipynb` (Stage 3) is an alternative inference path using 5-feature Lasso models trained by `regressors.ipynb`'s exploratory cells. Not part of the active flow.

`explorations/expected_value_players.ipynb` (Stage 4) computes empirical price comparisons from real auction data. Serves as validation for model predictions. Integration strategy for improving production models is TBD.

**See `FantaHelpML/docs/refactoring-plan.md` for the full ML repo refactoring plan.**

---

## 6. BE-Side Improvements (Optional, Post-ML-Rework)

These are BE changes that would make the contract cleaner, but are not blockers:

| Change | Priority | Detail |
|---|---|---|
| Import `Age` from CSV | LOW | Add `Age` to `PlayerCreateDto` so it's populated from data |
| Import `Role_M` properly | MEDIUM | Parse `Role_M` column (e.g., comma-separated or JSON array) instead of stubbing |
| Soft import mode | MEDIUM | Add a non-destructive import option (upsert by `Id` instead of wipe+insert) |
| Validate import data | LOW | Add range checks (e.g., `ExpMf` > 0, `ExpPrice` > 0) before DB write |

---

## 7. Annual Flow Checklist

Every season, the flow is:

```
FantaHelpML (produce data)  →  FantaHelpBE (import + serve)  →  FantaHelpFE (consume via API)
```

### Step 1: ML produces new season data

- [ ] Run `data_preprocess_merge.ipynb` (Stage 0) → produces `data_preprocess_merge.xlsx`
- [ ] Run `ratings.ipynb` (Stage 1) → produces `output_rp.csv` with ExpectedMf and MyRating
- [ ] Run `fvm_to_distribution.ipynb` (Stage 2A) → trains LinearRegression models from real auction data → `.joblib` files
- [ ] Run `regressors.ipynb` (Stage 2B) → applies models → produces final CSV with expPrice, expStd, expMf
- [ ] (Optional) Run `explorations/expected_value_players.ipynb` (Stage 4) → produces empirical price stats for validation
- [ ] Verify output CSV has columns: `id, role, name, squad, price, myrating, mate, regularness, fvm, expmf, expprice, expstd`
- [ ] Spot-check a few players for reasonable values
- [ ] (Optional) Compare model-predicted expPrice/expStd against empirical values from exploration notebook

### Step 2: BE imports data

- [ ] BE is running (DB up, migrations applied)
- [ ] Upload CSV via `POST /api/players/import` (or `curl` / Swagger)
- [ ] Verify import succeeded: `GET /api/players` returns expected count
- [ ] Spot-check ML fields: `ExpectedPerformance`, `ExpectedPrice`, `ExpectedStd` are non-zero

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
| `Players` | `Id`, `Name`, `Squad`, `Role`, `Role_M`, `Price`, `Age`, `Rating`, `Mate`, `Regularness`, `FVM`, `ExpectedPerformance`, `ExpectedStd`, `ExpectedPrice` | |
