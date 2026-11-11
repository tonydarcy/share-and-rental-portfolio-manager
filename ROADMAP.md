# Roadmap

Share Portfolio Manager aims to be one free, open tool for an investor and their accountant: portfolio tracking like
Navexa or Sharesight, the ledger and year-end statements like Xero, and the tax and SMSF work like Class or BGL.

Shares, CGT and franking come first, because that is where other tools are weakest, and the business books follow, so
one tool can cover every entity a client has.

This page lists only what is **not built yet**. Everything already in the site is described in the
[README](README.md#what-it-does). Items are ordered by value to users and accountants against effort. Anything marked
**good first issue** is a self-contained job for a new contributor. Last reviewed 4 November 2026.

## Next: investor features

1. **Broker imports checked against real files.** Each broker CSV layout (CommSec, CMC, SelfWealth, Stake, Pearler,
   Superhero, Interactive Brokers) needs checking against real export files, and PDF contract notes aren't read yet.
   **Good first issue** (one broker each).
2. **API and MCP server extras.** An OpenAPI description for app builders; REST paths for the newer tools (they run
   through `POST /api/v1/tools/{name}` for now); and an Edit button on the site's Trades page using the same edit as the
   `edit_trade` tool. **Good first issue:** a new tool is one entry in `src/PortfolioManager.Web/Api/AgentTools.cs`.
3. **Alerts by text message or phone notification**, as well as email.
4. **Franking and DRP prices fetched automatically.** Announced dividends get their franking and DRP price from a file
   the site admin uploads (Import > Dividend details). An ASX announcements feed would fill them in by itself, but the
   ASX's data needs a paid licence.
5. **Registry statement files checked against real ones.** Fund tax statement files and fund holdings files are read
   by their column names; each registry's and issuer's layout (Computershare, MUFG / Link, Boardroom, Automic, Vanguard,
   iShares, BetaShares) needs checking against real files, and PDF statements aren't read. **Good first issue.**
6. **Crypto exchange files checked against real ones.** Coinbase, Swyftx, CoinSpot and Independent Reserve exports
   are read by their column names; each needs checking against a real file, and more exchanges (BTC Markets, Kraken,
   Binance) could be added. **Good first issue** (one exchange each).

## Next: business books

7. **Pay an invoice online.** A card or bank payment link on invoices (Stripe or similar), with the payment matched
    to the invoice when it lands.
8. **Assistant extras.** Replies that appear as they are written, reading an attached receipt or statement in the
    chat, and a monthly spending limit when the host shares their own Anthropic key with every user.

## Next: security

9. **Keep each portfolio's data unreadable to anyone without access.** Today a portfolio is kept private by the
    access check on each page and API call. Three layers would mean a bug, a leaked backup or a stolen disk can't expose
    one user's data to another:
    - *Row-level security in SQL Server,* using the same access check that shared portfolios already use, so the
      database itself refuses rows from portfolios the signed-in user can't see. Every proc needs testing to make sure
      it hides nothing it should show.
    - *Encrypting private text and files per portfolio:* attachments, notes, bank account numbers, ABNs and tenant
      details. Each portfolio gets its own random key. A copy is stored for each person with access, locked with a
      key worked out from their password. The server unlocks it at sign-in and keeps it for the session, and the
      stored procs decrypt with it. A row fetched for the wrong user comes back unreadable. API keys (REST, MCP,
      Assistant) each need their own locked copy. A forgotten password needs a recovery code, or another person on
      the portfolio to share it again.
    - *Disk and backup encryption* (SQL Server TDE or BitLocker on the server), a setting rather than code.

    **Speed matters here.** Amounts, dates and quantities stay unencrypted. If they were encrypted, every report would
    have to decrypt every row on every run and couldn't use indexes, and CGT, the ledger and the dashboard are already
    slow in places. Before turning this on, time the slowest pages on a large test database with and without
    row-level security, and fix any page that gets noticeably slower first. This is not true end-to-end encryption
    (the server holds the keys while a request runs), so it doesn't protect against someone who takes over the server.

## Next: tidying up

10. **Retire single-entry accounting.** Portfolios made before December 2025 can still be single entry, so their
    trades, cash and payouts post no journals. They can now be switched (Portfolio settings > Switch to double entry),
    and new ones are always double entry. Once every live portfolio has been switched: a migration that switches any
    left (after checking each one's bank imports, which can double up with payouts), then take out the single-entry
    branches in the procs (`IF @strAccountingMode IN ('Hybrid', 'DoubleEntry')`), the `AccountingMode` checks and
    single-entry notices in the site, and finally the `strAccountingMode` column.

## Next: phone apps

11. **iOS and Android apps.** The site already works on a phone and can be added to the home screen (web app
    manifest, service worker, phone-friendly tables), so the question is what a store app adds: a listing people can
    find, push notifications, Face ID / fingerprint unlock, the camera for receipts, and an offline copy. The plan is
    in three steps, each useful on its own.

    **Choices considered.**
    - *Better web app only (PWA).* Cheapest; no store fees or reviews. Push works on Android, and on iPhone since
      iOS 16.4 once the site is added to the home screen. No store listing, and Apple limits what it can do.
    - *The site wrapped in a store app* (Capacitor or a plain web view). Fast to make, but Apple rejects apps that are
      only a website in a frame (App Review Guideline 4.2), so it would need real native features anyway.
    - *.NET MAUI app over the existing API* (recommended). One C# code base for iOS and Android, the same language and
      tools as the site, MIT licensed, and it can share model classes with `PortfolioManager.Core`. Calls `/api/v1`.
    - *React Native or Flutter.* Mature, but adds a second language and toolchain for little gain here.

    **Step 1: web app extras (about 1 to 2 weeks).** Web push for the alerts in item 3 (a device table, VAPID keys,
    a subscribe switch in Email alerts), an "Install app" prompt, and Face ID / fingerprint sign-in by passkey.
    Useful for everyone straight away, including people running their own copy.

    **Step 2: the API the app needs (about 2 weeks).** Builds on item 2. *Partly done (2026-11-11):* dashboard,
    price history, share code search, attachments, the app's sign-in return address and Firebase push for alerts are
    built; still to do from this step: OpenAPI, token expiry and refresh, a "Signed-in devices" page, and alerts
    settings over the API.
    - OpenAPI description and REST read paths for the dashboard, holdings, payouts, cash, alerts and documents.
    - Sign-in: the app opens the site's own sign-in page in the system browser (OAuth with PKCE, already used by
      `OAuthServer.cs` for Claude connectors), so 2FA and lockout keep working. The token goes in the phone's
      Keychain / Keystore; tokens need an expiry, refresh, and a "Signed-in devices" list in the account menu to
      revoke a lost phone. (The app's return address `com.shareportfoliomanager.app:/oauth` is allowed.)
    - A device token table and a sender for Apple (APNs) and Google (Firebase Cloud Messaging) push, called by
      `AlertService`. Rate limits on the API.
    - The app asks for the site address on first start (default app.shareportfoliomanager.com), so self-hosted copies
      work too, as the Nextcloud and Home Assistant apps do.

    **Step 3: the app (version 1 about 6 to 10 weeks).** `src/PortfolioManager.Mobile` (.NET MAUI). *Started
    (2026-11-11):* an Android test app that signs in through the site (any site address) and shows each portfolio's
    holdings; GitHub builds an installable APK. Everything below is still to do.
    - Mostly reading: portfolio picker, dashboard, holdings and prices, payouts and income calendar, CGT and
      unrealised gains, alerts. Quick entries: add a trade, payout or cash line (through the same tools and procs as
      the site), and a photo of a receipt or contract note attached to a bill or trade.
    - Face ID / fingerprint to open; screens hidden in the app switcher.
    - Offline: the last-loaded figures kept on the phone, encrypted, shown with an "offline copy" note. No offline
      edits in version 1 (they would need conflict handling against the ledger).
    - Anything else (tax returns, journals, the business books) opens the site in the in-app browser.
    - Builds and signing in GitHub Actions (macOS runners are free for public repos), so no Mac is needed day to day;
      signing keys kept in GitHub secrets.

    **Store accounts and costs.** Apple Developer Program US$99 (about AUD 150) a year; Google Play US$25 once.
    Enrol as an organisation (needs a free D-U-N-S number) so the store shows the business name, not Tony's own.
    New personal Google accounts must run a closed test with 12 testers for 14 days before going public.

    **Store review rules that apply.**
    - A privacy policy link (Legal pages exist), Apple's privacy labels and Google's Data safety form.
    - Deleting the account must be possible inside the app (Apple 5.1.1(v)); the site needs a "Delete my account"
      page first.
    - A demo sign-in for reviewers (a sample portfolio).
    - Google Play's financial features declaration: the app tracks and reports, it doesn't trade, lend or give advice,
      so no licence is needed; the disclaimer is shown in the app.
    - While the site is free there's no in-app purchase issue. If a paid hosted plan comes later, Apple's
      multiplatform rule (3.1.3(b)) lets people sign in to a plan bought on the website, but the app can't point to the
      website to buy (outside the US) unless Apple's in-app purchase is also offered.
    - If sign-in with Google or another social account is added to the app, Apple also wants a privacy-focused option
      such as Sign in with Apple (4.8).

    **Licence.** The App Store's terms clash with the AGPL for code written by others (VLC was pulled for this in
    2011). Tony owns the code, so he can publish the app himself; outside contributions to the app would need a
    contributor agreement or a store exception added to the licence. Settle this before accepting app pull requests.

    **Awaiting Tony's check:** the costs and review rules above are as published in late 2026; check them when
    enrolling.

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
- Financial statements and entity types (SMSF, family trust, company) follow Australian practice (the ABN/ACN labels
  and APES references already change by country). Client addresses still expect an Australian state and postcode. Other countries' statutory account formats are not built.
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
- The annual exempt amount is shown, but there is no split of gains between the basic and higher CGT rates, and no
  dividend allowance (£500) or dividend tax bands. There is no franking equivalent, so that part simply does not apply.
- London prices already loaded in pence before the daily feed started converting them (November 2026) stay 100
  times too high until they are reloaded.
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
- Wash sale rule: losses with a buy within 30 days either side are flagged, but the loss isn't disallowed or added to
  the new shares' cost. This is the biggest gap for active US traders.
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
- The 50% inclusion rate is shown and superficial losses are flagged, but a flagged loss isn't denied or added to the
  new shares' adjusted cost base.
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
  long term after (12.5% above a ₹1.25 lakh yearly exemption). Other assets use 24 months. The exemption is shown;
  the rates and the 24-month period are not worked out.
- Grandfathering: for listed shares bought before 1 February 2018, the cost is the higher of actual cost and the
  31 January 2018 market price (capped at the sale price). Not built.
- First in, first out matches what India requires for shares held in a demat account, so parcel matching itself is
  right.
- Transaction costs: STT can be entered on a trade (and is kept out of the gain), but stamp duty, exchange charges and
  GST on brokerage go in brokerage. STT on trades edited or imported from a broker file isn't kept yet.
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
- **The ESS annual report file for the ATO.** Tax > ESS annual report keeps an employer's register, prints each
  employee's statement and gives the figures as a CSV, but not the ATO's own ESS annual report file. That file needs
  each employee's TFN, date of birth and address, which the site deliberately doesn't keep, and lodging it
  electronically has the same digital service provider requirements as SBR above.
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
