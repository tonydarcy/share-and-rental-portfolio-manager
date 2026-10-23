# Share Portfolio Manager

Free, open-source share portfolio tracking, double-entry accounting, invoicing and year-end tax for Australian
investors, SMSFs, family trusts and investment companies, and the accountants who look after them.

It does what people usually pay several subscriptions for: tracking holdings and performance (like Navexa or
Sharesight), keeping the books and producing financial statements (like Xero), and the year-end tax and SMSF work
(like Class or BGL). Every trade, dividend and corporate action posts its own journal, so the portfolio, the ledger and
the tax figures always agree. The entity's owner and their accountant can work in the same books, each with their own
login.

Live site: https://app.shareportfoliomanager.com. You can use it there, or run your own copy (see
[Running it yourself](#running-it-yourself)).

## What it does

### Portfolio

- Several portfolios per login, one per person or entity, with a picker in the menu bar. Each has its own country,
  financial year, currency and number format (Australia, 1 July to 30 June, AUD by default).
- Holdings with today's value, gain and 200-day trend; share trades, cash accounts, and payouts (dividends,
  distributions, interest and rent) with franking credits and foreign tax.
- Dividend reinvestment plans (DRP), splits and consolidations, bonus issues, returns of capital, demergers (the cost
  base split by percentage), takeovers with or without scrip-for-scrip rollover, code and name changes, worthless or
  delisted shares, and options exercised or lapsed.
- Announced dividends and splits for the stocks you hold, fetched daily, ready to confirm with one click
  (Corporate actions > Announced).
- Foreign shares in their own currency. Trades take that day's exchange rate when none is entered, and a check page
  flags rates that look wrong.
- Managed funds, unlisted shares, property and other assets with prices you enter or upload; crypto priced daily.
- Margin and investment loans (loan-to-value ratio, interest and its deductible part), and term deposits and bonds
  (interest earned and accrued at the year end).
- Custom groups, to classify holdings your own way (for example by risk or strategy).

### Reports

- Performance (time-weighted return, monthly returns) against an index or any share, and contribution by holding,
  split into price gain, income and currency.
- Diversity by sector, country and company, looking through ETFs and funds.
- Income calendar, what-if sale, tax-loss selling (with a warning about wash sales), capital gains, unrealised gains
  and taxable income.
- A consolidated view across a family's portfolios: each entity's value, cash, income and net capital gain, and every
  holding across the group.
- Share quotes and price trends.

### Imports and prices

- Broker CSV exports: CommSec, CMC, SelfWealth, Stake, Pearler, Superhero, Interactive Brokers, and any CSV with a
  header row. An example file is in `samples/`.
- Bank statements (QIF, OFX or CSV) with allocation rules, then bank reconciliation.
- A one-off Navexa import (from its CSV files) and an ongoing Navexa sync (with your Navexa API key).
- End-of-day prices, exchange rates, dividends and splits from [EODHD](https://eodhd.com), downloaded each day by the
  price updater, for the ASX and US, UK, Canadian, NZ, Hong Kong, Singapore and Indian exchanges. Prices can also be
  uploaded as a CSV.
- Reading a receipt or bill from a PDF or photo into a draft bill, expense claim or trade.

### Accounting

- Chart of accounts, manual journals (including repeating and reversing ones), trial balance, profit and loss, balance
  sheet and cash movement.
- Realised gains posted automatically on each sale, and market value revaluation with deferred tax.
- Budgets, tracking categories (with a profit and loss by category), foreign currency accounts and foreign exchange
  revaluation.
- Locked periods, an audit trail (changes after a lock are marked), and attachments on trades, journals, clients,
  years and workpapers.
- Export to Xero (manual journal CSV) and MYOB AccountRight (general journal).
- Financial statements with comparatives for each entity type, and a printable report pack with declarations and an
  APES 315 compilation report.
- GST, VAT or sales tax codes, with a report by return box (presets for Australia, the UK, the US, Canada and India).

### Business books

- Customers, invoices (with or without tax, discounts, your logo and payment details, print or save as PDF), quotes,
  credit notes, repeating invoices, deposits matched to invoices, customer statements and aged receivables. Approved
  invoices post to the ledger.
- Suppliers, bills, repeating bills, purchase orders and aged payables.
- Inventory and stocktakes, expense claims, and a fixed asset register with depreciation.

### Tax (Australia)

- First in, first out capital gains with the 50% discount for assets held more than 12 months, or a choice of parcels
  on each sale (picked, smallest gain or largest gain).
- Fund tax statements (AMIT/AMMA), which replace the payouts' breakdown in the workpapers and labels and adjust the
  cost base of units held.
- Employee share schemes and shares held as trading stock.
- Income tax reconciliation, tax planning for individuals, workpapers and a year-end checklist.
- Trust distribution statement and resolution; SMSF member accounts, contribution caps, minimum pensions, TBAR events,
  ECPI, pension minute, investment strategy and audit pack; company tax, deferred tax check and franking account;
  a Division 7A calculator.
- ATO prefill comparison (paste or upload the prefill report), tax return labels for each entity type (a printable
  page and a CSV keying sheet for Xero Tax, HandiTax or LodgeiT), and a sheet to copy onto the ATO's paper return form.

### Rental properties

- A rental schedule for each property laid out like the ATO's, with a column for each owner, filling labels 21P, 21Q,
  21F and 21U and shown in the Taxable income report. Agent statements (rental payouts with the property's code), the
  maintenance log, the rent ledger, bills the tenant paid and linked loans fill it in, alongside figures keyed for the
  year.
- Depreciating assets (diminishing value, prime cost or the low-value pool), capital works, balancing adjustments,
  holiday homes and private use, and private redraws on a property loan.
- Income and expenses by month for any period, year on year with big changes flagged, a depreciation schedule a few
  years ahead, and a review checklist before the return goes in.
- The sale of a property: the five cost base elements less capital works claimed, the main residence exemption, the
  six-year rule and the market value rule, going into the capital gains report.
- Running the property: tenancies with the bond and lease dates, a rent ledger showing arrears, bills passed on to the
  tenant, a maintenance log that decides repair or capital, inspections with photos, safety checks and renewals by
  state, documents, and reminders by email. Each property's value, loans, equity and yields, with equity counted in
  net worth on the dashboard.

### For accountants

- Clients with entity type, ABN/ACN and officeholders, practice details, and a practice dashboard showing every
  client's year status, open queries, unallocated bank lines and workpapers to review.
- Client queries asked from a bank line or journal and answered by the client, and client onboarding.
- Sharing: give someone a role in an entity's books by email, as owner (view, change and share), editor (view and
  change, for the accountant or bookkeeper) or read only (for an auditor). People who haven't signed up yet are
  invited by email.

### API and AI assistants

An assistant in the site (Assistant in the menu): ask Claude about the portfolio in plain English, or ask it to record
something. It looks things up by itself, but every change is shown to you to approve or reject first. Each person uses
their own Anthropic API key, kept encrypted, or the host can provide one.

A JSON API (`/api/v1`) and an MCP server (`/mcp`), so Claude or another AI assistant (or your own app) can read the
portfolio, add trades and cash, import, match and allocate bank transactions, read invoices and run the CGT, income,
trial balance, profit and loss, balance sheet and rental schedule reports. Each login makes its own API keys (account menu > API keys),
which can be read only; a key reaches the same portfolios with the same role as its login, and every change made with
one is logged. Details are in [README-razor-pages.md](README-razor-pages.md#api-and-mcp-server).

### Accounts and security

Email checks for new accounts, password reset by email, two-factor sign-in with any authenticator app, limits on
password guessing, an optional "I'm human" check, and email alerts for price targets, announced dividends, failed
syncs and bank lines waiting to be allocated. The site works on a phone and can be added to the home screen.

## Running it yourself

[SETUP.md](SETUP.md) walks through it step by step, from installing the tools to running it on a Windows or Linux
server. The quickest way to try it is Docker:

```
docker compose up -d
```

then open http://localhost:8080 and register. Without Docker:

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and SQL Server (Express, Developer or
   the Docker image is fine).
2. Create an empty database and run `Database/CreateDatabase.sql` in it, then every script in `Database/Migrations`
   dated after 2026-10-16, in file name order.
3. Give the app its connection string, kept outside the repo:
   ```
   cd src/PortfolioManager.Web
   dotnet user-secrets set "ConnectionStrings:Default" "Server=.;Database=PortfolioManager;Trusted_Connection=true;TrustServerCertificate=true"
   ```
4. `dotnet run --project src/PortfolioManager.Web`, open the address it prints and register. The first account
   registered is the site administrator.
5. Follow [First steps](SETUP.md#7-first-steps) to set up your first entity, share it and raise an invoice.

A made-up demo portfolio (`Database/SampleData/demo-portfolio.sql`) is there for trying the reports.

### Settings

Everything is optional except the connection string. On a server, settings go in `appsettings.Production.json` next to
the published site (never in git); on your own computer, in user-secrets; with Docker, as environment variables.
[SETUP.md](SETUP.md#6-run-it-on-a-server) explains each one.

| Setting | What it turns on |
| --- | --- |
| `ConnectionStrings:Default` | The database. Required. |
| `Site` | Your own site name, logo, public address (`BaseUrl`, needed for links in emails), support email and demo login. |
| `Email` | Your SMTP server, for password resets, account checks, sharing invitations, emailed invoices and alerts. |
| `Captcha` | A Cloudflare Turnstile "I'm human" check on the login, sign-up and reset forms. |
| `DocumentReading:AnthropicApiKey` | Reading photos and scanned PDFs of receipts and bills (files are sent to Anthropic's API). |
| `DataProtection:KeysPath` | Where the keys protecting sign-ins, two-factor secrets and saved Navexa keys are kept. |
| `SiteAdminUserIds` | Which logins can upload prices for everyone (the first account by default). |
| `Prices:Eodhd` (price updater) | Your EODHD API key and exchange codes for the daily price download. |

### Updating

Pull the new code, run any new scripts in `Database/Migrations` (in file name order, before the new build goes live),
then publish and copy the site again as in [SETUP.md](SETUP.md#updating-to-a-new-version). With Docker,
`docker compose up -d --build` does all of it.

## Limitations

- **The tax rules are Australian.** Each portfolio can use another country, financial year and currency, and GST, VAT
  and sales tax work anywhere, but the CGT discount, franking, SMSF, trust, company and return pages are Australian
  and hide themselves elsewhere. Capital gains outside Australia are plain first in, first out, which is wrong where
  the law requires pooled or average cost (the UK and Canada, for example). London prices from the daily feed come in
  pence, so they show 100 times too high. What is missing for the UK, the US, Canada and India is listed in
  [ROADMAP.md](ROADMAP.md#using-it-outside-australia).
- **Whole units only.** Fractions of a unit (crypto, some managed funds) can't be recorded yet.
- **Foreign trades in the ledger** post the foreign amount without converting it, and payouts are entered in the
  portfolio's own currency.
- **Announced dividends** have no franking or DRP price in the feed: the franking is taken from the last payout, so
  check each one against the statement.
- **No lodgment.** Returns are prepared for keying into tax software or copying onto the paper form; the site doesn't
  lodge with the ATO. There is no payroll and no live bank feed (bank statements are imported as files).
- **English only.**
- **Daily prices need an EODHD plan** that covers your exchanges (the free plan allows 20 calls a day and may not
  include the ASX). Without one, upload prices as a CSV.

What's planned is in [ROADMAP.md](ROADMAP.md).

## Built with

ASP.NET Core 10 Razor Pages (C#), SQL Server with stored procedures (Dapper), xUnit. Runs on Windows (IIS), Linux
(nginx) or Docker.

| Folder | What |
| --- | --- |
| `src/PortfolioManager.Core` | Services and calculations, no web code |
| `src/PortfolioManager.Web` | The website (Razor Pages), the JSON API and the MCP server |
| `src/PortfolioManager.PriceUpdater` | Daily price download job |
| `tests/PortfolioManager.Tests` | Unit tests (`dotnet test PortfolioManager.slnx`) |
| `Database/CreateDatabase.sql` | Builds a new, empty database |
| `Database/Migrations` | SQL scripts applied in file name order |
| `Database/SampleData` | The demo portfolio |
| `samples` | Example import files |

[README-razor-pages.md](README-razor-pages.md) is the technical reference: how each feature works underneath, every
database migration, the API and MCP server, and the price updater.

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
