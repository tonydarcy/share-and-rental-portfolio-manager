# Share Portfolio Manager

Free, open-source share portfolio tracking, double-entry accounting, invoicing and year-end tax for Australian
investors, SMSFs, family trusts and investment companies, and the accountants who look after them.

It does what people usually pay several subscriptions for: tracking holdings and performance (like Navexa or
Sharesight), keeping the books and producing financial statements (like Xero), and the year-end tax and SMSF work
(like Class or BGL). Every trade, dividend and corporate action posts its own journal, so the portfolio, the ledger and
the tax figures always agree. The entity's owner and their accountant can work in the same books, each with their own
login.

Live site: https://app.shareportfoliomanager.com

## What it does

- **Portfolio:** holdings, trades, cash, dividends and distributions (with franking), DRP, splits, returns of capital,
  demergers, takeovers and code changes, foreign shares in their own currency, managed funds, unlisted shares,
  property and other assets with manual prices, margin loans, term deposits and bonds.
- **Reports:** performance against an index, contribution by holding, diversity with ETF look-through, income
  calendar, what-if sale, tax-loss selling, and a consolidated view across a family's portfolios. Email alerts for
  price targets and announced dividends. Works on a phone and can be added to the home screen.
- **Imports:** broker CSVs (CommSec, CMC, SelfWealth, Stake, Pearler, Superhero, Interactive Brokers and others),
  QIF/OFX bank statements with allocation rules, Navexa import and sync, daily end-of-day prices, dividends and
  splits from EODHD, and reading receipts and bills from a photo.
- **Accounting:** chart of accounts, journals (including repeating and reversing), bank reconciliation, trial balance,
  P&L, balance sheet, budgets and tracking categories, foreign currency accounts, market value revaluation, locked
  periods, audit trail, attachments, export to Xero or MYOB, financial statements for each entity type and a printable
  report pack with an APES 315 compilation report.
- **Business books:** invoices, quotes, credit notes and repeating invoices; bills, suppliers and purchase orders;
  inventory and stocktakes; expense claims; a fixed asset register with depreciation; GST, VAT or sales tax by return
  box.
- **Tax (Australia):** FIFO capital gains with the 50% discount and a choice of parcels, taxable income, fund (AMIT)
  tax statements, employee share schemes, shares as trading stock, rental properties, tax reconciliation and planning,
  trust distributions, SMSF member accounts, TBAR and audit pack, company tax and franking account, Division 7A, ATO
  prefill comparison, tax return labels and a sheet for a paper return.
- **For accountants:** clients, a practice dashboard, year-end checklist and workpapers, client queries and
  onboarding. Share an entity with its owner, accountant, bookkeeper or auditor as an owner, editor or read only user.
- **API and AI assistants:** a JSON API and an MCP server, so Claude or another AI assistant (or your own app) can read
  the portfolio, add trades and cash, import and allocate bank transactions, read invoices and run reports, using
  per-user API keys that can be read only. Every change an agent makes is logged.

Each portfolio can be set to another country, financial year and currency, but the tax rules are Australian. What is
missing for the UK, the US, Canada and India is listed in [ROADMAP.md](ROADMAP.md#using-it-outside-australia).

What's next is in [ROADMAP.md](ROADMAP.md).

## Built with

ASP.NET Core 10 Razor Pages (C#), SQL Server with stored procedures (Dapper), xUnit. Runs on Windows (IIS) or Linux.

| Folder | What |
| --- | --- |
| `src/PortfolioManager.Core` | Services and calculations, no web code |
| `src/PortfolioManager.Web` | The website (Razor Pages) |
| `src/PortfolioManager.PriceUpdater` | Daily price download job |
| `tests/PortfolioManager.Tests` | Unit tests |
| `Database/CreateDatabase.sql` | Builds a new, empty database |
| `Database/Migrations` | SQL scripts applied in date order |
| `samples` | Example import files |

## Running it yourself

[SETUP.md](SETUP.md) walks through it step by step, from installing the tools to running it on a Windows or Linux
server. In short:

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and SQL Server (Express, Developer or
   the Docker image is fine).
2. Create an empty database and run `Database/CreateDatabase.sql` in it, then every script in `Database/Migrations`
   dated after 2026-10-16, in file name order. Or run `docker compose up -d`, which does all of this for you.
3. Give the app its connection string, kept outside the repo:
   ```
   cd src/PortfolioManager.Web
   dotnet user-secrets set "ConnectionStrings:Default" "Server=.;Database=PortfolioManager;Trusted_Connection=true;TrustServerCertificate=true"
   ```
4. `dotnet run --project src/PortfolioManager.Web`, open the address it prints and register. The first account
   registered is the site administrator.
5. Follow "First steps" at the end of [SETUP.md](SETUP.md#7-first-steps) to set up your first entity, share it and
   raise an invoice.

## Contributing

Bug reports, ideas and code are all welcome, especially from accountants and investors who know what's missing.
See [CONTRIBUTING.md](CONTRIBUTING.md). Please report security problems privately as described in
[SECURITY.md](SECURITY.md).

## Not advice

This software helps you keep records and prepare figures. It is not financial or tax advice, and its results should be
checked by someone qualified before they are relied on or lodged.

## Licence

[GNU Affero General Public License v3.0](LICENSE). You may use, change and share it freely. If you run a changed
version for other people over a network, you must offer them its source code under the same licence.
