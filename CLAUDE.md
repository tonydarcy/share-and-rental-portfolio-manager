# CLAUDE.md

Share Portfolio Manager: an Australian share portfolio site (ASX, CGT discount, franking credits, double-entry ledger).
Live at https://app.shareportfoliomanager.com. Owner: Tony (tax agent). Open source (AGPL-3.0) is the goal; what's next is in `ROADMAP.md`.

## Read this first, read little else

- Work only in `src/`, `tests/` and `Database/Migrations/` unless asked. The old Web Forms site has been removed.
- **Don't open `Database/CreateDatabase.sql`** (530 KB fresh-install script: the base schema, then every migration up to
  2026-10-16). To see one proc or table, grep for it:
  `grep -n "PROCEDURE \[dbo\].\[uspAddPayout\]" Database/CreateDatabase.sql` then `sed -n 'N,+100p'` from there
  (the Read tool is blocked on it). A proc can appear more than once; the **last** match is current, unless a newer
  file in `Database/Migrations/` changes it.
- `README-razor-pages.md` has the full feature list, migration notes and deploy detail. Read only the section you need
  (`grep -n '^## ' README-razor-pages.md`, then read that range).
- Files over ~20 KB (the Navexa sync/import files, `BankImportService.cs`, the big migrations, their tests): grep for
  the method you need and read around it, not the whole file. The old 2026-10-14 schema dumps in the project's
  shared folder are superseded by `CreateDatabase.sql`; don't open them.

## Layout

| Path | What |
| --- | --- |
| `PortfolioManager.slnx` | The solution. |
| `src/PortfolioManager.Core/<Area>/` | Services (Dapper over stored procs), one folder per area: Trades, Cash, Payouts, CorporateActions, Accounting, Invoicing, Holdings, Tax, Reports, Imports (CSV) and Import (Navexa), Prices, Portfolios, Accounts, Security, Dashboard, and the business books: Bills, Orders, Inventory, Claims, Assets, Reading. No web code (kept reusable for a future mobile API). |
| `src/PortfolioManager.Core/Data/` | `IDbConnectionFactory`, `ProcResult`. |
| `src/PortfolioManager.Web/Pages/<Area>/` | Razor Pages, mirroring the Core areas. `Program.cs` registers services. |
| `src/PortfolioManager.Web/Api/` | JSON API (`/api/v1`) and MCP server (`/mcp`) for AI agents, signed in by API key. Both run the tools in `AgentTools.cs`; add a tool there. |
| `src/PortfolioManager.Web/Services/` | Web-only helpers: `CurrentPortfolio` (portfolio picker, checked per request), `CsvUpload`, `SiteAdmin`. |
| `src/PortfolioManager.PriceUpdater/` | Daily console job (Task Scheduler) pulling EODHD end-of-day prices via `uspUploadSharePrice`. |
| `tests/PortfolioManager.Tests/` | xUnit, one file per feature (e.g. `PayoutTests.cs`). Tests cover parameter mapping and pure logic; no DB. |
| `Database/CreateDatabase.sql` | Builds a new empty database (baseline through 2026-10-16). Never edit it; changes go in migrations. |
| `Database/Migrations/` | Dated SQL scripts, idempotent, run by hand in SSMS before deploying. |
| `SETUP.md` | Install guide for people running their own copy. Update it when setup steps change. |

To add a feature, copy the shape of a neighbouring one: a Core service + its mapping class, a page under the same area,
a test file. `src/PortfolioManager.Core/Trades/TradeService.cs` is a short example.

## Rules

- **Trades, cash, payouts, splits, DRP and journals must be written through the stored procs**, never inline SQL.
  The procs post the double-entry journals (Hybrid/DoubleEntry mode, the default for new users) and keep FIFO CGT right.
  If a proc runs several statements without its own transaction, wrap the call in one (see `TradeService`).
- A proc change goes in a new `Database/Migrations/YYYY-MM-DD-<name>.sql` (or `YYYY-MM-DD-NN-<name>.sql` when a day
  has several, numbered in the order they must run, because file name order is run order) that is safe to run twice (`CREATE OR ALTER`,
  `IF NOT EXISTS`) and has no `USE` line (others name their database differently), plus a line in the README's
  "Database migration" section. T-SQL keywords used as column aliases need brackets (`AS [Close]`).
- Every page is scoped to the picked portfolio via `CurrentPortfolio`; never trust a portfolio id from the form or query string.
- No secrets in the repo. Connection strings live in user-secrets (dev) or `appsettings.Production.json` on the server only.
- Each portfolio has a country, financial year start, currency and format (`RegionSettings`, Portfolio settings page),
  defaulting to Australia (1 Jul to 30 Jun, AUD, en-AU). `RegionSettings.Current` and the request culture are set per
  request, so `FinancialYear` and `ToString("C")` follow it. Franking and the CGT discount (50% for assets held more
  than 12 months) only show when the country is Australia.
- **For an unfamiliar feature, look before you write.** Spend a few searches (not a long survey) on GitHub and NuGet
  for open-source code that already solves it, compare one or two approaches, then build. Prefer a well-maintained
  NuGet package (recent releases, wide use) over copying code. Its licence must be AGPL-3.0 compatible (MIT, BSD,
  Apache-2.0, LGPL, GPL-3.0 or AGPL-3.0; not GPL-2.0-only, SSPL or "non-commercial"), and copied code keeps its
  notice. Tax rules found in other projects, even Australian ones, still go in the PR as awaiting Tony's check.

## Build and test

```
dotnet build PortfolioManager.slnx
dotnet test PortfolioManager.slnx
dotnet run --project src/PortfolioManager.Web
```

Cloud sessions install the .NET 10 SDK at startup (`.claude/hooks/install-dotnet.sh`); if `dotnet` is still missing,
`apt-get install -y dotnet-sdk-10.0`. GitHub Actions (`.github/workflows/ci.yml`) builds and tests every PR, but run
the tests yourself before pushing rather than shipping untested code.

## Deploy (Tony does this on the VPS)

1. Run any new `Database/Migrations/*.sql` in SSMS first.
2. GitHub Desktop on the VPS: Fetch origin, Pull.
3. `dotnet publish src\PortfolioManager.Web -c Release -o Desktop\pm-publish`, copy the contents into the app's wwwroot
   (keep `appsettings.Production.json`), recycle only this app's pool.
4. Never copy `src/`, `tests/`, `publish/` or `.git` into any wwwroot.

## Style

.NET 10, primary constructors, `async` all the way, file-scoped namespaces. Write UI text and PR descriptions
in plain English for a non-developer reader.
