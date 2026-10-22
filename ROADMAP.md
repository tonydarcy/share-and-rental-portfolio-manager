# Roadmap

Share Portfolio Manager aims to be one free, open tool for an investor and their accountant: portfolio tracking like
Navexa or Sharesight, the ledger and year-end statements like Xero, and the tax and SMSF work like Class or BGL.

Shares, CGT and franking come first, because that is where other tools are weakest, and the business books follow, so
one tool can cover every entity a client has.

This page lists only what is **not built yet**. Everything already in the site is described in the
[README](README.md#what-it-does). Items are ordered by value to users and accountants against effort. Anything marked
**good first issue** is a self-contained job for a new contributor. Last reviewed 22 October 2026.

## Next: investor features

1. **Part units.** The trade procedures take whole units only, but crypto and some managed funds trade in fractions.
2. **Foreign shares in the ledger.** The journals of a foreign trade post the foreign order total without converting it
   to the portfolio currency, and payouts have no currency of their own (they are entered in the portfolio currency).
3. **Franking and DRP prices for announced dividends.** EODHD's feeds have neither, so announced dividends take the
   franking from the stock's last payout. An ASX announcements source would fill this in.
4. **Broker imports checked against real files.** Each broker CSV layout (CommSec, CMC, SelfWealth, Stake, Pearler,
   Superhero, Interactive Brokers) needs checking against real export files, and PDF contract notes aren't read yet.
   **Good first issue** (one broker each).
5. **Parcel choice in the older reports.** The capital gains Sales table and the unrealised gains report still match
   first in, first out, even when a sale used a different parcel choice.
6. **API and MCP server extras.** Sign-in by OAuth, so an assistant can connect without pasting a key; more tools
   (payouts, journals, creating and sending invoices, editing and deleting); and an OpenAPI description for app
   builders. **Good first issue:** a new tool is one entry in `src/PortfolioManager.Web/Api/AgentTools.cs`.
7. **Alerts by text message or phone notification**, as well as email.
8. **Phone layouts for the busiest pages** (bank allocation, journals), and viewing offline.
9. **Consolidated view extras.** Performance over time for a family group, and a printable pack for the adviser.
10. **Fund look-through files.** Importing fund issuers' holdings files for ETF look-through, instead of entering
    them by hand or using EODHD fundamentals.
11. **Fund tax statement extras.** Importing the fund registries' AMIT/AMMA statement files, and posting the cost base
    change to the ledger.
12. **More corporate actions.** A takeover where only some shares are taken up, and a demerger paid partly as a
    dividend.
13. **Employee share scheme extras.** The $1,000 upfront reduction from the income test, and the ESS annual report for
    employers.
14. **Trading stock extras.** Some holdings as trading stock and others as investments in one portfolio, and posting
    the stock movement to the ledger.
15. **Loans and fixed income in the ledger.** Posting loan entries and the year-end interest accrual, and feeding the
    deductible interest into the return labels.

## Next: accountant features

16. **Year ends by country** on the practice dashboard, for clients in other countries.
17. **Attachments printed into the packs.** The SMSF audit pack and report pack list attachments; the files themselves
    should be printed into the pack instead of downloaded one by one.
18. **Invoicing extras.** Refunding a credit note, and one deposit paying several invoices.
19. **Sending straight to Xero and MYOB.** Today the exports are import files; sending them through Xero's and MYOB's
    APIs would save a step.
20. **Rental property extras.** The low-value pool, balancing adjustments on disposal, and filling the figures from the
    ledger.
21. **SMSF extras.** The actuary data as a file, and pro-rata minimum pensions for pensions started during the year.
22. **Tax planning for other entities.** Companies, trusts and SMSFs (individuals are done), and the Medicare levy
    low-income reduction.
23. **More of the BAS.** Cash-basis reporting of invoiced sales (they are reported by invoice date today), PAYG
    instalments and the other BAS labels.

## Next: business books

24. **Pay an invoice online.** A card or bank payment link on invoices (Stripe or similar), with the payment matched
    to the invoice when it lands.
25. **AI assistant in the site.** A chat panel that answers questions about the books and suggests entries, using the
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
- **Other countries.** Region settings already allow another financial year and currency; each country's tax rules
  (like the CGT discount and franking here) are a plug-in someone from that country could write. See "Using it
  outside Australia" above for what is missing in the UK, the US, Canada and India.
