# Roadmap

Share & Rental Portfolio Manager aims to be one free, open tool for an investor and their accountant: portfolio tracking like Navexa or Sharesight, the ledger and year-end statements like Xero, and the tax and SMSF work like ShareSight. 

Shares, CGT and franking come first,
because that is where other tools are weakest, and the business books (bills, inventory) follow, so one
tool can cover every entity a client has.

Priorities are ordered by value to users and accountants against effort. Anything marked **good first issue** is a
self-contained job for a new contributor. Last reviewed 21 October 2026.

## Done

**Investor:** holdings, trades and cash, payouts (dividends, distributions, interest, rental), DRP, splits and
consolidations, returns of capital, FIFO CGT with the 50% discount, capital gains, taxable income and unrealised gains
reports, performance (time-weighted return, monthly returns, comparison with an index or any share), diversity, share
quote and trends, custom groups, CSV imports, Navexa import and
ongoing Navexa sync, end-of-day prices from EODHD, multiple portfolios, region settings (country, financial year,
currency, format).

**Accounting:** double-entry ledger posted by every trade, payout and corporate action; chart of accounts, manual
journals, QIF/OFX bank import with allocation rules, bank reconciliation, trial balance, P&L, balance sheet, cash
movement; automatic realised gains on sale; market value revaluation with deferred tax; locked periods.

**Working together:** several people in one entity's books, each with their own login: owner (view, change and
share), editor (view and change, for the accountant or bookkeeper) or read only (for an auditor); invitations by email
for people who haven't signed up yet.

**Invoicing:** customers, invoices with GST, VAT or sales tax (prices with or without tax, discount lines), invoice
numbering and payment details, print or save as PDF, part and full payments to a bank account, void, and what is owing
and overdue. Approved invoices post to Accounts Receivable, income and the tax control account, so they appear in the
ledger, the financial statements and the sales tax report.

**API and AI agents:** a JSON API and an MCP server, so an AI assistant such as Claude, or another app, can list
holdings, trades and cash, add trades and cash, import, match and allocate bank transactions, read invoices and run the
CGT, income, trial balance, P&L and balance sheet reports. Each login makes its own API keys (optionally read only); a
key reaches the same portfolios with the same role as its login, and every change made with one is logged.

**Accountant:** clients with entity type, ABN/ACN and officeholders; practice details; financial statements with
comparatives for each entity type; printable report pack with declarations and the APES 315 compilation report.

**Tax (Australia):** income tax reconciliation; trust distribution statement and resolution; SMSF member accounts, caps,
minimum pensions and audit pack; company tax, deferred tax check and franking account; Division 7A calculator; tax
return labels per entity type as a printable page and a CSV keying sheet.

## Then: investor features (Navexa and Sharesight have these)

Ordered by how often investors ask for them.

8. ~~**Automatic dividends and corporate actions.**~~ Done: the price updater pulls announced dividends and splits from
   EODHD, and Corporate actions > Announced offers them for one-click confirmation. Still to do: franking and DRP prices,
   which EODHD's feeds don't have (an ASX announcements source would).
9. ~~**Broker import formats.**~~ Done for CSV exports: CommSec, CMC, SelfWealth, Stake, Pearler, Superhero, Interactive
   Brokers and any CSV with a header row (Import > Share trades). Still to do: checking each against real export files,
   PDF contract notes, and part units. **Good first issue** (one broker each).
10. ~~**Choice of parcels on sale.**~~ Done: first in, first out, parcels picked, smallest gain or largest gain per sale
    (Share trades > Parcels), followed by the CGT figures and the realised gain journal. Still to do: the legacy SQL
    reports (capital gains Sales table, unrealised gains) still match first in, first out.
11. ~~**What-if sale.**~~ Done: Reports > What-if sale, with first in, first out, smallest or largest gain.
12. ~~**Income calendar.**~~ Done: Reports > Income calendar.
13. **Foreign shares done properly.** Done: trades take the FX close for their own date when no rate is entered (Add
    trade and imports), a check page flags trades whose rate looks wrong, foreign income tax offsets are on the taxable
    income report and return labels, and the price updater covers US, UK, Canadian, NZ, Hong Kong and Singapore
    exchanges. Still to do: the ledger journals of foreign trades post the foreign order total without converting it,
    and payouts have no currency of their own (they are entered in the portfolio currency).
14. ~~**More asset types.**~~ Done: managed funds, unlisted shares, property and other assets with manual or CSV
    prices per portfolio (Prices > Manual prices), and crypto priced from EODHD. Still to do: part units (crypto and
    some funds trade in fractions; the trade procs take whole units).
15. ~~**Alerts.**~~ Done: email when a price target is hit, a dividend is announced, a sync fails or a bank import needs
    allocating (account menu > Email alerts). Still to do: alerts by text message or phone notification.
16. ~~**Phone-friendly pages.**~~ Done: the site can be added to a phone's home screen as an app, wide tables scroll
    sideways with the first column kept in view, and inputs don't zoom on iPhones. Still to do: phone layouts for the
    busiest data-entry pages (bank allocation, journals), and offline viewing.
17. **API and MCP server for AI agents.** ~~JSON API, MCP endpoint, per-user and read-only API keys, change log.~~
    Done (see Done above, and README-razor-pages.md "API and MCP server"). Still to do: sign-in by OAuth, so an
    assistant can connect without pasting a key; more tools (payouts, journals, invoices to create and send, editing
    and deleting); and an OpenAPI description for app builders. **Good first issue:** a new tool is one entry in
    `src/PortfolioManager.Web/Api/AgentTools.cs`.

18. ~~**Contribution report.**~~ Done: Reports > Contribution shows which holdings pushed the return up or down over
    a period, split into price gain, income and currency, for each holding and the whole portfolio.
19. ~~**Tax-loss harvesting.**~~ Done: Reports > Tax-loss selling lists holdings with parcels below cost, the loss,
    the change in net capital gain and the tax saved (from the what-if sale), flags purchases in the last 30 days, and
    warns about wash sales.
20. ~~**Consolidated view.**~~ Done: Reports > Consolidated puts a family group's portfolios together (each login makes
    its own groups): each entity's value, cash, income and net capital gain, its share of the group, and every holding
    across the group. Still to do: consolidated performance over time, and a printable pack for the adviser.
21. ~~**ETF look-through exposure.**~~ Done: Reports > Diversity shows sector, country and largest-company exposure
    through ETFs and funds. The price updater can fetch it from EODHD fundamentals (off by default; needs that plan), and
    anyone can enter a fund's figures on Prices > Fund look-through. Still to do: import fund issuers' holdings files.

22. ~~**ETF and managed fund annual tax statements (AMIT/AMMA).**~~ Done: Income > Fund tax statements. Each fund's
    statement replaces its payouts' breakdown in the tax workpapers and return labels (13C, 13Q, 13U, 20M, 20O), its
    gains go into the CGT figures (discount gains grossed up), and its AMIT increase or decrease changes the cost base of
    units held at the year end. Still to do: import the fund registries' statement files, and post the cost base change
    to the ledger.
23. ~~**Complex corporate actions.**~~ Done: Corporate actions > Demerger, takeover or code change. Demergers split the
    cost base by the entered percentage, takeovers can choose scrip-for-scrip rollover (only the cash is taxed), and
    the new shares keep the original purchase dates. Code and name changes move everything to the new code, and
    worthless or delisted shares are a sale for nil. Bonus issues use the split page, and rights issues and share
    purchase plans are purchases. Still to do: a takeover where only some shares are taken up, and the rules for a demerger
    paid partly as a dividend.
24. ~~**Employee share schemes, options and warrants.**~~ Done: Income > Employee share schemes takes each ESS statement
    and puts its discounts on an individual's item 12 labels. Corporate actions > Options exercised gives the shares a cost
    base of the exercise price plus the options' cost, acquired on the exercise date; lapsed options are a loss of their
    cost. Still to do: apply the $1,000 upfront reduction from the income test, and the ESS annual report for employers.
25. ~~**Share trader treatment.**~~ Done: Tax work > Shares as trading stock. From a chosen year the shares are trading
    stock, valued at cost, market value or the lower of the two for each holding; the trading profit replaces capital
    gains in the reconciliation and return labels. Still to do: some holdings as trading stock and others as
    investments in one portfolio, and posting the stock movement to the ledger.
26. ~~**Debt and fixed income.**~~ Done: Holdings > Margin and investment loans (balance, loan-to-value ratio, the year's
    interest and its deductible part) and Holdings > Term deposits and bonds (interest earned in the year and accrued at
    its end). Still to do: post loan entries and the year-end accrual to the ledger, and feed the deductible interest into
    the return labels.

## Then: accountant features (Xero, Xero Practice Manager, Class and BGL have these)

27. ~~**Practice dashboard.**~~ Done: Clients > Practice dashboard, every client's year status, open queries, unallocated
    bank lines and workpapers to review. Still to do: a separate year end for clients in other countries.
28. ~~**Attachments.**~~ Done: on trades, journals, clients, years and workpapers, listed in the SMSF audit pack and report pack.
    Still to do: print the files themselves into the pack (they are listed and downloaded one by one).
29. ~~**Audit trail.**~~ Done: Accounting > Audit trail, with changes after a lock marked.
30. ~~**Client queries.**~~ Done: Clients > Client queries, asked from a bank line or journal, answered by a client with view access.
31. ~~**Repeating and reversing journals.**~~ Done.
32. ~~**Invoicing extras.**~~ Done: deposit matching, repeating invoices, credit notes, customer statement, aged receivables
    and a logo. Still to do: refunding a credit note, and one deposit paying several invoices.
33. ~~**Export to Xero and MYOB.**~~ Done as import files (Xero manual journal CSV, MYOB AccountRight general journal).
    Still to do: sending them straight to Xero or MYOB through their APIs.
34. ~~**Rental property schedule.**~~ Done: Tax > Rental properties, labels 21P, 21Q, 21F and 21U. Still to do: low-value
    pool, balancing adjustments on disposal, and filling the figures from the ledger.
35. ~~**SMSF extras.**~~ Done: TBAR events, ECPI details, pension minute, investment strategy and Section F member labels.
    Still to do: the actuary data as a file, and pro-rata minimum pensions for pensions started during the year.
36. ~~**Prefill and lodgment.**~~ Prefill done: Tax > ATO prefill (paste or upload, compared with the records). Paper
    lodgment done: Tax > Paper tax return lays the figures out to copy onto the ATO's paper form (ordered from
    iorder.com.au, since the ATO doesn't accept printed copies of its return forms) with the address to post it to.
    Electronic lodgment through SBR is under "Later (big jobs)".
37. ~~**Workpapers and year-end checklist.**~~ Done: Tax > Workpapers and year-end checklist.
38. ~~**Tax planning.**~~ Done for individuals: Tax > Tax planning. Still to do: companies, trusts and SMSFs, and the Medicare
    levy low-income reduction.
39. ~~**Client onboarding.**~~ Done: Clients > Onboarding new clients.

## Then: business books (Xero and MYOB have these)

So a small business, not just an investment entity, can keep all its books here.

40. ~~**Bills and payables.**~~ Done: Business > Bills, Suppliers, Repeating bills, Aged payables.
41. ~~**Quotes and purchase orders.**~~ Done: Invoices > Quotes, Business > Purchase orders.
42. **Pay an invoice online.** A card or bank payment link on invoices (Stripe or similar), with the payment matched
    to the invoice when it lands.
43. ~~**Inventory.**~~ Done: Business > Inventory and Stocktake.
44. ~~**Expense claims.**~~ Done: Business > Expense claims.
45. ~~**Receipt and statement reading.**~~ Done: Business > Read a receipt or bill (Claude for photos and scans, if an API key is set).
46. ~~**Fixed asset register.**~~ Done: Business > Fixed assets and Post depreciation.
47. ~~**Budgets and tracking categories.**~~ Done: Accounting > Tracking categories, P&L by tracking, Budgets.
48. ~~**Foreign currency accounts.**~~ Done: Accounting > Foreign currency accounts and Foreign exchange revaluation; invoices and bills take a currency.
49. **AI assistant in the site.** A chat panel that answers questions about the books and suggests entries, using the
    same tools as the MCP server, with every change shown for approval first.

## Using it outside Australia

The site already lets each portfolio pick its country, financial year, currency and number format, keeps foreign
shares in their own currency with exchange rates, and has GST, VAT and sales tax presets for the UK, the US, Canada and
India. Everything else about tax was written for Australia. Outside Australia the franking, CGT discount, SMSF, trust,
Division 7A, tax return labels, paper return, ATO prefill, rental schedule, employee share scheme and fund tax
statement pages either hide themselves or do not apply, and the capital gains report gives plain first in, first out
gains with no local rules applied. The lists below say what someone in each of the four markets most likely to use it
would find missing, roughly in order of how much it matters. Each country's rules would be a plug-in, ideally written
or checked by someone who works with that country's tax.

**Every market**

- Capital gains use first in, first out parcels (or a parcel picked on the sale). Several countries require a pooled
  or average cost instead (below), so the gain shown can be wrong there.
- No tax-free or retirement account types (ISA, SIPP, IRA, 401(k), Roth, TFSA, RRSP, PPF and so on). An account can be
  set up as a separate portfolio, but nothing treats its gains or income as tax free or deferred.
- Withholding tax on foreign dividends can be entered, but there is no foreign tax credit calculation or treaty rates.
- Financial statements, entity types (SMSF, family trust, company) and the compilation report follow Australian
  practice and wording (ABN, ACN, APES 315). Other countries' statutory account formats are not built.
- Broker import formats are Australian apart from Interactive Brokers. Other overseas brokers' CSV exports would need
  mapping (or a saved layout).
- No lodgment anywhere: the site can't submit returns to HMRC, the IRS, the CRA or the Income Tax Department.
- Screens and reports are in English only.
- Data protection: anyone hosting it for others must meet local privacy law (UK GDPR, India's Digital Personal Data
  Protection Act 2023, Canada's PIPEDA and Quebec Law 25, and US state laws), including where the data is stored.

**United Kingdom** (tax year 6 April to 5 April, set up already)

- Share matching rules: a sale must be matched first with shares bought the same day, then within the next 30 days
  ("bed and breakfast"), then against the Section 104 pool at average cost. The site's first in, first out gains are
  wrong for most UK investors who have bought the same share more than once.
- No annual exempt amount (£3,000), no split of gains between the basic and higher CGT rates, and no dividend allowance
  (£500) or dividend tax bands. There is no franking equivalent, so that part simply does not apply.
- London prices: EODHD and most UK data sources quote LSE shares in pence (GBX), not pounds. Until the price loader
  converts pence to pounds, UK holdings priced from the daily feed will be shown 100 times too high. Prices entered by
  hand in pounds are fine.
- No Self Assessment pages (SA100, SA108 capital gains summary) and no excess reportable income from offshore reporting
  funds or notional distributions from accumulation units.
- Making Tax Digital: VAT returns must be filed through HMRC-recognised software over HMRC's API. The VAT report gives
  the nine boxes but can't file them. MTD for Income Tax (from April 2026 for higher incomes) would need quarterly
  updates sent the same way.
- Stamp duty (0.5% on most UK share purchases) has to be entered as brokerage; it is not worked out.
- Company accounts in the Companies House format (FRS 102 / FRS 105, iXBRL tagging) and the CT600 are not built.

**United States** (calendar year, set up already)

- No short-term and long-term split: gains on assets held more than a year are taxed at lower rates, but the site
  shows only one total. (The Australian 12-month CGT discount logic is the starting point for this.)
- No wash sale rule: a loss is disallowed and added to the cost of shares bought within 30 days before or after the
  sale. This is the biggest gap for active US traders.
- Cost basis methods: first in, first out and a picked parcel (specific identification) are there, but average cost
  for mutual funds is not.
- No Form 8949 or Schedule D, no 1099-B, 1099-DIV or 1099-INT import, and no split of qualified and ordinary dividends.
- No foreign tax credit (Form 1116), no PFIC rules for US persons holding non-US funds, and no state income tax.
- Sales tax is set by state, county and city. The preset is a single rate, so rates by location (or a service such as
  Avalara or TaxJar) would be needed for a business with sales across states.
- Business returns (1120, 1120-S, 1065 and K-1s) and US GAAP statement layouts are not built.
- Prices and dividends for NYSE and NASDAQ shares already come through EODHD.

**Canada** (calendar year, set up already)

- Adjusted cost base: Canada requires the average cost of all identical shares a person owns (across all their
  taxable accounts), not first in, first out, so every gain shown is likely to be wrong for repeat purchases.
- No 50% inclusion rate, and no superficial loss rule (a loss denied when the same share is bought within 30 days).
- Dividends: no gross-up and dividend tax credit for eligible and non-eligible dividends (Canada's version of
  franking), which the franking code could be adapted for.
- ETF and fund distributions: return of capital and reinvested ("phantom") capital gains distributions change the
  adjusted cost base each year (T3 slips). The Australian fund tax statement page does something similar and could be
  adapted.
- No T5008, T3 or T5 slip import, no Schedule 3, no T1135 foreign property check (over C$100,000 of foreign shares),
  and no TFSA, RRSP or FHSA contribution room.
- GST/HST is there, but provincial sales taxes (PST, QST) and Quebec's separate return are not.
- Company returns (T2 and its GIFI schedules) are not built.
- TSX and TSX Venture prices already come through EODHD.

**India** (financial year 1 April to 31 March and lakh/crore number format, set up already)

- Capital gains rates and periods: listed shares and equity funds are short term up to 12 months (taxed at 20%) and
  long term after (12.5% above a ₹1.25 lakh yearly exemption). Other assets use 24 months. None of this is worked out.
- Grandfathering: for listed shares bought before 1 February 2018, the cost is the higher of actual cost and the
  31 January 2018 market price (capped at the sale price). Not built.
- First in, first out matches what India requires for shares held in a demat account, so parcel matching itself is
  right.
- Transaction costs: securities transaction tax (STT), stamp duty, exchange charges and GST on brokerage have to be
  entered as brokerage. STT is not deductible from capital gains, so mixing it into brokerage overstates the cost.
- Dividends are taxed at the person's slab rate with TDS deducted. TDS can be recorded as withholding, but there is no
  Form 26AS or Annual Information Statement (AIS) import, though the ATO prefill page is a model for one.
- No ITR-2 or ITR-3 schedules (Schedule CG and the share-by-share Schedule 112A), and no treatment of intraday
  (speculative) or futures and options trading as business income.
- Mutual funds: no import of the consolidated account statement (CAS) from CAMS, KFintech, NSDL or CDSL, which is how
  most Indian investors hold funds. Broker exports from Zerodha, Groww, Upstox and similar are not mapped.
- Prices: NSE and BSE are now in the default EODHD exchange list (codes NSE and BSE).
- GST: the GSTR-3B preset is there, but GSTR-1 filing, e-invoicing (an IRN from the government portal above the
  turnover limit), e-way bills and HSN/SAC codes on invoices are not.
- Company accounts in the Schedule III format and Tally import or export are not built.

## Later (big jobs that need a partner or accreditation)

- **Payroll and Single Touch Payroll.** Pay runs, PAYG withholding, super guarantee and STP reporting. Moved here
  from the main list in October 2026. Payroll is only useful if it can be lodged with the ATO legally, and that means
  registering the site with the ATO as a digital service provider and meeting its security framework, even when the
  reports go through a sending service provider such as Ozedi. Payday Super (from 1 July 2026) adds SuperStream
  contributions through a clearing house. On top of that come yearly changes to tax tables, super rates and the STP
  and SuperStream specifications. Payroll that only records pay runs without lodging them would confuse users and add
  work for no real benefit, and importing pay-run journals from Xero or Employment Hero adds little for people already
  paying for those. Worth revisiting only with a hosted version that has paying customers.
- **Electronic lodgment of tax returns (SBR).** Lodging returns straight to the ATO through Standard Business
  Reporting, instead of on paper. Like payroll, it needs registration as an ATO digital service provider, the ATO's
  security framework, product registration and conformance testing for each return and year, or a partnership with a
  sending service provider or an existing lodgment service. Until then, Tax > Paper tax return covers lodging by post,
  and the label summary CSV covers keying into Xero Tax, HandiTax or LodgeiT.
- **Bank feeds** (deferred, like payroll: not until a hosted version has paying customers). In Australia a legal
  feed means the Consumer Data Right (CDR). Screen-scraping (handing a provider the bank login) is still legal but
  against most banks' terms and is being phased out, so it isn't worth building on. Three ways in, cheapest last:
  - *Become an accredited data recipient ourselves.* Apply to the ACCC with an independent ASAE 3150 security
    assurance report (repeated every two years, with yearly attestations), insurance, AFCA membership, a CDR policy,
    a consent and dashboard flow that meets the Consumer Data Standards, and conformance testing against the CDR
    register. Industry estimates put the first year in the low hundreds of thousands of dollars plus ongoing audit
    and compliance staff time. Not viable for a self-funded project.
  - *Be a CDR representative of an accredited provider* (Basiq, Fiskil, Adatree, Frollo and similar; Yodlee for
    overseas banks). No accreditation of our own, but we sign their contract, meet their security checks, publish
    a CDR policy, and they are liable for us, so they vet us first. Pricing is quoted, not published: expect a
    monthly platform minimum plus a fee per connected account. The build is about 4 to 6 weeks: their consent
    screen, a webhook or nightly pull into the existing bank import (same matching and allocation rules), and a
    page to see and revoke connections. Ongoing: yearly compliance checks, consents expire after 12 months and
    must be renewed, and handling outages. Each self-hosted copy would need its own contract, so this is a
    hosted-only feature.
  - *Trusted adviser route.* A client can tell an accredited app to send their CDR data to their registered tax
    agent or accountant. Useful for a practice, but it still needs an accredited app on the client's side.
  - *What we have instead:* QIF/OFX/CSV bank import with allocation rules and reconciliation already covers this.
    Cheap upgrades that close most of the gap: save each bank's CSV layout, an "email your statement in" address,
    and PDF statement reading. Revisit feeds when a hosted version has enough paying users to cover the provider's
    monthly minimum.
- ~~**GST and BAS**~~ Done for any country's GST, VAT or sales tax (tax codes, report by return box). Still to do:
  cash-basis reporting of invoiced sales (they are reported on the accrual basis, by invoice date, today), PAYG instalments and the other BAS labels.
- **Other countries.** Region settings already allow another financial year and currency; each country's tax rules
  (like the CGT discount and franking here) are a plug-in someone from that country could write. See "Using it
  outside Australia" above for what is missing in the UK, the US, Canada and India.
