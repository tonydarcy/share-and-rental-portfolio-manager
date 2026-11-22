# AGENTS.md

Guidance for AI coding agents working in **Share Portfolio Manager** — an open-source (AGPL-3.0) Australian and multi-region share portfolio, rental property, double-entry accounting, business books, and year-end tax platform.

Live site: https://app.shareportfoliomanager.com  
Owner: Tony (Australian tax agent). Roadmap: `ROADMAP.md`. Setup guide: `SETUP.md`.

---

## Read First, Read Little Else

Keep context small and targeted:

- Work in `src/`, `tests/`, and `Database/Migrations/` unless asked otherwise. The legacy Web Forms site has been removed.
- **Do not open `Database/CreateDatabase.sql` in full** (~516 KB baseline schema + migrations through `2026-10-16`). To inspect a table or stored procedure, search and slice it:
  ```bash
  grep -n "PROCEDURE \[dbo\].\[uspAddPayout\]" Database/CreateDatabase.sql
  sed -n '<line>,+100p' Database/CreateDatabase.sql
  ```
  A procedure can appear multiple times; the **last** match is current unless overridden by a newer file in `Database/Migrations/`.
- `README-razor-pages.md` (~92 KB) contains detailed feature notes, the database migration log, API/MCP docs, and IIS/price-updater setup. List headings first (`grep -n '^## ' README-razor-pages.md`) and read only the section you need.
- For large files (Navexa sync/import, `BankImportService.cs`, large migrations, large test suites), grep for the method or symbol you need and read around it rather than reading the entire file.

---

## Repository Layout

Solution file: `PortfolioManager.slnx` (.NET 10, C#, nullable enabled, implicit usings).

| Path | Purpose |
| --- | --- |
| `src/PortfolioManager.Core/<Area>/` | Core domain logic, validation, parameter mapping, and Dapper data access over SQL Server stored procedures. **No ASP.NET/web dependencies.** Areas cover portfolio (`Trades`, `Cash`, `Payouts`, `CorporateActions`, `Crypto`, `Debt`, `Holdings`, `Performance`, `Prices`, `Portfolios`, `Groups`, `Dashboard`), accounting & business books (`Accounting`, `Invoicing`, `Bills`, `Orders`, `Inventory`, `Claims`, `Assets`, `SalesTax`, `Statements`, `Exports`, `Reading`), tax & rentals (`Tax`, `TaxWork`, `Rentals`, `Clients`, `Practice`), and platform/integrations (`Accounts`, `Alerts`, `Api`, `Assistant`, `Backup`, `Email`, `Import`, `Imports`, `NavexaSync`, `Regions`, `Reports`, `Security`, `Validation`). |
| `src/PortfolioManager.Core/Data/` | `IDbConnectionFactory`, `ProcResult` (parses `"Error: ..."` / success rows from write procs), and `AnsiText` (sends `varchar` instead of `nvarchar` for inline SQL). |
| `src/PortfolioManager.Web/Pages/<Area>/` | ASP.NET Core Razor Pages mirroring Core areas. UI uses Bootstrap 5.3, Bootstrap Icons, and Chart.js (from jsDelivr with SRI) plus `wwwroot/` assets and PWA manifest/service worker. |
| `src/PortfolioManager.Web/Program.cs` | DI registration, auth (cookie + API key), rate limiting, security headers, per-request region/culture middleware, Razor Page global filters, API/MCP/OAuth route mapping. |
| `src/PortfolioManager.Web/Api/` | JSON REST API (`/api/v1`), MCP server (`/mcp`), and OAuth 2.0 PKCE server (`/Account/Connect`). Both REST and MCP execute the shared tools in `AgentTools*.cs` (`AgentTools.cs`, `AgentTools.Accounts.cs`, `AgentTools.Books.cs`, `AgentTools.Property.cs`, `AgentTools.Mobile.cs`). |
| `src/PortfolioManager.Web/Services/` | Web-only helpers and filters: `CurrentPortfolio`, `AuditedConnectionFactory`, `ViewOnlyFilter`, `LockedPeriodFilter`, `AdminTwoFactorFilter`, `TermsFilter`, `SecurityHeaders`, `SiteAdmin`, `MainMenu`, `CsvUpload`, `AssistantChat`, schedulers, and token protectors. |
| `src/PortfolioManager.Mobile.Client/` | Plain `net10.0` client library for the phone app (OAuth PKCE sign-in, `/api/v1` calls, encrypted offline copy). Included in `PortfolioManager.slnx` and unit-tested by `MobileClientTests.cs`. |
| `src/PortfolioManager.Mobile/` | `.NET MAUI` phone app (`net10.0-android`; `net10.0-ios` on macOS). **Intentionally excluded from `PortfolioManager.slnx`** so the web solution builds without mobile workloads; built by `.github/workflows/android.yml`. |
| `src/PortfolioManager.PriceUpdater/` | Daily console job (Windows Task Scheduler) fetching EODHD prices, FX rates, announced dividends/splits, and optional ETF holdings via `uspUploadSharePrice` and related procs. |
| `tests/PortfolioManager.Tests/` | xUnit unit tests (one file per feature, e.g. `PayoutTests.cs`). Tests cover calculations, validation, parameter mapping, API/MCP/OAuth, and mobile client logic — **no database required**. |
| `tests/smoke/` | CI end-to-end verification: `build-database.sh`, `smoke.sh`, and SQL invariant checks (`delete-check.sql`, `convert-check.sql`, `portfolio-column-check.sql`). |
| `Database/CreateDatabase.sql` | Fresh-install baseline through `2026-10-16`. **Never edit it**; put all changes in `Database/Migrations/`. |
| `Database/Migrations/` | Dated, idempotent T-SQL migration scripts applied in filename order. |
| `Database/SampleData/` | Demo portfolio seed data used by Docker (`LOAD_SAMPLE_DATA=1`) and CI smoke tests. |
| `deploy/` | Windows/IIS update and rollback scripts (`Deploy.cmd`, `Rollback.cmd`, `deploy.ps1`, `README.md`). |
| `Dockerfile`, `docker-compose.yml`, `docker/init-db.sh` | Containerized web + SQL Server 2022 stack for local development and self-hosting. |

### Adding a New Feature

Copy the shape of a neighboring feature:

1. Core service + validation/mapping types in `src/PortfolioManager.Core/<Area>/` (see `src/PortfolioManager.Core/Trades/TradeService.cs` and `TradeEntry.cs` for a clean example).
2. Register the service in `src/PortfolioManager.Web/Program.cs`.
3. Razor Page(s) in `src/PortfolioManager.Web/Pages/<Area>/` (and menu entries in `MainMenu.cs` if applicable; call `CurrentPortfolio.ForgetMenu(...)` when records affecting menu visibility change).
4. If exposing to AI agents, scripts, or the phone app, add a tool in the matching `AgentTools*.cs` partial class and map its route in `ApiEndpoints.cs`.
5. Unit tests in `tests/PortfolioManager.Tests/<Feature>Tests.cs`.
6. Update `SETUP.md` if new configuration or setup steps are introduced.

---

## Architecture Rules & Invariants

### Database & Stored Procedures

- **All financial writes must go through stored procedures — never inline SQL.** Trades, cash transactions, payouts, splits, DRP, corporate actions, and journals are written via stored procedures so double-entry journals (Hybrid/DoubleEntry mode, the default for new portfolios) and FIFO/parcel CGT stay accurate.
- **Transactions:** If a stored procedure runs multiple statements without its own transaction, wrap the call in a transaction in C# (see `TradeService.AddAsync`).
- **Write-proc results:** Parse status text using `ProcResult.From(result, successStart, id)`.
- **Inline SQL & `varchar` parameters:** When writing read-only inline SQL filtering on `varchar` columns (stock codes, exchange codes, FX pairs), wrap string arguments with `AnsiText.Of(...)` so Dapper does not send `nvarchar` and cause full table scans under `SQL_Latin1` collation. (Not needed for stored procedure calls.)
- **Migrations:**
  - Create a new `Database/Migrations/YYYY-MM-DD-<name>.sql` (or `YYYY-MM-DD-NN-<name>.sql` when multiple scripts share a date — **alphabetical filename order is execution order**).
  - Must be **idempotent** (`CREATE OR ALTER`, `IF NOT EXISTS`, `IF COL_LENGTH(...) IS NULL`, etc.) — CI executes every post-baseline migration **twice**.
  - **Never include a `USE <db>` statement** (installers choose their own database name).
  - **SQL Server compatibility level 120** (live databases): do not use T-SQL features introduced after SQL Server 2014 (e.g. avoid `STRING_AGG`).
  - Bracket T-SQL keywords used as column aliases (e.g. `AS [Close]`).
  - Portfolio child tables carry `iUserID` maintained by `trg<table>Portfolio` triggers (see `2026-11-11-portfolio-id-on-child-tables.sql`); `tests/smoke/portfolio-column-check.sql` enforces no `NULL` `iUserID` rows.
  - Every `"usp..."` or `"dbo.usp..."` string literal in `src/` is verified against `sys.procedures` by `tests/smoke/build-database.sh` — do not leave dead or unmigrated proc references in code.
  - Add an entry for every new migration in the **"Database migration"** section of `README-razor-pages.md`.

### Security, Scoping & Request Pipeline

- **Portfolio scoping:** Every Razor Page must use `CurrentPortfolio` (`GetSelectedAsync()`) to determine the active portfolio. **Never trust a portfolio ID from query strings or form posts.**
- **Authentication & authorization:** Every Razor Page requires authentication via the fallback policy unless explicitly marked `[AllowAnonymous]`. API (`/api/v1`) and MCP (`/mcp`) endpoints use API key authentication only (`ApiKeyAuthenticationHandler`), never cookies.
- **Global Razor Page filters** (configured in `Program.cs`):
  - `ViewOnlyFilter`: Blocks non-GET/HEAD/OPTIONS requests when the picked portfolio has `CanEdit == false`, unless the PageModel is decorated with `[AllowViewOnly]`.
  - `LockedPeriodFilter`: Catches SQL locked-period errors (`LockedPeriodService.ErrorNumber`) on `POST` and surfaces the message in `TempData["Error"]`.
  - `TermsFilter` and `AdminTwoFactorFilter`: Enforce terms acceptance and admin 2FA when enabled.
- **Audit trail:** `AuditedConnectionFactory` sets `SESSION_CONTEXT('pm_user')` on opened SQL connections for database audit triggers.
- **CSP & security headers:** `SecurityHeaders.cs` restricts scripts/styles/fonts to `'self'`, `https://cdn.jsdelivr.net` (pinned with SRI hashes), and Cloudflare Turnstile. Do not introduce third-party CDNs without updating `SecurityHeaders.cs`.
- **No secrets or real client data in git.** Connection strings and API keys live only in `dotnet user-secrets` (dev), environment variables (`ConnectionStrings__Default`, etc.), or `appsettings.Production.json` on the server.

### Regional Settings & Australian Tax Rules

- Each portfolio has its own country, financial year start, currency, and number/date format (`RegionSettings`, configured on Portfolio settings), defaulting to Australia (`AU`, 1 July–30 June, `AUD`, `en-AU`).
- `RegionSettings.Current` (`AsyncLocal`) and `CultureInfo.CurrentCulture` / `CurrentUICulture` are set per signed-in request in `Program.cs`, so `FinancialYear` and `ToString("C")` follow the active portfolio automatically.
- Australian-specific tax features (franking credits, 50% CGT discount for assets held > 12 months, ATO return labels, BAS/activity statement, SMSF/trust/company rules) must only appear or apply when `RegionSettings.IsAustralia` is true.

### Dependencies, Reuse & Licensing

- **Look before writing unfamiliar features:** Do a brief search on GitHub and NuGet for existing open-source solutions, compare one or two options, and prefer well-maintained NuGet packages over hand-rolling or copying code.
- **Licence compatibility (AGPL-3.0):** Any NuGet package or adapted code must be AGPL-3.0 compatible (`MIT`, `BSD`, `Apache-2.0`, `LGPL`, `GPL-3.0`, or `AGPL-3.0` — **not** `GPL-2.0-only`, `SSPL`, or non-commercial licences). Preserve copyright/licence notices on any copied code.
- **Tax rule verification:** Any tax calculation or rule (even sourced from Australian open-source projects) must be called out in the PR description as awaiting Tony's review.

---

## Build, Test & CI

Requires **.NET 10 SDK** (`.claude/hooks/install-dotnet.sh` installs `dotnet-sdk-10.0` in cloud environments; manually: `apt-get install -y dotnet-sdk-10.0`).

### Standard Commands

Always use quiet flags to keep output readable (errors and test failures still print):

```bash
# Build the main solution
dotnet build PortfolioManager.slnx -v q --nologo

# Run all unit tests
dotnet test PortfolioManager.slnx -v q --nologo --logger "console;verbosity=minimal"

# Run a targeted test class or method when debugging a failure
dotnet test PortfolioManager.slnx -v q --nologo --filter "FullyQualifiedName~PayoutTests"

# Run the web app locally (requires ConnectionStrings:Default via user-secrets or env var)
dotnet run --project src/PortfolioManager.Web

# Run web + SQL Server 2022 together via Docker (serves http://localhost:8080)
docker compose up -d
```

### Mobile App (`.NET MAUI`)

`src/PortfolioManager.Mobile/` is not in `PortfolioManager.slnx`. Building it locally requires the `maui-android` workload (see `src/PortfolioManager.Mobile/README.md`). Changes to `src/PortfolioManager.Mobile/**` or `src/PortfolioManager.Mobile.Client/**` trigger `.github/workflows/android.yml` to build the APK. Keep HTTP/OAuth/offline logic in `src/PortfolioManager.Mobile.Client/` so it stays covered by `dotnet test PortfolioManager.slnx`.

### CI Checks (`.github/workflows/ci.yml`)

Every push to `main` and every pull request runs two jobs:

1. **`build`**: Release build and full xUnit test suite on `PortfolioManager.slnx`.
2. **`database-and-pages`**:
   - Boots SQL Server 2022 and runs `tests/smoke/build-database.sh`: builds from `CreateDatabase.sql`, runs every post-`2026-10-16` migration **twice**, loads `Database/SampleData/`, runs `delete-check.sql`, `convert-check.sql`, and `portfolio-column-check.sql`, and verifies every `usp*` procedure referenced in `src/` exists in the database.
   - Starts `PortfolioManager.Web`, flushes SQL Server caches, and runs `tests/smoke/smoke.sh`: signs in as `demo@example.com`, crawls every parameterless `@page` and every discovered internal link, and fails on HTTP 400/404/5xx, unexpected redirects to `/Account/Login`, or any page taking > 15 seconds.

Always run `dotnet build` and `dotnet test` locally before pushing.

---

## Deployment (Windows / IIS VPS)

Tony deploys manually on the Windows VPS by double-clicking `deploy\Deploy.cmd` inside the repository clone (see `deploy/README.md`):

- Pulls `main`, lists new `Database/Migrations/*.sql` scripts and pauses while Tony runs them in SSMS.
- Publishes `PortfolioManager.Web`, backs up `wwwroot` to `_deploy`, copies the published output behind `app_offline.htm` (never touching `appsettings.Production.json` and never copying `src/`, `tests/`, or `.git`), and checks the home page.
- `deploy\Rollback.cmd` restores the newest backup (migrations are additive/backward-compatible).
- **Script constraint:** Keep `deploy/deploy.ps1` and `.cmd` scripts **Windows PowerShell 5.1 compatible and ASCII-only**.

---

## Code Style & PR Conventions

- **C# / .NET 10:** File-scoped namespaces, primary constructors, `async`/`await` end-to-end, nullable reference types enabled.
- **Plain English:** Write all UI text, validation messages, error messages, and PR descriptions in plain English for a non-developer / accountant reader.
- **Pull requests:**
  - One feature or fix per PR, with unit tests for all calculations and parameter mappings.
  - Follow `.github/pull_request_template.md` (clear **Before / After** user-facing summary).
  - Sign off commits for DCO compliance (`git commit -s` adds `Signed-off-by:`).
