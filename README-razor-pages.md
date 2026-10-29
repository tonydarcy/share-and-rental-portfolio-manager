# Share Portfolio Manager: technical reference

How each feature works underneath (the pages, the stored procedures they call and the rules they follow), every
database migration, the API and MCP server, and the daily price updater. For what the site does, start with the
[README](README.md); to install it, follow [SETUP.md](SETUP.md). The site is ASP.NET Core Razor Pages (C#, .NET 10)
over a SQL Server database whose stored procedures post the double-entry journals.

| Folder | What it is |
| --- | --- |
| `src/PortfolioManager.Core` | Talks to the database (Dapper over the existing procs). No web code, so a mobile API can reuse it later. |
| `src/PortfolioManager.Web` | The Razor Pages website. |
| `tests/PortfolioManager.Tests` | Automated tests (`dotnet test PortfolioManager.slnx`). |

## Features in detail

- **Log in / log out.** Passwords are stored as PBKDF2 hashes (compatible with the older Web Forms site's accounts),
  and attempts are still recorded with `uspRecordLoginAttempt`.
- **Portfolio picker** in the navbar, using `uspGetAccessiblePortfolios`. The choice is checked
  against that list on every request.
- **Sharing** (account menu, or Share on Manage portfolios): give another login a role in the selected portfolio by their
  email: Owner (`Admin`: view, change and share), Editor (`Edit`: view and change, not share) or Read only (`View`),
  change it, or take it away (`uspGetPortfolioShares`, `uspSharePortfolio`, `uspRemovePortfolioShare`). Only the
  portfolio's own login and owners may share (`fnCanSharePortfolio`). An email nobody has signed up with is saved as an
  invitation (`tblPortfolioInvites`) and becomes access when they register and log in (`uspAcceptPortfolioInvites`, called
  from the login page). With Read only access every page opens but any save is turned away (`ViewOnlyFilter`; pages that
  don't change the portfolio, such as logging in or switching portfolio, are marked `[AllowViewOnly]`).
- **Invoices** (`/Invoices`): customers, invoices with lines, sales tax codes and prices with or without tax, a printable
  invoice (print or save as PDF from the browser), payments and voiding. Settings hold the number prefix, next number,
  days to pay and payment details. Drafts post nothing. Approving posts a journal (source `Invoice`): Dr 1200 Accounts
  Receivable, Cr each line's income account (default 4800 Sales and Fee Income), with tax split to the tax control
  account by `uspAddJournalLineWithTax`, so it appears on the sales tax report by invoice date. Saving an approved invoice
  reposts its journal. A payment (source `InvoicePayment`) posts Dr bank, Cr Accounts Receivable. Voiding removes the
  journal and keeps the invoice, marked Void; it needs any payments deleted first. All writes go through the invoice procs.
- **API keys and AI assistants** (account menu, `/Account/ApiKeys`): each login makes, names and revokes its own API keys,
  optionally read only, and sees a log of every change made with them (and by anyone's keys in portfolios it owns).
  The keys drive the JSON API and the MCP server described under "API and MCP server" below.
- **Dashboard:** total, share and cash value (and property equity), then cards that each load on their own so a slow one
  doesn't hold up the page: the latest day's move, gain and time-weighted return this financial year, unrealised gain and
  income with franking; a value chart with a period picker (1M to All), money put in, and a Return view against an ASX 200
  index or fund; a "Needs attention" list (bank lines to code, prices over 5 days old, holdings with no cost, the next BAS
  date, Navexa sync failures, rent arrears and property checks, overdue invoices and bills); top 10 holdings and where the
  money is (by type or sector); income received by month (franked, unfranked, other) with the next 12 months projected and
  announced dividends coming up; the tax year so far (Australia only: net capital gain, franking, net rent, an estimate,
  SMSF members' caps and minimum pension, and a tax-loss hint in the last 60 days of the year); rental and business cards
  when there are properties, invoices or bills; recent activity; quick action buttons; and a client list for logins that
  can open several portfolios. No migration.
- **Import from Navexa** (`/Import/Navexa`): a one-off import of a Navexa trades CSV and income CSV, with a preview,
  per-row errors and a duplicate check, saved through `uspAddShareTrade` / `AddShareTradeWithCashTransaction` and `uspAddPayout`.
  Navexa's `Crypto` exchange becomes `CRYPTO` with codes like `BTC-AUD`, and staking, airdrop and mining rows in the trades
  file come in as crypto rewards (crypto rows in the income file are left out: they carry no units).
- **Navexa sync** (`/Import/NavexaSync`): connects to Navexa with the user's own API key (Navexa, Settings, API keys;
  stored encrypted with ASP.NET Data Protection), with the Navexa portfolio picked from a list or typed in by its Navexa ID
  (changeable later, keeping the key), and copies trades, dividends, DRPs, splits, returns of capital and cash
  across, once or daily/weekly. Check Navexa shows what would come across without adding anything; Sync now adds what's
  ready. Each Navexa record is remembered, so a re-run never adds it twice, and records that look like ones already
  entered by hand are asked about first. Where the ledger needs something Navexa doesn't give, the page asks: a franking
  credit (suggested at 30%), DRP units and price, a split ratio, the code for a holding on an exchange it doesn't know, which bank account a
  Navexa cash account is, and which ledger account interest and fees post to. Everything goes through the usual procs.
  Crypto comes across too: buys and sells on `CRYPTO` (BTC as `BTC-AUD`), a deposit with a cost as a Buy without cash,
  swaps (Navexa type 7, or a trade with coins on both sides) as a crypto swap, and staking income (and staking, airdrop and
  mining trades with no income record) as crypto rewards. Withdrawals, transfers, lost and stolen coins are listed but
  not added. Coins kept to more than 8 places are rounded, and a dust sale whose fee is more than its proceeds has the fee
  cut to the proceeds.
  Navexa custom holdings (unlisted funds, property, bullion) come across without asking, on a manually priced market:
  `FUND` when the name says fund or trust, `PROPERTY` when it says property, otherwise `OTHER`. The code is a code-like
  word after the colon ("Brickx Managed Fund: BXM01 - ..." is `BXM01`) or the first word and the initials of the rest
  ("ABC Bullion: Gold (Gold Saver)" is `GOLD-GS`), with `-2` added if two holdings would share one. Enter their prices on
  Prices > Manual prices. A code saved on the page overrides it; one saved on the old `CUSTOM` suggestion is ignored.
  Scheduled syncs run inside the web app every 30 minutes while it's awake; for them to run on time set the app pool's
  Start Mode to AlwaysRunning and Idle Time-out to 0 (otherwise a due sync runs soon after the next visit).
- **Payouts** (Income menu): list by date range or financial year, add, edit and delete dividends, distributions, interest
  and rental payouts with `uspGetPayouts`, `uspAddPayout`, `uspAddRentalPayout`, `uspUpdatePayout`, `uspUpdateRentalPayout`
  and `uspDeletePayout`. Editing a payout re-posts its ledger journal; deleting it removes the journal.
- **Corporate actions** (Transactions menu): record or undo a share split or consolidation (`uspApplyStockSplit`,
  `uspDeleteStockSplit`; the earlier trades are changed to the new units with the same cost base and purchase dates), and record
  or delete a return of capital (`uspAddReturnOfCapital`, `uspDeleteReturnOfCapital`), which reduces the cost base of the parcels held,
  with any excess shown as a capital gain (CGT event G1) on the tax reports. Each shows a preview before anything is saved.
  **Demergers, takeovers and code changes** (`Core/CorporateActions/ParcelTransfers.cs`, Corporate actions > Demerger,
  takeover or code change): a demerger adds the new company's shares with the entered percentage of each parcel's cost base,
  and a takeover sells the old shares for the cash plus the new shares' value and buys the new ones. With scrip-for-scrip
  rollover only the cash is taxed now. In both, the CGT figures (`CgtParcels`) give the new shares the old parcels' purchase
  dates and cost base share. A code change renames everything recorded under the old code. Worthless or delisted shares
  are a sale of every unit for nil. Bonus issues are recorded as a split, and rights issues and share purchase plans as purchases.
  **Options and warrants exercised** (same page, `uspExerciseOptions`): the options are sold for their cost base and the shares
  bought for the exercise price plus that cost base, acquired on the exercise date; the CGT figures make no event of the
  options' sale. Lapsed options are recorded as worthless (a loss of their cost).
- **Loans, term deposits and bonds** (Holdings menu, `Core/Debt/`): margin and investment loans with their drawdowns,
  repayments, interest and fees, giving what's owed, the loan-to-value ratio against the shares' value and the year's
  deductible interest; and term deposits, bonds and hybrids with the interest earned in a financial year and accrued at its
  end (simple interest, actual days over 365, coupons counted back from maturity). Nothing is posted to the ledger.
- **Shares as trading stock** (Tax work > Shares as trading stock, `Core/Tax/TradingStock.cs`): for a share trader. From the
  chosen year the portfolio's shares are trading stock valued at cost, market value or the lower of the two for each holding;
  the year's profit (sales plus closing stock, less opening stock and purchases) replaces the capital gains in the tax
  reconciliation and return labels (P8 for an individual). Stock is at what it cost: returns of capital and fund statements
  don't lower it, but demergers, takeovers and option exercises still move cost between holdings.
- **Employee share schemes** (Income > Employee share schemes, `Core/Tax/EssStatements.cs`): each employer's ESS statement
  for a year. The discounts go on an individual's return labels at item 12 (12D, 12E, 12F, 12G, 12C, 12A); other entities get
  a warning instead. The shares themselves are purchases at their market value on the taxing point.
  **Add a DRP** (Income menu) records a reinvested dividend and its new units together with `uspAddDRPTransaction`;
  deleting the payout deletes the units too.
- **Diversity report** (Reports menu) with a sector chart, from `GetDiversityReport`.
- **Performance** (Reports menu): value history (cash plus shares each day), time-weighted return for a financial year or
  custom dates (annualised beyond a year), monthly returns, money added and taken out, income, and a comparison with an
  index (`tblMarketIndexHistory`) or any share or ETF with prices, such as ASX:STW. Worked out in `PerformanceCalculator`
  rather than the legacy `uspCalculateTWR` / `uspBackfillHistory`, which counted a trade's settlement as money added.
  Splits are allowed for (older prices are divided by the ratio) and foreign holdings are converted with the FOREX rates.
- **Share quote** and **Share trends** (Reports menu): a stock's latest close, change, year range and trend (Bullish,
  Neutral or Bearish against its 200-day average and 52-week standard deviation) with a price chart; and the latest
  trend for every holding, the legacy "Selected Popular ASX Companies" list, or everything with prices.
- **Groups** (Holdings menu): create, rename and delete custom groups and tick the trades in each
  (`uspCreateCustomGroup`, `uspRenameCustomGroup`, `uspDeleteCustomGroup`, `uspAddTradeToGroup`, `uspRemoveTradeFromGroup`).
  The Holdings page filters by group.
- **Manage portfolios** (account menu): list them and create a new one with `AddNewPortfolio`.
- **CSV imports** (Import menu): share trades for the selected portfolio, and market prices (site admins only,
  `SiteAdminUserIds` in appsettings), each with a preview before anything is saved. Share trades can also come
  straight from a broker's export (`Core/Imports/BrokerCsv.cs`): CommSec account transactions, CMC Invest, SelfWealth,
  Stake, Pearler, Superhero, an Interactive Brokers activity statement, or any CSV whose header names the date, code and
  quantity columns. The format is worked out from the file or picked from a list; lines that aren't buys or sells are
  left out, brokerage is worked out from the total when there's no brokerage column, and part units are refused. A new
  broker with an unusual layout is one more parser in that file (**good first issue**).
- **Choice of parcels on sale** (Share trades, the stack button on a sale): first in, first out, parcels picked by hand,
  smallest gain or largest gain (losses, then discounted gains, then other gains, using the entity's discount rate).
  Smallest and largest are worked out into picks when saved, so `CgtParcels` (tax figures, parcels sold, open parcels)
  and `SaleCosts` (the realised gain journal) read the same picks. A pick that no longer fits after a trade is edited is
  topped up first in, first out and flagged. The legacy SQL reports (the capital gains Sales table and unrealised gains)
  stay first in, first out.
- **What-if sale** (Reports, `/Reports/WhatIf`): pick a holding, units, price (the last close on file by default),
  brokerage, date and parcel method, and see the parcels it would take, the gain, the discount and the year's net
  capital gain with and without it (after this year's other sales and losses carried forward), times a tax rate you can
  change (32%, 30% for a company, 15% for a super fund to start). Nothing is saved (`Core/Tax/WhatIfSale.cs`).
- **Income calendar** (Reports, `/Reports/IncomeCalendar`): dividends announced for the portfolio's holdings and not
  yet paid (from the announcements the price updater saves, estimated on the units held at the ex-date), and the income
  received in a financial year by holding and month, with the franking credits.
- **Foreign shares** (`Core/Prices/FxRates.cs`): a foreign-currency trade added with the exchange rate blank (or 1),
  on the Add trade page or in a trade import, takes the FX close on file for its date (the `FOREX` pairs the price
  updater saves, up to a week back), so its cost base is converted at its own date. Share trades > Foreign currency
  trades lists every trade in another currency against the FX close on its date and flags rates more than 5% out.
  The ledger journal of a foreign trade still posts the order total unconverted (see "Foreign shares in the ledger" in ROADMAP.md).
- **More asset types** (`Core/Prices/ManualPrices.cs`): managed funds, unlisted shares, property and other assets are
  trades on the markets `FUND`, `UNLISTED`, `PROPERTY` and `OTHER`, priced on Prices > Manual prices (one at a time or
  a CSV of Code, Date, Price) for that portfolio only; the price updater skips them. Crypto is market `CRYPTO` with codes
  such as `BTC-AUD`, priced daily from EODHD's `CC` feed.
- **Crypto** (`Core/Crypto/`): Transactions > Record a crypto swap posts a Sell of one coin and a Buy of the other at the
  swap's value through `uspAddShareTrade` in one transaction (both against Trade Clearing, which nets to nil). Income >
  Add a crypto reward posts a staking, airdrop, mining or other reward through `uspAddDRPTransaction` with the value as
  the payout's other amount (Other Investment Income 4900; other income on the return labels 24Y, 6S and 11S) and the
  coins bought at that value. Import > Crypto exchange files reads Coinbase, Swyftx, CoinSpot and Independent Reserve
  exports (and any CSV with a header row) by column names into buys, sells, swaps and rewards, with a preview. Payouts,
  DRPs, the dividend details file and both Navexa imports refuse franking on `CRYPTO`.
- **Contribution** (Reports > Contribution, `Core/Performance/Contribution.cs`): for each holding over a period, the
  value at the start and end, bought less sold, and the gain split into price, income (including reinvested) and, for
  foreign shares, currency (price change in the share's currency at the end rate; currency is the rate's move on the
  start value and each trade). Returns are over the average invested value (Modified Dietz), so they differ a little
  from the time-weighted return on Performance. Cash isn't included.
- **Tax-loss selling** (Reports > Tax-loss selling, `Core/Tax/TaxLossHarvest.cs`): for each holding with parcels below
  cost at the latest price, a what-if sale of just those parcels (smallest gain first) against this year's gains: the
  loss, the change in net capital gain, the tax saved at the rate given, and any loss carried forward. Holdings bought
  in the last 30 days are flagged, with a note on wash sales (Part IVA, TR 2008/1). Foreign prices are converted at the
  FX close, here and in the what-if sale's starting price.
- **Consolidated view** (Reports > Consolidated, `Core/Reports/Consolidated.cs`): a login groups portfolios it can open
  (for example the individual, the family trust, the company and the SMSF). The page shows each entity's share value,
  cash, total, share of the group, income and net capital gain for the period, then each holding across the group by
  entity. With mixed currencies, amounts stay in each portfolio's currency and aren't totalled. A portfolio the login
  loses access to drops out of its groups.
- **Fund look-through** (Reports > Diversity, Prices > Fund look-through, `Core/Reports/LookThrough.cs`): each fund's
  value is spread over its sectors, countries (or regions) and largest holdings, and companies held directly count by
  their sector and listing country, so a company held both ways is one line. Shared figures come from the price updater
  (`Prices:LookThrough:Enabled`, off by default: EODHD fundamentals, 10 API calls per stock, each stock at most every 30
  days; stocks that aren't funds are noted so they aren't asked again). A portfolio can enter its own figures for a fund
  it holds, which it then uses instead.
- **Fund tax statements** (Income > Fund tax statements, `Core/Tax/FundTaxStatements.cs`): one row per fund per year
  from its AMMA or annual distribution statement. For that fund and year: the tax workpaper income uses the statement
  instead of its payouts (`TaxIncomeFacts.WithStatements`; franked distributions and their credits go to 13C/13Q for
  individuals, other Australian income and NCMI to 13U, foreign income to 20M), and the reconciliation adds the attributed
  income less the distributions in the accounts. Its capital gains become CGT events on the year end (discount method gains
  doubled, so the holder's own discount applies) and its net AMIT decrease (or increase) changes the cost base of units held
  then, through `CorporateActionsService.GetTaxCostBaseLotsAsync` (the CGT pages, parcels, what-if and tax-loss selling).
  The ledger and the realised gain journals don't change. The list shows whether each statement ties to the cash paid.
- **On a phone**: the site has a web app manifest (`/manifest.webmanifest`, named from `Site:Name`) and a service worker
  (`wwwroot/sw.js`), so it can be added to the home screen. Pages always come from the network; only the "you're
  offline" page is stored on the phone. `wwwroot/js/mobile.js` makes every table scroll sideways inside its card, and on
  narrow screens the first column stays in view. Styles, scripts and icons are served without sign-in.
- **Email alerts** (account menu > Email alerts, `Core/Alerts/AlertService.cs`, `Web/Services/AlertScheduler.cs`): each
  login chooses, per portfolio, emails for price targets reached (checked against the day's close, sent once), dividends
  announced for a stock held, a failed scheduled Navexa sync, and bank lines waiting to be allocated (at most once a
  day). The site checks every 30 minutes and sends one email with everything new. It only runs when the Email settings
  and `Site:BaseUrl` are set. Someone with view access can have alerts too; they stop if their access is removed.
- **Accounting** menu: the chart of accounts (add, edit, deactivate and delete the portfolio's own accounts; system
  accounts are read only), manual journal entry and the journal history (only manual journals can be edited or
  deleted), QIF/OFX bank statement import with a preview and import history, allocating imported lines to an
  account, a payout or a trade, and the trial balance, profit & loss, balance sheet and cash movement reports.
  It uses `uspGetChartOfAccounts`, `uspAddChartOfAccount`, `uspUpdateChartOfAccount`, `uspSetChartOfAccountActive`,
  `uspDeleteChartOfAccount`, `uspGetJournalList`, `uspGetJournalDetail`, `uspAddJournalHeader`, `uspAddJournalLine`,
  `uspUpdateJournalHeader`, `uspClearJournalLines`, `uspDeleteJournal`, `uspAddBankImportJournal`,
  `uspGetImportHistory`, `uspDeleteImportBatch`, `uspGetUnallocatedTransactions`, `uspAllocateSuspenseTransaction`,
  `uspGetTrialBalance`, `uspGetProfitLossReport`, `uspGetBalanceSheet`, `uspGetDetailedCashFlowReport` and
  `uspGetCashMovementReport`.
- **Clients** (navbar): every portfolio you can open is a client, listed with its entity type and ABN. **Client details** (for the
  selected portfolio) holds what financial statements print: legal name, entity type (individual, partnership, company, trust or
  SMSF), ABN and ACN (check digits are verified), trustee, directors / trustees / partners / members, address and the reporting
  basis wording. **Practice details** (account menu) holds your practice name, preparer name and tax agent number; they belong to
  your login, not to a client. Uses `uspGetClientList`, `uspGetClientDetails`, `uspSaveClientDetails`,
  `uspDeleteClientOfficeholders`, `uspAddClientOfficeholder`, `uspGetPracticeDetails` and `uspSavePracticeDetails`.
- **Financial statements** (Accounting menu, or Statements on the Clients page): for the selected client and a financial year, with the
  year before as comparatives: a detailed profit and loss statement (an operating statement for an SMSF), a statement of financial
  position with an equity section for the entity type (issued capital and retained earnings; settled sum and undistributed income;
  liability for accrued benefits by member; partners' capital and current accounts; owner's capital, retained profits and drawings),
  and notes (accounting policies for a special purpose framework, and each line broken down by account). It reads the trial
  balance (`uspGetTrialBalance`) at 30 June of the year and the two years before; a year's profit is the change in the revenue and
  expense accounts, rolled into equity at year end without posting closing journals. Accounts are mapped to lines by type, number
  range and name (the page lists the rules); anything it can't place is shown on an "Other" line. It warns when assets less
  liabilities don't equal equity, and when clearing or suspense accounts have a balance.
- **Realised gains on sale** post themselves: before any ledger report or journal is shown, each sale's journal is brought up to
  date so it credits the share's cost base account with the FIFO cost of the units sold (returns of capital included) and the
  difference to 4500 Realised Gains on Investments (`RealisedGainService`, `uspGetSaleJournals`, `uspPostSaleGain`). A backdated
  buy, deleted trade, split or return of capital is picked up the next time, and only journals that are out of date are rewritten.
  A sale of more units than were held is left at proceeds. Remove any manual journal that moved realised gains, or they count twice.
- **Report pack** (Accounting menu, or the button on Financial statements): a printable pack for the selected client: cover page
  (legal name, trustee, ABN and ACN), contents, the two statements, the notes, the declaration for the entity type (directors',
  trustee's, SMSF trustees', partners' or the individual's, signed by the officeholders in the client details) and a compilation
  report in the APES 315 form signed off with your practice details. Choose the year, whether to include the compilation report and
  an optional signing date. Use the browser's Print / Save as PDF: each part starts on a new A4 page and the navbar and settings
  are left out.
- **Tax menu** (Australian rules, per financial year; nothing posts to the ledger):
  - *Income tax reconciliation*: profit before tax from the ledger, less the accounting gains on investments, plus the FIFO net
    capital gain, plus or minus the year's adjustments (non-deductible expenses, non-assessable income and so on), less tax losses
    brought forward. Companies and SMSFs also get tax payable after foreign, franking (refundable for a fund, not a company), TFN and
    PAYG credits. Settings per year: losses brought forward, capital losses (blank = worked out), company rate, ECPI %, instalments.
  - *Trust distribution*: the year's beneficiaries and shares, net income split by character (other, franked, franking credits,
    grossed-up capital gains, TFN and foreign credits) with an individual beneficiary's labels, and a resolution to print.
  - *SMSF members* and *SMSF audit pack*: member accounts by phase, earnings shared by weighted balance, contributions tax on the
    receiving account, cap checks (concessional, non-concessional bring-forward by total super balance) and minimum pensions; the pack
    prints the member statements, investments at 30 June prices, bank reconciliations, tax and a checklist.
  - *Company tax and franking*: tax payable, the deferred tax liability checked against the market value adjustment, and a franking
    account (credits from payouts and tax paid, debits for franked dividends and refunds). *Division 7A calculator*: minimum
    yearly repayments at the benchmark rates.
  - *Tax return labels*: the figures label by label for the entity's return, printable or as a CSV keying sheet. Xero Tax, HandiTax
    and LodgeiT have no import for return labels, so the CSV is the export for now; a package format can be added as another
    `ITaxReturnExport`.
  - *Paper tax return* (Tax > Paper tax return): the same figures laid out by form part (an individual's main return,
    supplementary section and business schedule), with the official form to order (name and NAT number), how to fill it
    in, the GPO Box 9845 address for the client's capital city and a before-posting checklist. The ATO doesn't accept
    printed copies of its income tax return forms (they are scanned), so the forms are ordered free from iorder.com.au
    and the figures copied on by hand.
- **GST, VAT and sales tax** (Tax > GST codes / GST report; the menu uses the country's name for the tax): any country.
  *Tax codes* sets what the tax is called, how often it's reported and its control account, and holds the portfolio's tax
  codes (sales or purchases, rate, and the return boxes the amount and the tax go in). One click adds the usual codes for
  the portfolio's country: Australia (BAS G1 to G14, 1A, 1B), New Zealand, UK, Ireland, South Africa, Singapore, Canada,
  India and the US, or a plain set to fill in elsewhere. A tax code can go on a bank allocation, an allocation rule or a
  manual journal line: the amount is entered with tax, the account gets it less tax and the tax goes to the control
  account. The *report* adds up a period (month, quarter, year, or any dates) into the return's boxes, by tax code and
  transaction by transaction, to print or download as CSV.
- **Practice tools** (Clients menu): a *practice dashboard* across every client you can see (year status, open queries,
  unallocated bank lines, workpapers still to review), *client queries* (ask about a bank line or journal, the client answers on
  the site, optional email), *onboarding* (send a new client a link to an engagement letter and a details form; accepting it sets
  up their portfolio, client details and starter accounts) and *attachments* on trades, journals, clients, years and workpapers
  (PDF, images, Office files and CSV, up to 10 MB each).
- **Year end** (Tax > Workpapers and year-end checklist): the year's status (not started to signed; locked follows the lock
  date), a checklist for the entity type with links to each page, and a workpaper per balance sheet account with the supporting
  figure, preparer and reviewer sign-off. *Audit trail* (Accounting menu) lists every add, change and delete to journals,
  trades, payouts, corporate actions, bank lines and invoices with who made it, and marks changes made to a period after it had
  been locked.
- **Repeating and reversing journals**: a journal can repeat (weekly to yearly, with an end date) and reverse the next day;
  Accounting > Repeating journals posts what's due. Any journal can be reversed on a chosen date.
- **Invoicing extras**: credit notes (adjustment notes when GST applies) applied to invoices, repeating invoices, a bank
  deposit matched to an invoice from the unallocated page, customer statements, aged receivables and a logo on invoices.
- **Export to Xero or MYOB** (Accounting menu): every journal, a monthly summary or opening balances as a Xero manual journal CSV
  or a MYOB AccountRight general journal import file, with each account's code (and tax rate) in that system.
- **Rental properties** (Property menu): *yearly figures and assets* (income and expenses by ATO category, Division 40
  decline in value by diminishing value, prime cost or the low-value pool, Division 43 capital works, balancing adjustments
  on disposal, days available and used privately); the *rental schedule* laid out like the ATO's with a column per owner,
  also in the Taxable income report; *income and expenses by month* for any period; *year on year* with changes of $1,000
  and 25% flagged; a *depreciation schedule* five years ahead; a *review checklist* (interest with no rent, repairs over
  $5,000, missing agent statement months, weeks not entered, travel, private use, shares nobody owns). Rental payouts with
  the property's agent statement code, repairs in the maintenance log, the rent ledger (for properties without a code),
  tenant bills once paid (netted off the expense) and the interest on linked loans (replacing the interest keyed) fill the
  schedule. *Properties* shows value, linked loans, equity, yields, rent arrears and reminders; each *property's page*
  keeps details and co-owners, tenancies with the rent ledger, bills to the tenant, the maintenance log (a capital job can
  be added as capital works), inspections with photos, safety checks and renewals (with each state's usual checks),
  documents, linked loans and the *sale* (cost base less capital works claimed, main residence days, the six-year rule and
  the market value rule), which goes into the capital gains report. Property reminders are emailed when property alerts
  are on (Email alerts). Equity is counted in net worth on the dashboard.
- **Assistant** (menu): a chat with Claude about the portfolio being looked at, using the same tools as the API. Reads run
  straight away; every change waits for the person to approve or reject it, then runs through the same checks and change
  log as an API key (shown as "Site assistant"). Each login saves its own Anthropic API key (encrypted with the site's
  data protection keys) and picks the model; the host can set `Assistant:AnthropicApiKey` for everyone without one. One
  conversation is kept per login and portfolio.
- **More tax work**: *SMSF extras* (TBAR events with report-by dates and each
  member's transfer balance, the actuary's ECPI details, a pension minute and the investment strategy, plus Section F member
  labels); *ATO prefill* (paste or upload the prefill report and compare it with the payouts and statements; lodging itself needs
  ATO digital service provider accreditation, so returns are lodged from a tax package); *tax planning* for individuals (the year
  so far plus salary, then a what-if sale, a deductible super contribution and extra deductions side by side).
- **Business books** (Business menu): *bills* from suppliers with payments, supplier credit notes, repeating bills and aged
  payables; *quotes* that become invoices and *purchase orders* that become bills; *inventory* with stock on hand, average
  cost, cost of goods sold posted on each sale and stocktakes; *expense claims* with the receipt, approval and paying people
  back; *fixed assets* with depreciation runs (diminishing value, prime cost, low-value and small business pools, instant
  write-off, sharing the rental schedule's engine) and the gain or loss on sale; *receipt reading* that turns an uploaded PDF
  or photo into a draft bill, claim or trade (photos and scans need a Claude API key; PDFs with text are read without one).
  Accounting gets *tracking categories* on invoice, bill, claim and journal lines with a profit and loss by option,
  *budgets* by account and month with budget against actual, and *foreign currency* bank accounts, invoices and bills with
  realised gains and losses on payment and a revaluation at a date's rates.
- **October 2026 extras** (migrations 2026-10-26-01 to -07):
  - *Part units*: trades, DRPs, demergers, takeovers and option exercises take up to 8 decimal places (`FormRules.UnitsProblem`),
    and journal descriptions show them without trailing zeros (`fnUnitsText`).
  - *Payouts in another currency* (Payouts > Add): the currency and rate (that day's rate when left blank) convert the
    amounts to the portfolio's currency before `uspAddPayout`; what was entered is kept and shown on the payout.
  - *Dividend details* (Import > Dividend details, site admin only): a CSV of franked % and DRP price by stock and ex-date,
    saved on the announced dividends (`uspSaveAnnouncedDividendDetails`). The announced franking then wins over the last
    payout's, and Add as DRP works out the units.
  - *Parcel choice in the older reports*: the capital gains Sales table is built from the same parcel matching as the CGT
    report (`CapitalGainsService.FromEvents`), and unrealised gains from the open parcels (`UnrealisedGainsService.Build`).
  - *Phone layouts and offline copies*: tables marked `table-stack` (unallocated bank lines, allocation matches,
    reconciliation, journals and the journal entry lines) show each row as a card on a phone (`mobile.js` labels the
    cells). "Offline copies on this device" in the account menu lets `sw.js` keep the main read-only pages as they're
    opened, shown with an "offline copy" banner when there's no connection, and deleted when it's switched off or on
    signing out.
  - *Consolidated view*: the group's time-weighted return (daily values, money in and out and income added across the
    entities, `GroupReturns.Combine`) and each entity's, a monthly strip, and `?pack=true` for a printable adviser pack
    with each entity's holdings.
  - *Fund look-through files* (Prices > Fund look-through): reads an issuer's holdings CSV (`FundHoldingsFile`), finding
    the header row by its column names, into sectors, countries and the 25 largest holdings, to check and save.
  - *Fund tax statement files* (Payouts > Fund tax statements): a statement CSV (`FundStatementCsv`, columns found by
    name) previewed and saved all or nothing, replacing statements for the same fund and year. Saving or deleting a
    statement (re)posts its cost base journal (`uspPostFundTaxStatementJournal`, source `FundStatement`).
  - *Corporate actions*: a takeover can take only some shares (the oldest parcels go first), and a demerger can be paid
    partly as a dividend (posted with `uspAddPayout` to the new shares' cost base account, franking at 30%).
  - *Employee share schemes*: tax planning adds the ESS discounts and the $1,000 reduction when the income test
    (taxable income, reportable fringe benefits, reportable super and net investment losses) is $180,000 or less
    (`EssReduction`). Tax > ESS annual report keeps an employer's grants (`tblEssGrants`), prints each employee's
    statement and gives the year's figures as a CSV; no TFNs are kept.
  - *Trading stock*: holdings can be kept as investments (`tblShareTraderHoldings`; their gains stay capital gains),
    and "Post the journal" moves the gains on stock sold from 4500 to 4550 Trading Profit on Shares and records the
    change in closing stock over cost in 1395 (`uspPostTradingStockJournal`, source `TradingStock`).
  - *Loans and fixed income*: each loan entry posts a journal (`uspPostLoanEntryJournal`, source `LoanEntry`; the loan
    has its own liability account under 2300, interest and fees go to 5300). Debt > Fixed income posts the year-end
    accruals and their reversal (`uspPostInterestAccruals`, source `InterestAccrual`). The tax reconciliation takes out
    what's in 5300 and puts in the deductible part of the investment loans, takes accrued interest back out, and D8
    shows the deductible loan interest.
- **Accountant, BAS and rental extras** (migrations 2026-10-26-08 to -11):
  - *Year ends by country* (Clients > Practice dashboard): each client's year end comes from its own country and year
    start (`PracticeClientYear.YearEnd`), clients are grouped by country with Australia first (`ByCountry`), and the
    year filter reads "years ending in".
  - *Attachments printed into the packs*: a year's attachments are put into one PDF (`AttachmentPack.Build`, PdfPig):
    a contents page, then PDFs merged, JPEG and PNG photos a page each, and text and CSV files as text pages. Word,
    Excel, email and other files are listed with why. The SMSF audit pack and report pack print photos in place, and
    "Supporting documents as one PDF" downloads the lot (Attachments > `?handler=Pack`).
  - *Credit note refunds* (Invoices > a credit note > Refund to the customer): `uspRefundCreditNote` posts Dr Accounts
    Receivable, Cr the bank, and the customer statement shows it as a Refund.
  - *Split deposits* (Accounting > Allocate, a deposit > Split over several invoices): oldest due first is suggested
    (`DepositSplit.Suggest`); `uspMatchInvoiceDepositSplit` posts one journal for all of them, and deleting any one of the
    payments deletes them all and puts the deposit back in Unallocated.
  - *Send to Xero and MYOB* (Accounting > Export > Send straight to ...): OAuth sign-in (`AccountingApiClient`, callback
    `/Accounting/Connected` with a signed, 20-minute state naming the portfolio), tokens encrypted with Data Protection
    (`AccountingTokenProtector`) in `tblAccountingConnections`, refreshed when they expire. Xero gets manual journals as
    drafts, 50 a request, with tax types matched by name or code from its TaxRates; MYOB gets one general journal per
    journal, with accounts and tax codes matched by their display ids. Each send is logged (`tblAccountingSends`) and a
    second send overlapping earlier dates needs "Send again anyway". Turned on by `AccountingLink` settings (SETUP.md).
  - *SMSF extras* (Tax work > SMSF): a retirement phase account can have a pension start date and balance;
    `MinimumPension.ProRata` pro-rates the first year's minimum by days (none for a June start). "Actuary data" downloads
    each member account's balances and flows as a CSV (`SmsfActuaryFile`) with an estimated retirement share.
  - *Tax planning* (Tax work > Tax planning): the Medicare levy low-income reduction (`MedicareLowIncome`, singles and
    seniors), and plans for a company or SMSF (`EntityPlanner`) and for a trust's beneficiaries (`TrustPlanner`).
  - *Activity statement* (Sales tax, Australia): cash or accruals basis (`uspGetSalesTaxCashLines` scales each paid
    invoice's or bill's taxed lines by the part paid), PAYG instalments (amount, or T1 x T2 with T1 suggested from the
    profit and loss revenue less gains and franking), and the other labels kept per period (`tblBasLabels`, locked
    periods can't change). `ActivityStatementBuilder.Build` works out W5, 5A, 8A, 8B and 9 in whole dollars.
  - *Rental extras*: the low-value pool is worked out for the pool as a whole (`RentalLowValuePool`), with termination
    values off the balance and any excess as income; a capital job can be split into depreciating assets and capital
    works (`JobSplit`); and receipts and arrears reminders can be emailed to the tenant (`TenantEmail`).
- **Create an account** from the login page (`AddNewUser`, password stored as a PBKDF2 hash).
- An optional link on the login page to an older site (`Site:OldSiteUrl`), for people moving across.

## Running it on your PC

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or Visual Studio 2026).
2. Give it the connection string. It is stored outside the repo with user-secrets:
   ```
   cd src/PortfolioManager.Web
   dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=PortfolioManager;User Id=...;Password=...;TrustServerCertificate=true"
   ```
3. `dotnet run --project src/PortfolioManager.Web` and open the URL it prints.

Build and test with `dotnet build PortfolioManager.slnx` and `dotnet test PortfolioManager.slnx`.

## Database migration

Some features need procedure changes in the database. Run the scripts in `Database/Migrations/` in SQL Server
Management Studio **before** you copy a new build to the server. Each one is safe to run twice, and the current
site keeps working after it.

The scripts don't name a database, so pick yours in the SSMS database drop-down (not `master`) before you press
Execute. A database built with `Database/CreateDatabase.sql` (see [SETUP.md](SETUP.md)) already includes every script
below, up to and including 2026-10-16; it only needs scripts dated after that, run in file name order (scripts from the same day are numbered so that sorting by name gives the right order).

- `2026-10-15-ledger-fixes.sql`: balanced payout journals (franking credits, interest, foreign tax and rental lines),
  editing payouts and rental payouts with their journals, and deleting a double-entry trade with its journals and
  settlement cash row. It also adds any missing ledger accounts it posts to (1400, 1410, 1420, 4100, 4200, 4300,
  4400, 4900, 5400) and lists them at the end. If one of those numbers is already used for a different type of
  account, it stops with a message and changes nothing.
- `2026-10-15-corporate-actions.sql` (run after the ledger fixes): adds `tblReturnsOfCapital` and `tblCorporateActionTrades`
  (the trades each split changed, so it can be undone); `uspApplyStockSplit` no longer rounds each trade's units (that lost cost base
  on a consolidation); new `uspDeleteStockSplit`, `uspAddReturnOfCapital` and `uspDeleteReturnOfCapital`; `GetProfitSummaryv2` takes
  returns of capital off a holding's cost; `uspAddDRPTransaction` posts balanced journals (the dividend into the bank and back out to
  the stock's cost base account); `uspDeletePayout` deletes a DRP payout's reinvested units with it. A split recorded before this
  migration can't be undone from the site.
- `2026-10-15-check-unbalanced-journals.sql`: read only. Lists every journal whose debits and credits differ,
  with the portfolio, date, description and difference, then a total per portfolio and source. Run it after the
  migration to see how much older data is out. An old payout journal is fixed by opening the payout on the new
  site and saving it.
- `2026-10-15-accounting.sql` (run after the ledger fixes): what the Accounting menu needs. It adds
  `uspUpdateChartOfAccount`, `uspSetChartOfAccountActive` and `uspAddBankImportJournal` (the old import page wrote
  its journals with inline SQL), returns the parent and active flag from `uspGetChartOfAccounts`, and makes
  `uspAllocateSuspenseTransaction` check the line and account belong to the portfolio and date the allocation on
  the bank transaction's date instead of today.

- `2026-10-15-financial-statements.sql`: adds `tblClientDetails`, `tblClientOfficeholders` and `tblPracticeDetails` and the procs
  the Clients, Client details and Practice details pages use. It only adds things; nothing existing changes.
- `2026-10-15-groups-and-sharing.sql`: **required before deploying the build with Groups and Sharing**, because the portfolio
  picker now reads `uspGetAccessiblePortfolios`. Adds `uspRenameCustomGroup`, `uspDeleteCustomGroup`,
  `uspGetAccessiblePortfolios`, `fnCanManagePortfolio`, `uspGetPortfolioShares`, `uspSharePortfolio` and
  `uspRemovePortfolioShare`, and makes `uspCreateCustomGroup` return a result row. A permission row whose access text is
  View or Read only now opens the portfolio as View only; every other row keeps full access.
- `2026-10-15-region-settings.sql` (run after groups-and-sharing): adds country, financial year start and date/number
  format columns to `tblUsers` (blank means Australia), plus `uspGetPortfolioRegion` and `uspSavePortfolioRegion` for the
  Portfolio settings page. Until it's run every portfolio shows with Australian settings, as before.
- `2026-10-15-navexa-sync.sql`: adds `tblNavexaConnection` (the encrypted API key and settings), `tblNavexaMapping` and
  `tblNavexaSyncItem` (every Navexa record seen, so nothing is added twice), plus `uspAddNavexaCashTransaction`, which adds a
  cash entry like `uspAddCashTransaction` but posts its other side to a chosen ledger account (interest income, bank fees).
  Only the Navexa sync page uses them; until it's run that page shows an error and nothing else changes.
- `2026-10-15-realised-gains.sql` (run after the ledger fixes): **required before deploying the build that posts realised gains**,
  because every ledger report calls its procs. Adds system account 4500 Realised Gains on Investments (it stops and changes nothing
  if 4500 is already a non-Revenue account), `uspGetSaleJournals` and `uspPostSaleGain`.
- `2026-10-15-revaluation.sql` (run after realised-gains): the Market value revaluation page. Adds system accounts
  1390 Investments - Market Value Adjustment, 4600 Unrealised Gains on Investments, 2850 Deferred Income Tax Liability and
  5950 Income Tax Expense - Deferred (it stops and changes nothing if one of those numbers is a different type of account),
  plus `uspStartRevaluation`, `uspFinishRevaluation` and `uspDeleteRevaluation`. Only that page uses them.
- `2026-10-15-bank-reconciliation.sql`: the Bank reconciliation page. Adds `tblBankReconciliation` and
  `tblBankReconciliationLine` (the ledger lines ticked on each statement), plus `uspSaveBankReconciliation`,
  `uspReopenBankReconciliation` and `uspDeleteBankReconciliation`. Journals aren't touched; only that page uses them.
- `2026-10-15-lock-periods.sql` (run after realised-gains and bank-reconciliation): **required before deploying the build
  with Lock periods**, because the ledger reports read `fnLockedTo` through `uspGetSaleJournals`. Adds `tblLockedPeriod`,
  `tblLockedPeriodHistory`, `fnLockedTo`, `uspGetLockedPeriod`, `uspSetLockedPeriod` and `uspGetLockedPeriodHistory`, and
  triggers on `tblJournal`, `tblJournalLines`, `tblShareTrades`, `tblPayouts`, `tblCorporateActions`, `tblBankStatement` and
  `tblBankReconciliation` that refuse (error 50300) any change dated in a locked period, from either site or SSMS. Nothing
  changes for a portfolio until it is locked. A share split in an open year still adjusts the units of earlier trades.
- `2026-10-15-allocation-rules.sql`: **required before deploying the build with Allocation rules**, because the Unallocated
  page reads them. Adds `tblAllocationRule` and `uspGetAllocationRules`, `uspSaveAllocationRule` and `uspDeleteAllocationRule`.
  Rules allocate through the existing `uspAllocateSuspenseTransaction`.
- `2026-10-16-tax-workpaper.sql` (run after lock-periods, it uses `fnLockedTo`): **required before deploying the build with
  the Tax menu**. Adds `tblTaxYear` (losses brought forward, company rate, ECPI %, PAYG instalments, resolution date) and
  `tblTaxAdjustment`, with `uspGetTaxYear`, `uspSaveTaxYear`, `uspSaveTaxAdjustment` and `uspDeleteTaxAdjustment`.
- `2026-10-16-trust-distributions.sql` (after tax-workpaper): adds `tblTrustBeneficiary` and `uspGetTrustBeneficiaries`,
  `uspSaveTrustBeneficiary`, `uspDeleteTrustBeneficiary` and `uspCopyTrustBeneficiaries` for Tax > Trust distribution.
- `2026-10-16-smsf-members.sql` (after tax-workpaper): **required before deploying**, because the tax pages read the members
  of an SMSF. Adds `tblSmsfMember` and `tblSmsfMemberAccount` with `uspGetSmsfMembers`, `uspSaveSmsfMember`,
  `uspDeleteSmsfMember`, `uspSaveSmsfMemberAccount` and `uspDeleteSmsfMemberAccount`.
- `2026-10-16-franking-account.sql` (after lock-periods): adds `tblFrankingEntry` and `uspGetFrankingEntries`,
  `uspAddFrankingEntry` and `uspDeleteFrankingEntry` for Tax > Company tax and franking. The Division 7A calculator needs nothing.
- `2026-10-17-sales-tax.sql` (after accounting and allocation-rules): **required before deploying**, because bank
  allocations and allocation rules send the new tax code. Adds `tblSalesTaxSetup`, `tblTaxCodes`, three columns on
  `tblJournalLines` (`iTaxCodeID`, `dblTaxAmount`, `bIsTaxLine`) and `tblAllocationRule.iTaxCodeID`, the procs
  `uspGetSalesTaxSetup`, `uspSaveSalesTaxSetup`, `uspGetTaxCodes`, `uspSaveTaxCode`, `uspDeleteTaxCode`,
  `uspAddJournalLineWithTax` and `uspGetSalesTaxLines`, and an optional tax code on `uspAllocateSuspenseTransaction`,
  `uspGetAllocationRules` and `uspSaveAllocationRule`.
- `2026-10-17-unallocated-fix.sql`: `uspGetUnallocatedTransactions` only lists lines of imported statements. Before, the
  line an allocation posts to clear Bank Import Suspense came back on the Unallocated page as a new transaction, and
  Apply rules could allocate it again, undoing the first allocation.
- `2026-10-18-shared-access.sql` (after groups-and-sharing): **required before deploying**, because the Sharing page reads
  invitations. Adds the Editor role (`Edit`) and invitations: `tblPortfolioInvites`, `fnAccessLevel`,
  `fnCanSharePortfolio`, `uspGetPortfolioInvites`, `uspCancelPortfolioInvite`, `uspAcceptPortfolioInvites`, and changes
  `uspGetAccessiblePortfolios`, `uspGetPortfolioShares`, `uspSharePortfolio` and `uspRemovePortfolioShare`. Existing
  shares keep what they could do.
- `2026-10-18-invoicing.sql` (after ledger-fixes and sales-tax): **required before using Invoices**. Adds system accounts
  1200 Accounts Receivable and 4800 Sales and Fee Income, `tblCustomers`, `tblInvoiceSettings`, `tblInvoices`,
  `tblInvoiceLines` and `tblInvoicePayments`, and `uspGetInvoiceSettings`, `uspSaveInvoiceSettings`, `uspGetCustomers`,
  `uspSaveCustomer`, `uspDeleteCustomer`, `uspGetInvoices`, `uspGetInvoice`, `uspSaveInvoice`, `uspPostInvoiceJournal`,
  `uspVoidInvoice`, `uspAddInvoicePayment` and `uspDeleteInvoicePayment`.
- `2026-10-20-api-keys.sql` (after shared-access): **required before using API keys**, the JSON API or the MCP server.
  Adds `tblApiKeys` (only a SHA-256 hash of each key is kept) and `tblApiAuditLog` (the agent change log), with
  `uspAddApiKey`, `uspGetApiKeys`, `uspRevokeApiKey`, `uspFindApiKey`, `uspAddApiAuditLog` and `uspGetApiAuditLog`.
  It only adds things; until it's run the API keys page shows a message and the rest of the site is unchanged.
- The 2026-10-21 scripts are numbered 01 to 29 in the order they must run (they were renamed on 22 October 2026, when a
  fresh install showed that alphabetical order was wrong). If you already ran some under their old names, they are
  safe to run again.
- `2026-10-21-01-account-security.sql` (after api-keys): **required before deploying** the build with password reset,
  email checks and two-factor sign-in (login reads the new columns). Adds `strTwoFactorSecret` and
  `strTwoFactorRecoveryCodes` to `tblUsers`, marks every existing login's email as checked (first run only, so nobody
  is locked out), returns the new columns from `uspGetLoginDetails`, and adds `uspGetLoginById`,
  `uspSetLoginPassword`, `uspConfirmLoginEmail` and `uspSetTwoFactor`. Email settings are in SETUP.md, step 6.
- `2026-10-21-02-announced-actions.sql` (after corporate-actions): Corporate actions > Announced dividends and splits. Adds
  `tblAnnouncedDividends`, `tblAnnouncedSplits` (filled by the price updater, shared by every portfolio) and
  `tblAnnouncedActionDismissals`, with `uspSaveAnnouncedDividend`, `uspSaveAnnouncedSplit` and `uspDismissAnnouncedAction`.
  It only adds things; until it's run that page shows a message and the price updater logs an error after the prices.
- `2026-10-21-03-parcel-choice.sql` (after lock-periods): choice of parcels on a sale. Adds `tblSaleParcelChoice` and
  `tblSaleParcelAllocation` with `uspGetSaleParcelChoices`, `uspSaveSaleParcelChoice` and `uspDeleteSaleParcelChoice`
  (both refuse a sale in a locked period). Until it's run every sale stays first in, first out and the Parcels page
  shows an error when saving.
- `2026-10-21-04-manual-prices.sql`: Prices > Manual prices. Adds `uspGetManualPrices`, `uspSaveManualPrice` and
  `uspDeleteManualPrice`, which keep a portfolio's own prices as `tblSharePrice` / `tblSharePriceCache` rows with its
  `iUserID` (the holdings procs already prefer those). It only adds procedures.
- `2026-10-21-05-family-groups.sql`: Reports > Consolidated. Adds `tblFamilyGroups`, `tblFamilyGroupMembers`,
  `uspGetFamilyGroups`, `uspSaveFamilyGroup` and `uspDeleteFamilyGroup`. It only adds things.
- `2026-10-21-06-fund-look-through.sql`: fund look-through. Adds `tblFundExposure`, `uspSaveFundExposure` and
  `uspDeleteFundExposure`. It only adds things.
- `2026-10-21-07-fund-tax-statements.sql`: Income > Fund tax statements. Run after `2026-10-15-lock-periods.sql`. Adds
  `tblFundTaxStatements`, `uspSaveFundTaxStatement` and `uspDeleteFundTaxStatement` (both refuse a locked year). It only
  adds things.
- `2026-10-21-10-employee-shares-options.sql`: Income > Employee share schemes and exercising options. Run after
  `2026-10-21-09-complex-corporate-actions.sql`. Adds `tblEssStatements`, `uspSaveEssStatement`, `uspDeleteEssStatement`
  (both refuse a locked year) and `uspExerciseOptions`. It only adds things.
- `2026-10-21-12-debt-fixed-income.sql`: Holdings > Margin and investment loans, and Term deposits and bonds. Run after
  `2026-10-15-lock-periods.sql`. Adds `tblLoans`, `tblLoanEntries`, `tblFixedIncome` and their save and delete procs
  (loan entries refuse a locked period). It only adds things.
- `2026-10-21-11-share-trader.sql`: Tax work > Shares as trading stock. Run after `2026-10-15-lock-periods.sql`. Adds
  `tblShareTrader`, `uspGetShareTrader` and `uspSaveShareTrader` (refuses a change to a locked year). It only adds things.
- `2026-10-21-13-ledger-fx.sql`: foreign trades in the ledger. Run after `2026-10-15-lock-periods.sql` and
  `2026-10-15-ledger-fixes.sql`. `uspAddShareTrade` now posts a trade's journal in the portfolio's currency (the order
  total divided by the exchange rate), as the cash side already was, and existing foreign trade journals outside locked
  periods are converted once. Sale journals go back to proceeds only, and the site re-posts their realised gains.
- `2026-10-21-09-complex-corporate-actions.sql`: demergers, takeovers and code changes. Run after
  `2026-10-21-08-alerts.sql`, `2026-10-21-06-fund-look-through.sql` and `2026-10-21-07-fund-tax-statements.sql`. Adds
  `tblParcelTransfers`, `uspGetParcelTransfers`, `uspApplyDemerger`, `uspApplyTakeover`, `uspChangeStockCode` and
  `uspDeleteParcelTransfer`. The demerger and takeover procs add their trades through `uspAddShareTrade`. It only adds things.
- Roadmap 27 to 39. Run these in this order, after all the earlier 2026-10-21 scripts:
  - `2026-10-21-14-recurring-journals.sql`: repeating and reversing journals. Adds `tblRecurringJournals`, `uspSaveRecurringJournal`,
    `uspMarkRecurringJournalPosted`, `uspSetRecurringJournalActive` and `uspDeleteRecurringJournal`. It only adds things.
  - `2026-10-21-15-attachments-audit.sql`: attachments and the audit trail. Run after the invoicing script. Adds `tblAttachments`,
    `uspAddAttachment`, `uspDeleteAttachment`, `tblAuditLog` and audit triggers on journals, trades, payouts, corporate actions,
    bank lines and invoices. The site tells the triggers who is signed in (`SESSION_CONTEXT`); changes made in SSMS show the app name.
  - `2026-10-21-16-practice.sql`: year status, checklist, workpapers, client queries and onboarding. Adds `tblYearStatus`,
    `tblYearChecklist`, `tblWorkpapers`, `tblClientQueries`, `tblOnboarding` and their save procs. It only adds things.
  - `2026-10-21-17-invoicing-extras.sql`: credit notes, repeating invoices, deposit matching and the invoice logo. Run after
    `2026-10-21-15-attachments-audit.sql`. Adds columns to `tblInvoices`, `tblInvoicePayments` and `tblInvoiceSettings`, adds
    `tblRepeatingInvoices`, and changes `uspPostInvoiceJournal` (credit notes post the other way), `uspAddInvoicePayment` and
    `uspDeleteInvoicePayment` (a deleted deposit match goes back to unallocated). New procs: `uspSetInvoiceKind`,
    `uspMatchInvoiceDeposit`, `uspApplyCreditNote`, `uspSaveInvoiceLogo` and the repeating invoice procs.
  - `2026-10-21-18-ledger-export.sql`: Accounting > Export to Xero or MYOB. Adds `tblLedgerExportMap` and `uspSaveLedgerExportMap`.
  - `2026-10-21-19-rental-properties.sql`: Tax > Rental properties. Adds `tblRentalProperties`, `tblRentalAmounts`, `tblRentalAssets`
    and their save and delete procs. It only adds things.
  - `2026-10-21-20-smsf-extras.sql`: Tax > SMSF TBAR, ECPI and strategy. Run after `2026-10-16-smsf-members.sql`. Adds
    `tblTbarEvents`, `tblSmsfYearExtras`, `uspSaveTbarEvent`, `uspSetTbarReported`, `uspDeleteTbarEvent` and `uspSaveSmsfYearExtras`.
  - `2026-10-21-21-ato-prefill.sql`: Tax > ATO prefill. Adds `tblPrefillItems` and `uspReplacePrefillItems`.
- Roadmap 40, 41 and 43 to 48 (business books). Run these in this order, after `2026-10-21-21-ato-prefill.sql`:
  - `2026-10-21-22-tracking-budgets.sql`: tracking categories and budgets. Adds `tblTrackingCategories`, `tblTrackingOptions`,
    `tblBudgets`, `tblBudgetLines`, two tracking columns on `tblJournalLines` and their save and delete procs. It only adds things.
  - `2026-10-21-23-foreign-currency.sql`: foreign currency accounts. Adds system accounts 4950 and 4960 (realised and unrealised
    exchange gains and losses), `tblForeignCurrencyAccounts`, `tblFxRevaluations`, currency columns on `tblInvoices`,
    `tblInvoicePayments` and `tblJournalLines`, and the foreign bank entry and revaluation procs. It only adds things.
  - `2026-10-21-24-inventory.sql`: stock items. Adds system accounts 1300, 5700 and 5710, `tblInventoryItems`,
    `tblInventoryMovements`, `tblStockAdjustments`, item and tracking columns on `tblInvoiceLines`, and the stock procs
    (average cost is worked through in date order, and each sale's cost of goods sold journal is reposted when it changes).
  - `2026-10-21-25-bills.sql`: bills and payables. Adds system account 2100 Accounts Payable, `tblSuppliers`, `tblBills`,
    `tblBillLines`, `tblBillPayments`, `tblRepeatingBills` and their procs (`uspSaveBill`, `uspPostBillJournal`,
    `uspAddBillPayment`, `uspApplySupplierCredit` and so on).
  - `2026-10-21-26-invoice-books.sql`: invoices with items, tracking and other currencies. Changes `uspGetInvoices`,
    `uspGetInvoice`, `uspSaveInvoice`, `uspPostInvoiceJournal`, `uspVoidInvoice`, `uspAddInvoicePayment`,
    `uspMatchInvoiceDeposit` and `uspApplyCreditNote`. Existing invoices post exactly as before.
  - `2026-10-21-27-quotes-orders.sql`: quotes and purchase orders. Adds `tblQuotesOrders`, `tblQuoteOrderLines` and their procs.
    It only adds things.
  - `2026-10-21-28-expense-claims.sql`: expense claims. Adds system account 2150 Expense Claims Payable, `tblExpenseClaims` and the
    claim procs, and changes `uspAddAttachment` to take the kinds `Bill` and `ExpenseClaim`.
  - `2026-10-21-29-fixed-assets.sql`: fixed asset register. Adds system accounts 1500, 1590, 5750 and 4910, `tblFixedAssets`,
    `tblDepreciationRuns`, `tblDepreciationEntries` and the depreciation and disposal procs. It only adds things.
  - `2026-10-23-01-rental-reports.sql`: Property menu reports and tax. Adds an agent statement code, state and value to
    `tblRentalProperties`, a termination value to `tblRentalAssets`, and `tblRentalOwners`, `tblRentalLoanLinks` and
    `tblRentalSales` with their procs. Changes `uspSaveRentalAsset` (low-value pool, disposals), `uspDeleteRentalProperty`
    (removes everything kept on the property) and `uspAddLoanEntry` (private redraws); the older rental procs now start
    their messages with "Error:".
  - `2026-10-23-02-running-a-property.sql`: Property > Properties, each property's page. Run after the -01 script. Adds
    `tblRentalTenancies`, `tblRentalRentPayments`, `tblRentalTenantBills`, `tblRentalJobs`, `tblRentalInspections`,
    `tblRentalCompliance` and `tblRentalAlertsSent` with their procs, a property alerts setting (`uspSaveAlertSettings`),
    and the attachment kinds `Property`, `RentalJob` and `Inspection` (`uspAddAttachment`).
  - `2026-10-23-03-assistant.sql`: the Assistant page. Adds `tblAssistantSettings` and `tblAssistantChats` with their procs,
    and changes `uspGetApiAuditLog` so the change log shows the assistant's changes as "Site assistant".
  - `2026-10-26-01-part-units.sql`: part units (up to 8 decimal places). Adds `fnUnitsText` and changes the unit
    parameters of `uspAddShareTrade`, `AddShareTradeWithCashTransaction`, `uspAddDRPTransaction`, `uspApplyDemerger`,
    `uspApplyTakeover` and `uspExerciseOptions` to FLOAT, and `tblCGTParcelMatching.iMatchedQuantity` too.
  - `2026-10-26-02-payout-currency.sql`: payouts in another currency. Adds `strCurrency`, `dblExchangeRate` and
    `dblForeignTotal` to `tblPayouts` and the matching optional parameters to `uspAddPayout`; the journal stays in the
    portfolio's currency.
  - `2026-10-26-03-dividend-details.sql`: Import > Dividend details. Adds the franked percentage, DRP price and source to
    `tblAnnouncedDividends`, and `uspSaveAnnouncedDividendDetails`. It only adds things.
  - `2026-10-26-04-fund-statement-journal.sql`: fund tax statements in the ledger. Adds `uspPostFundTaxStatementJournal`
    (the AMIT cost base change between Trust Distribution Income and the fund's cost base account) and posts the
    statements already entered.
  - `2026-10-26-05-ess-employer-register.sql`: Tax > ESS annual report. Adds `tblEssGrants`, `uspSaveEssGrant` and
    `uspDeleteEssGrant`. It only adds things.
  - `2026-10-26-06-trading-stock-extras.sql`: trading stock. Adds `tblShareTraderHoldings` (holdings kept as investments)
    with `uspGetShareTraderHoldings` and `uspSetShareTraderHolding`, and `uspPostTradingStockJournal` with system accounts
    1395 Trading Stock Valuation Adjustment and 4550 Trading Profit on Shares (made when first used).
  - `2026-10-26-07-loans-in-ledger.sql`: loans and fixed income in the ledger. Adds `tblLoans.iAccountID`,
    `uspPostLoanEntryJournal` and `uspPostInterestAccruals` (accounts 2300, 1450, 2390 and 5300 are made when first
    used), and posts the loan entries already recorded.
  - `2026-10-26-08-credit-refunds-split-deposits.sql`: invoicing extras. Adds `uspRefundCreditNote` and
    `uspMatchInvoiceDepositSplit`, and changes `uspDeleteInvoicePayment` so deleting one payment of a split deposit
    deletes the others that share its journal.
  - `2026-10-26-09-accounting-connections.sql`: sending to Xero and MYOB. Adds `tblAccountingConnections` (tokens are
    encrypted by the site before they're saved), `tblAccountingSends`, `uspSaveAccountingConnection`,
    `uspDeleteAccountingConnection` and `uspAddAccountingSend`. It only adds things.
  - `2026-10-26-10-pension-start.sql`: pro-rata minimum pensions. Adds `dtPensionStart` and `dblStartBalance` to
    `tblSmsfMemberAccount` and changes `uspGetSmsfMembers` and `uspSaveSmsfMemberAccount` to read and save them.
  - `2026-10-26-11-bas-extras.sql`: the activity statement. Adds `tblBasSettings`, `tblBasLabels`, `uspSaveBasSettings`,
    `uspSaveBasLabel` and `uspGetSalesTaxCashLines`. It only adds things.
- `2026-10-21-08-alerts.sql`: Email alerts. Run after `2026-10-21-02-announced-actions.sql`. Adds `tblAlertSettings`,
  `tblPriceAlerts`, `uspSaveAlertSettings`, `uspSavePriceAlert`, `uspDeletePriceAlert`, `uspMarkPriceAlertSent` and
  `uspMarkAlertsChecked`. It only adds things.
- `2026-10-27-holdings-cost.sql`: `GetProfitSummaryv2` also returns each holding's cost (buys less sales and returns of
  capital), so the API and MCP `get_holdings` tool show it. Until it runs, the site works the cost out from market value
  and gain, which reads as 0 for a holding with no price.
- `2026-10-27-02-unrealised-gains-speed.sql`: `GetUnrealizedCapitalGains` (the unrealised gains report's prices) no longer
  times out on a long price history: it reads the latest-price cache for today, and looks up past prices by stock so
  the index is used. Same results.
- `2026-10-27-03-crypto-rewards.sql`: crypto rewards. Run after `2026-10-26-01-part-units.sql`. `uspAddDRPTransaction`
  takes an optional `@dblOtherAmount`, kept as the payout's other amount, for coins received as income (staking, airdrops,
  mining), and refuses franking on market `CRYPTO`. Existing callers work as before. Until it runs, adding a crypto
  reward (by hand, from an exchange file or from Navexa) fails with a database error; nothing else changes.
- `2026-10-28-01-speed-indexes.sql`: indexes only, so pages stop reading whole tables: journal lines by journal (every
  ledger report, and the cash movement report, which read every line once per bank line), journals by what posted
  them, bank statement lines by portfolio and date and by trade, a portfolio's own prices, and CGT parcel matches by
  trade. Nothing else changes. Takes a minute or so on a big database.
- `2026-10-28-02-price-trend-speed.sql`: the daily price job's trend update (`uspUpdateMissingSharePriceTrendsByDateAndExchange`)
  finds the traded stocks' new prices through the index instead of reading every price on each pass. Same results.
- `2026-10-28-03-security-log.sql`: adds `tblSecurityEvents` (sign-ins, wrong passwords and codes, password, two-factor
  and API key changes, with the internet address and browser) and four procs: `uspAddSecurityEvent`,
  `uspCountRecentSignInFailures` (pauses an account after 5 wrong attempts in 15 minutes), `uspGetSecurityEvents`
  (Account > Recent sign-ins) and `uspGetSignInHistory` (new-device emails). Run it before deploying: until it runs,
  logging in fails with a database error. After deploying, everyone is signed out once, because sign-in cookies now
  carry a password stamp.
- `2026-10-28-04-terms-acceptance.sql`: adds `tblTermsAcceptances` (which version of the terms of use each login agreed
  to, when and from where) with `uspAddTermsAcceptance` and `uspGetTermsAcceptance`. Run it before deploying: until it
  runs, signing up and every page after sign-in fail with a database error. With the `Legal` settings at their defaults,
  everyone is asked to agree to the terms once at their next page.
- `2026-10-28-05-auto-close-off.sql`: turns off AUTO_CLOSE on the site's database if it is on. With it on, SQL Server
  closes the database whenever nobody is connected, and the first visit afterwards can time out while it reloads.
  Nothing else changes.
- `2026-10-28-06-price-cache-clustered.sql`: gives `tblSharePriceCache` (each stock's latest price) a clustered index.
  Without one, the table's daily delete-and-reload left gigabytes of empty pages that SQL Server read whenever it
  refreshed the table's statistics, so holdings and the dashboard could time out. Can take a minute on a big database.
- `2026-10-28-07-delete-cash-transaction.sql`: adds `uspDeleteCashTransaction`, which deletes a cash transaction entered by
  hand with its journal and recalculates the account's running balances (a trade's settlement, or an entry ticked off on
  a bank reconciliation, is refused). Needed by the API and MCP tools `edit_cash_transaction` and
  `delete_cash_transaction`; nothing else uses it, so the site works without it until then.
- `2026-10-29-delete-portfolios-and-accounts.sql`: adds the procs behind **Delete data** (part or all of a portfolio)
  and **Delete account** (account menu): `uspDeletePortfolioData`, `uspDeletePortfolio`, `uspDeleteLogin`, the
  internal `uspPurgePortfolio`, `uspRecalcBankBalances` and `uspClearDeletedJournalLinks`, and the functions
  `fnCanDeletePortfolio`, `fnIsLoginRow` and `fnLoginDeletePlan`. Running it deletes nothing. Those two pages need it.
- `2026-10-29-delete-sync-records.sql` (after the one above): **Delete data** of trades, payouts or cash now also
  forgets the Navexa sync's record of the ones it had added, so the next sync brings them back instead of skipping
  them. Changes `uspDeletePortfolioData` only.

## API and MCP server

The site has a JSON API at `/api/v1/` and an MCP (Model Context Protocol) server at `/mcp`, so an AI assistant such
as Claude, a script or a mobile app can work with a portfolio. Both run the same list of tools
(`src/PortfolioManager.Web/Api/AgentTools.cs`), which call the same Core services and stored procs as the pages, so
journals, FIFO CGT, locked periods and validation are exactly as on the site.

**Signing in.** Only an API key works (never the login cookie, so another site can't use a signed-in browser). Make
one on the API keys page and send it as `Authorization: Bearer pm_...` (or `X-Api-Key: pm_...`), or let the app get
one by OAuth (below). A key acts as the
login that made it: it reaches the same portfolios (`uspGetAccessiblePortfolios`) with the same role, so on a Read only
portfolio it can only look. A key marked read only can never change anything, and the MCP server doesn't even list
the tools that change things. Revoking a key stops it at once. Each address may make 120 API or MCP requests a minute.

**Change log.** Every call that changes something, done or refused, is written to `tblApiAuditLog` with the key, the
portfolio, the tool, what was sent and the result. It shows on the API keys page.

**Tools** (MCP name; REST path under `/api/v1/portfolios/{portfolioId}`):

| Tool | REST | Changes? |
| --- | --- | --- |
| `list_portfolios` | `GET /api/v1/portfolios` | |
| `get_holdings` | `GET /holdings?openOnly=true` | |
| `list_trades`, `add_trade` | `GET /trades?stockCode=&limit=`, `POST /trades` | add |
| `edit_trade`, `delete_trade` | `PATCH /trades/{tradeId}`, `DELETE /trades/{tradeId}` | yes |
| `list_payouts`, `add_payout`, `delete_payout` | `GET /payouts?from=&to=&stockCode=&limit=`, `POST /payouts`, `DELETE /payouts/{payoutId}` | add, delete |
| `list_cash_transactions`, `add_cash_transaction` | `GET /cash?account=&limit=`, `POST /cash` | add |
| `list_bank_accounts`, `list_ledger_accounts`, `list_tax_codes` | `GET /bank-accounts`, `/ledger-accounts`, `/tax-codes` | |
| `import_bank_transactions` | `POST /bank-imports` | yes |
| `list_unallocated`, `find_bank_transaction_matches` | `GET /unallocated`, `GET /unallocated/{lineId}/matches` | |
| `allocate_bank_transaction`, `match_bank_transaction` | `POST /unallocated/{lineId}/allocate`, `/match` | yes |
| `list_invoices`, `get_invoice` | `GET /invoices`, `GET /invoices/{invoiceId}` | |
| `report_capital_gains`, `report_taxable_income`, `report_profit_and_loss` | `GET /reports/capital-gains`, `/taxable-income`, `/profit-and-loss` (`financialYear=` or `from=&to=`) | |
| `report_unrealised_gains`, `report_trial_balance`, `report_balance_sheet` | `GET /reports/unrealised-gains`, `/trial-balance`, `/balance-sheet` (`asAt=`) | |
| `report_rental_schedule` | `GET /reports/rental-schedule` (`financialYear=`) | |

These tools have no REST path of their own; call them with `POST /api/v1/tools/{name}`:

| Tool | Changes? |
| --- | --- |
| `edit_payout`, `add_drp`, `add_rental_entry`, `edit_rental_entry` | yes |
| `edit_cash_transaction`, `delete_cash_transaction` | yes |
| `list_corporate_actions`; `add_split`, `delete_split`, `add_return_of_capital`, `delete_return_of_capital` | the last four (`preview: true` saves nothing) |
| `list_journals`, `get_journal`; `save_journal`, `delete_journal` (manual journals only) | the last two |
| `list_customers`, `list_suppliers`, `list_bills`, `get_bill` | |
| `save_customer`, `save_supplier`, `save_invoice`, `save_bill` (`approve: true` posts), `send_invoice`, `add_invoice_payment`, `add_bill_payment` | yes |
| `list_manual_prices`; `set_manual_price`, `delete_manual_price` | the last two |

`GET /api/v1/tools` lists every tool with its JSON Schema, and `POST /api/v1/tools/{name}` runs any tool with its
arguments as the JSON body. Bodies use the same field names as the forms (for a trade: `date`, `type`, `market`,
`code`, `quantity`, `price`, `brokerage`, `currency`, `exchangeRate`, `recordCash`, `bankAccount`...). Dates are
`yyyy-mm-dd`.

`edit_trade` takes only the fields that change. The database has no proc to edit a trade, so
`TradesService.ReplaceTradeAsync` deletes it with `uspDeleteShareTrade` and records the new version with the Add trade
proc in one transaction (journals, cash settlement and bank balances follow, and custom group links are put back); the
trade gets a new id, which the tool returns. A cash settlement is kept in the same account, its date moving with the
trade date. Trades a DRP, split, demerger/takeover or saved parcel choice points at are refused with what to do
instead, and a locked period refuses the whole change. Every other edit tool works the same way: send the id and only the fields that
change (`AgentCall.BindOver` lays them over the stored record, which then goes through the page's own checks).
`edit_cash_transaction` is a delete and re-add too (new id); payouts, rental entries, journals, invoices and bills keep
their ids. `send_invoice` emails the customer (or `to`) with replies going to the key's login. Errors come back as `{"error": "...", "fields": {...}}` with status 400 (invalid), 403 (not allowed),
404 (not found), 409 (locked period) or 500.

**MCP.** The server speaks Streamable HTTP without sessions: each POST to `/mcp` carries one JSON-RPC message and the
reply comes back as JSON (`initialize`, `ping`, `tools/list`, `tools/call`). To connect Claude Code:

```
claude mcp add --transport http portfolio https://app.shareportfoliomanager.com/mcp --header "Authorization: Bearer pm_..."
```

Other MCP clients take the same URL and header.

**Sign-in by OAuth** (`src/PortfolioManager.Web/Api/OAuthServer.cs`), for Claude's custom connectors (claude.ai, desktop
and phone apps) and other MCP apps that can't take a header: add `https://your-site/mcp` as the connector address and
nothing else. A request without a key gets a 401 naming `/.well-known/oauth-protected-resource/mcp`, which points at
`/.well-known/oauth-authorization-server`. The app registers itself at `/oauth/register` (dynamic client registration,
no secret), sends the person to `/Account/Connect` to log in and allow it (with a Read only tick box), and swaps the
one-time code at `/oauth/token`, checked with PKCE (S256 only). What it gets back is an ordinary API key named after the
app, such as "Claude (connected 2026-10-27)", so it is listed, logged and revoked on the API keys page like any other;
it doesn't expire, and there are no refresh tokens. Nothing new is stored: the client id and the code are signed with
the Data Protection keys (keep `DataProtection:KeysPath` set, or apps must connect again after a restart), and a used
code is remembered in memory, which assumes the site runs on one server.

## Deploying to IIS

1. Once only: install the **ASP.NET Core 10 Hosting Bundle** on the server, then restart IIS (`iisreset`).
2. Once only: create a new IIS site on a subdomain (for example `app.shareportfoliomanager.com`) with its own folder,
   e.g. `C:\inetpub\portfolio-manager`. Set its app pool to **No Managed Code**,
   and set **Load User Profile = True** (so sign-ins survive app pool recycles). Add the HTTPS binding/certificate.
3. Once only: put the connection string next to the app in `appsettings.Production.json` (never committed):
   ```json
   { "ConnectionStrings": { "Default": "Server=...;Database=PortfolioManager;User Id=...;Password=...;TrustServerCertificate=true" } }
   ```
4. Each release: double-click `deploy\Deploy.cmd` in a clone of the code on the server (see `deploy/README.md`).
   It does the steps below and backs up the old version first. By hand: on your PC run
   `dotnet publish src/PortfolioManager.Web -c Release -o publish`
   and copy the contents of `publish/` to the new site's folder (keep your `appsettings.Production.json`).
   IIS picks up the new files; if a file is locked, drop an `app_offline.htm` into the folder first and delete it afterwards.

Do **not** copy `src/`, `tests/` or `.git` into any site's `wwwroot`.

## Automatic end-of-day share prices

`src/PortfolioManager.PriceUpdater` is a small program that Windows Task Scheduler runs once a day.
It replaces uploading the share price CSV by hand (the CSV upload still works as a fallback).

Each run it:
1. finds every stock currently held in any portfolio (`tblShareTrades`, Buy less Sell quantity), plus an FX pair such as
   `FOREX` / `USD/AUD` for every foreign currency used by a portfolio's trades or cash accounts, plus anything listed in
   `Prices:AlwaysUpdate` (`ASX:STW` by default, so the Performance page can compare with the ASX 200);
2. asks the price provider (EODHD, https://eodhd.com) for the last 10 days of daily prices for each one, so a missed
   day or a public holiday catches up by itself (one API call per symbol);
3. saves each day with `uspUploadSharePrice`, exactly like the legacy upload (it adds or updates the row in `tblSharePrice`
   and keeps the newest price in `tblSharePriceCache`);
4. runs `uspUpdateMissingSharePriceTrendsByDateAndExchange` for each exchange until it says it has finished, as the
   legacy upload does, so the 200-day average and trend on the Holdings page stay up to date.

A symbol that fails (unknown ticker, rate limit, network error) is logged and skipped; the rest still update.
If the API key is wrong or the daily API allowance is used up, it stops asking and says so.
At the end it logs how many symbols were updated and lists any that were not.

Then, unless `Prices:Announcements:Enabled` is `false`, it asks EODHD for the dividends and splits of every held stock
from the last 60 days (`Prices:Announcements:LookbackDays`, or from `--from` if that is earlier; two more API calls per
stock) and saves them with `uspSaveAnnouncedDividend` and `uspSaveAnnouncedSplit`. Each portfolio sees the ones it held
on Corporate actions > Announced dividends and splits, once they are paid or effective and not yet recorded. A dividend
is worked out as the amount per unit times the units held the day before the ex-date (allowing for later splits), with
the franking taken from the last payout of the same stock. "Add payout" saves it through `uspAddPayout`; it is only offered
when the currency is the portfolio's, the franking is known and the last dividend wasn't reinvested, otherwise "Review"
opens the Add payout (or Add DRP) page filled in. A split is recorded through `uspApplyStockSplit`. EODHD's feeds have no
franking or DRP prices, so check each one against the statement.
Exit code: `0` all fine, `1` some symbols or trends failed, `2` not configured or could not run (e.g. database down).

### EODHD account

Sign up at https://eodhd.com and copy your API token from the account page. The free plan allows only 20 API calls a day
(one per held stock or FX pair) and may not include the ASX, so a paid end-of-day plan is needed in practice.

Exchange codes are mapped to EODHD's: `ASX` → `.AU`, `NYSE`/`NASDAQ` → `.US`, `LSE` → `.LSE`, `NZX` → `.NZ`,
`TSX` → `.TO`, `FOREX` `USD/AUD` → `USDAUD.FOREX`, and a few more. If a holding is on an exchange it doesn't know,
the log says so; add it to `Prices:Eodhd:ExchangeMap` in the settings (e.g. `"ExchangeMap": { "CHIX": "AU" }`).

### Install on the VPS

1. On your PC: `dotnet publish src/PortfolioManager.PriceUpdater -c Release -o publish-prices`
2. Copy the contents of `publish-prices/` to a folder outside the web sites, e.g. `C:\PortfolioManager\PriceUpdater`.
3. In that folder create `appsettings.Production.json` (never committed; keep it when copying new releases):
   ```json
   {
     "ConnectionStrings": { "Default": "Server=...;Database=PortfolioManager;User Id=...;Password=...;TrustServerCertificate=true" },
     "Prices": { "Eodhd": { "ApiKey": "your-eodhd-token" } }
   }
   ```
   Or, instead of the file, set the system environment variables `ConnectionStrings__Default` and `Prices__Eodhd__ApiKey`
   (System Properties → Environment Variables → System variables). Settings are always read from the exe's own folder.
4. Test it once from a command prompt in that folder: `PortfolioManager.PriceUpdater.exe`. It prints one line per symbol
   and a summary.
5. Make a `logs` folder there, then open **Task Scheduler** → **Create Task...** (not "Basic Task"):
   - **General:** name `Portfolio Manager - share prices`; choose a user that can read the folder
     (e.g. your admin account); tick **Run whether user is logged on or not**.
   - **Triggers:** New → **Daily**, start at **8:00 PM** (Sydney time; ASX prices are ready a few hours after the
     4:10 pm close), recur every 1 day.
     The 10-day look-back means a missed run is filled in the next night. If you also hold US shares, add a second
     daily trigger at **9:00 AM** so last night's US close is picked up the same morning.
   - **Actions:** New → Start a program:
     - Program/script: `cmd.exe`
     - Add arguments: `/c PortfolioManager.PriceUpdater.exe >> logs\price-updater.log 2>&1`
     - Start in: `C:\PortfolioManager\PriceUpdater`
   - **Settings:** tick **Stop the task if it runs longer than** 1 hour; "If the task is already running": **Do not start a new instance**.
6. Right-click the task → **Run**, then check `logs\price-updater.log` and the **Last Run Result** column
   (`0x0` = all fine, `0x1` = some symbols failed, see the log).

### Filling in history for a new holding

The 200-day average needs about a year of prices. For a stock with no history in the database, run once by hand with
an earlier start date (one API call per symbol, whatever the range):
```
PortfolioManager.PriceUpdater.exe --from 2025-06-01
```

To try it on your PC, put the same two settings in user-secrets with `DOTNET_ENVIRONMENT=Development`:
```
cd src/PortfolioManager.PriceUpdater
dotnet user-secrets set "ConnectionStrings:Default" "Server=..."
dotnet user-secrets set "Prices:Eodhd:ApiKey" "your-eodhd-token"
```
