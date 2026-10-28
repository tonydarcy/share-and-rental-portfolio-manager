# Setting up Share Portfolio Manager

This guide takes you from nothing to a working copy of Share Portfolio Manager: first on your own computer to try it,
then on a server for real use. You need no programming knowledge for the first part, only the ability to install
programs and copy commands into a terminal.

What you end up with:

- **The website** (`src/PortfolioManager.Web`), which you open in a browser.
- **A SQL Server database**, which holds every account, portfolio, trade and journal.
- Optionally, **the daily price updater** (`src/PortfolioManager.PriceUpdater`), which downloads end-of-day share prices.

## Quick start with Docker

If you have [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker on Linux), one command runs
the website and SQL Server together, builds the database and applies every migration:

```
docker compose up -d
```

Then open http://localhost:8080 and register (the first account is the administrator, see step 4). To load the
made-up sample portfolio as well, start it with `LOAD_SAMPLE_DATA=1 docker compose up -d` (on Windows PowerShell:
`$env:LOAD_SAMPLE_DATA=1; docker compose up -d`) and log in as `demo@example.com` with password `demo-portfolio`
(see "Sample portfolio" in step 6).

- **Before keeping real data in it**, make a file called `.env` next to `docker-compose.yml` containing
  `SA_PASSWORD=` and a long password of your own (upper and lower case letters, a number and a symbol), before the
  first start. The database lives in a Docker volume, so it survives `docker compose down` and restarts; only
  `docker compose down -v` deletes it.
- **Updating:** pull the new code, then `docker compose up -d --build`. New migrations run automatically.
- **Settings** such as the site name or email (step 6) go in the `web` service's `environment` list, with `__` in
  place of `:`, for example `Site__Name: "Smith & Co Portfolios"`.
- The site answers on plain HTTP, which browsers accept for sign-in only at `localhost`. To reach it from other
  computers, put an HTTPS proxy in front of it (Caddy, nginx or IIS, as in step 6).
- SQL Server's container needs an Intel or AMD processor. On Apple Silicon Macs, turn on Rosetta in Docker Desktop.

The rest of this guide is for installing without Docker.

## 1. Install the tools

| What | Where to get it | Notes |
| --- | --- | --- |
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 | Windows, macOS or Linux. |
| SQL Server 2016 or later | https://www.microsoft.com/sql-server/sql-server-downloads | The free **Express** or **Developer** edition is fine. On macOS or Linux, use Docker (below). |
| A way to run SQL scripts | SQL Server Management Studio (Windows), or `sqlcmd` (any system) | `sqlcmd` comes with SQL Server and with the Docker image. |
| Git (optional) | https://git-scm.com | Or download the code as a ZIP from GitHub. |

**SQL Server in Docker** (any system with Docker Desktop or Docker Engine). Pick your own strong password:

```
docker run -d --name portfolio-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Choose-A-Strong-Passw0rd" -p 1433:1433 -v portfolio-sql-data:/var/opt/mssql mcr.microsoft.com/mssql/server:2022-latest
```

The `-v` part keeps your data when the container is restarted or replaced.

## 2. Get the code

```
git clone https://github.com/tonydarcy/share-and-rental-portfolio-manager.git
cd share-and-rental-portfolio-manager
```

## 3. Create the database

The database is built by one script, `Database/CreateDatabase.sql`. It creates every table, procedure and the shared
ledger accounts, and contains no data. It refuses to run in a system database or in a database that already has the
tables, so it can't damage anything by accident.

**With SQL Server Management Studio:**

1. Connect to your server, right-click **Databases**, **New Database...**, name it `PortfolioManager`, OK.
2. **File**, **Open**, choose `Database/CreateDatabase.sql`.
3. In the toolbar's database drop-down pick `PortfolioManager` (not `master`), then **Execute**.
4. The Messages tab should end with `Share Portfolio Manager database created.`

**With sqlcmd** (on Windows with SQL Server installed locally, `-E` signs in as you):

```
sqlcmd -S . -E -Q "CREATE DATABASE PortfolioManager"
sqlcmd -S . -E -d PortfolioManager -b -i Database/CreateDatabase.sql
```

**With the Docker SQL Server** (copies the script into the container and runs it there):

```
docker cp Database/CreateDatabase.sql portfolio-sql:/tmp/CreateDatabase.sql
docker exec portfolio-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Choose-A-Strong-Passw0rd" -C -Q "CREATE DATABASE PortfolioManager"
docker exec portfolio-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Choose-A-Strong-Passw0rd" -C -d PortfolioManager -b -i /tmp/CreateDatabase.sql
```

Messages such as "The module ... depends on the missing object ..." are normal: procedures are created in alphabetical
order and SQL Server notes when one calls another that comes later.

**Then apply any newer migrations.** `CreateDatabase.sql` includes every script in `Database/Migrations` up to and
including 2026-10-16. If the folder has scripts dated after that, run each of them against the same database in file
name order (scripts from the same day are numbered, so `2026-10-21-01-...` runs before `2026-10-21-02-...`). Each one is safe to run twice. Do the same whenever you update to a newer version of the code.

## 4. Run the website on your computer

Tell the site how to reach the database. The connection string is kept outside the code, in .NET's "user secrets" store
on your computer, so it is never committed by mistake. Run this from the repository folder, with the line that matches
your SQL Server:

```
cd src/PortfolioManager.Web

# SQL Server on this Windows computer, signing in as you
dotnet user-secrets set "ConnectionStrings:Default" "Server=.;Database=PortfolioManager;Trusted_Connection=true;TrustServerCertificate=true"

# SQL Server in Docker (or any server with a SQL login)
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Database=PortfolioManager;User Id=sa;Password=Choose-A-Strong-Passw0rd;TrustServerCertificate=true"

cd ../..
```

Then trust .NET's development certificate once (the site only works over HTTPS, because its sign-in cookie is
HTTPS-only) and start the site:

```
dotnet dev-certs https --trust
dotnet run --project src/PortfolioManager.Web
```

Open the `https://localhost:...` address it prints, choose **Register**, and create your account and first portfolio.

**The first account you register is the site administrator.** `SiteAdminUserIds` in
`src/PortfolioManager.Web/appsettings.json` lists the administrator accounts by number, and a new database numbers
its first account 1. Administrators can upload share prices for everyone. Register your own account first, before
you let anyone else in. An administrator is asked to turn on two-factor sign-in (account menu, **Two-factor sign-in**)
before any other page opens. To turn that off, for example on a copy only you use on your own PC, add
`"Security": { "RequireTwoFactorForAdmins": false }` to appsettings.

To check the code builds and its tests pass: `dotnet test PortfolioManager.slnx`.

## 5. Share prices

Holdings, performance and unrealised gains need prices. You have two choices:

- **Upload a CSV by hand** (Import, Share prices) as an administrator. CommSec and End of Day Data formats are accepted.
- **Download them every day** with the price updater and an [EODHD](https://eodhd.com) API key. The free EODHD plan
  allows 20 calls a day and may not include the ASX, so a paid end-of-day plan is needed in practice. Setup, including
  Windows Task Scheduler, is in [README-razor-pages.md](README-razor-pages.md#automatic-end-of-day-share-prices).
  On Linux, run the same program from `cron` instead.

## 6. Run it on a server

Whatever the server, the rules are the same:

- Publish a release build with `dotnet publish src/PortfolioManager.Web -c Release -o publish` and copy only the
  contents of `publish/` to the server. Never copy `src/`, `tests/` or `.git` into a web folder.
- Put the connection string in `appsettings.Production.json` next to the published files (or in the environment
  variable `ConnectionStrings__Default`). That file holds a password: keep it out of git and out of anything you share.
- Serve the site over HTTPS (for example with a free Let's Encrypt certificate). Sign-in does not work over plain HTTP.
- Give the site a SQL login that owns only its own database, not `sa`.
- Back up the database. A nightly full backup kept somewhere other than the server is the minimum.

```json
{
  "ConnectionStrings": {
    "Default": "Server=YOUR-SQL-SERVER;Database=PortfolioManager;User Id=portfolio_app;Password=...;TrustServerCertificate=true"
  },
  "DataProtection": { "KeysPath": "" }
}
```

`DataProtection:KeysPath` is a folder where the site keeps the keys that protect sign-in cookies and stored Navexa API
keys and assistant API keys. Leave it blank on IIS with **Load User Profile** on; otherwise set it to a folder the site
can write to and that survives updates (for example `/var/lib/portfolio-manager/keys`). If those keys are lost, everyone
has to sign in again and saved Navexa and assistant keys have to be re-entered. Anyone who can read the key files can
forge a sign-in, so let only the site's own account (on IIS, the app pool identity) read that folder. On Windows the
site also encrypts new key files for the machine (DPAPI), so a copy of the folder taken elsewhere is useless; keys made
before this change keep working.

### Your own name and logo

The site calls itself "Share Portfolio Manager" unless you say otherwise. Add a `Site` section to
`appsettings.Production.json` (every line is optional):

```json
"Site": {
  "Name": "Smith & Co Portfolios",
  "LogoUrl": "https://example.com/logo.png",
  "BaseUrl": "https://portfolios.example.com",
  "SupportEmail": "help@example.com",
  "OldSiteUrl": "",
  "OldSiteLabel": "Go to the old site"
}
```

- **Name** appears in the menu bar, page titles, emails and to AI assistants.
- **LogoUrl** is a small image shown before the name (about 28 pixels high).
- **BaseUrl** is the site's public address. Set it on any public server: links in emails (password resets, email
  checks, invitations) are built from it, and without it they use whatever address the visitor's browser sent.
- **SupportEmail** adds a "Need help?" line at the foot of each page and to emails.
- **OldSiteUrl** adds a link on the login page, for example while people move across from an older site.
- **DemoEmail** and **DemoPassword** add a "Look around the demo portfolio" button to the login page. Load the demo
  first with `Database/SampleData/demo-portfolio.sql` (login `demo@example.com`, password `demo-portfolio`). Anyone
  who presses the button can change the demo portfolio, so only turn it on for a site meant for trying the app.

### Email and sign-in security

Without email settings the site works as before: nobody can reset a forgotten password themselves, new accounts can
be used straight away, and you send sharing links and invoices by hand. Add an `Email` section with your email
provider's SMTP details to turn on:

- **Password reset by email** ("Forgot your password?" on the login page). The link works once, for 2 hours.
- **An email check for new accounts.** A new account can't log in until its owner follows the link emailed to them.
  Accounts that existed before the `2026-10-21-01-account-security.sql` migration count as checked.
- **Emailed sharing invitations** (Share this portfolio) and **emailed invoices** (Email to customer, on an approved
  invoice). Replies go to the person who shared or sent it.

```json
"Email": {
  "Host": "smtp.office365.com",
  "Port": 587,
  "UserName": "no-reply@example.com",
  "Password": "...",
  "FromAddress": "no-reply@example.com",
  "FromName": ""
}
```

Any provider that offers SMTP works (Microsoft 365, Google Workspace, SendGrid, Amazon SES, Mailgun and others).
`Security` can be added if needed: `Auto` (the default), `StartTls`, `Tls` (port 465) or `None` (a local test server
such as Mailpit only). Like the connection string, the password belongs only in `appsettings.Production.json`.

The site only sends email; it never receives any, so you don't need a mail server or MX records for the site's
address. Some things to know:

- **Use an address you own.** `FromAddress` must be one the SMTP account may send as, ideally on your own domain
  (for example `no-reply@yourdomain.com`). Add the SPF and DKIM records your email provider gives you to that
  domain's DNS, or many emails will land in junk folders.
- **Replies.** Invitations and invoices are sent with "Reply-To" set to the person who shared or sent them, so
  replies go to their own inbox. Replies to password and sign-up emails go to `FromAddress`; if nobody reads that
  mailbox, say so in the name (`no-reply@...`).
- **Microsoft 365** needs "Authenticated SMTP" turned on for the mailbox (Microsoft 365 admin centre, the user's Mail
  settings). **Gmail and Google Workspace** need an app password (Google Account, Security, 2-Step Verification,
  App passwords) rather than the normal password. Host `smtp.gmail.com`, port 587.
- **Set `Site:BaseUrl` too** (see above), so links in emails point to the public address. Email alerts don't run
  without it.
- **Check it works.** Restart the site after changing the settings, then, signed in as the site administrator, open
  the account menu, **Check email settings**, and send yourself a test email. If it fails, the page says why in plain
  English (wrong password, server unreachable, address not allowed and so on).
- **Accounts made before email was turned on.** Accounts that existed before the `2026-10-21-01-account-security.sql`
  migration count as checked, but accounts made after it while email was still off do not. Once email is on, those
  people are asked to confirm their address at their next login, and the login page offers to send the link again.

**Two-factor sign-in** is always available: each person can turn it on from the account menu, **Two-factor sign-in**,
with any authenticator app. They get ten one-off recovery codes for a lost phone. If someone loses both, an
administrator can turn it off for them in the database:
`UPDATE tblUsers SET strTwoFactorSecret = NULL, strTwoFactorRecoveryCodes = NULL WHERE strLoginEmail = '...'`.
The authenticator secrets are protected with the same keys as the sign-in cookie (`DataProtection:KeysPath`), so keep
those keys safe: if they are lost, people with two-factor sign-in need their recovery codes.

**Limits on guessing.** Each address can post the login, sign-up, password reset and code forms at most 10 times a
minute. Each account is also paused for 15 minutes after 5 wrong passwords or codes in a row, wherever they come from,
and its owner is emailed; resetting the password lifts the pause. Every sign-in, wrong attempt and change to passwords,
two-factor sign-in and API keys is kept in `tblSecurityEvents`, and each person sees their own under
**Recent sign-ins** in the account menu. When email is on, people are also emailed when their account is signed in
to from a browser it hasn't seen before. Resetting a password signs out every other browser within a minute. For a public site, also turn on the "I'm human" check (a free [Cloudflare Turnstile](https://www.cloudflare.com/products/turnstile/)
widget) on those forms by adding its two keys:

```json
"Captcha": { "SiteKey": "0x4AAAA...", "SecretKey": "0x4AAAA..." }
```

### Reading receipts and bills (optional)

Business > Read a receipt or bill turns an uploaded PDF or photo into a draft bill, expense claim or trade for you to
check. Without any setting it reads PDFs that contain text, with simple patterns. To read photos and scanned PDFs too,
and to read better, add an [Anthropic](https://console.anthropic.com) API key. The uploaded file is then sent to
Anthropic's API to be read, so mention it in your privacy terms if others use your site.

```json
"DocumentReading": {
  "AnthropicApiKey": "sk-ant-...",
  "Model": "claude-opus-5-5"
}
```

`Model` can be left out. Like the other keys, this one belongs only in `appsettings.Production.json` (or user-secrets
on your PC).

### The assistant (optional)

The Assistant page lets each person chat with Claude about their portfolio and approve the changes it suggests. It
needs nothing set up: each person enters their own [Anthropic](https://console.anthropic.com) API key on the page, and
Anthropic bills them for what they use. The key is stored encrypted with the data protection keys above. If you would
rather pay for everyone (for example on a family install), add your own key and it is used by anyone who hasn't entered
one:

```json
"Assistant": {
  "AnthropicApiKey": "sk-ant-...",
  "Model": "claude-opus-5-5"
}
```

Both settings can be left out. `Model` is the default for people who haven't picked one (Opus 5.5, Sonnet 5.5 or Haiku
5.5). What people ask, and the figures the assistant looks up to answer, are sent to Anthropic's API, so mention it in
your privacy terms if others use your site. Run `2026-10-23-03-assistant.sql` first.

### Sending journals to Xero and MYOB (optional)

Accounting > Export always gives an import file. To let people connect a portfolio to Xero or MYOB and send the
journals straight there, register your site as an app with each one and add its details:

- **Xero**: create a "Web app" at [developer.xero.com](https://developer.xero.com/app/manage).
- **MYOB**: create an app at [developer.myob.com](https://developer.myob.com) (it gives an API key and a secret).

For both, the redirect address is your site's address followed by `/Accounting/Connected`, for example
`https://portfolio.example.com/Accounting/Connected`. Set `Site:BaseUrl` to that same address so the site gives the
right one.

```json
"AccountingLink": {
  "Xero": { "ClientId": "...", "ClientSecret": "..." },
  "Myob": { "ClientId": "...", "ClientSecret": "..." }
}
```

Leave out either system you don't want. `Scopes` can be set for each one if the defaults change (Xero:
`offline_access accounting.manualjournals accounting.settings.read`; MYOB: `sme-general-ledger`), and `Myob:ApiBase`
if MYOB gives you a different API address (the default is `https://api.myob.com/accountright/`). The sign-in tokens
are stored encrypted with the data protection keys above. Run `2026-10-26-09-accounting-connections.sql` first.

### Sample portfolio

`Database/SampleData/demo-portfolio.sql` adds a made-up portfolio covering the last twelve months: a deposit, buys
and a sale, franked dividends, trust distributions, a manual journal and weekly prices. Run it like a migration (it
does nothing if the demo login already exists), then log in as `demo@example.com` with password `demo-portfolio`. It
is handy for screenshots and for trying reports. Its share codes (DEMOBANK, DEMOMINE, DEMOETF, DEMOREIT) are made up,
so their prices never mix with real ones.

### Windows with IIS

1. Install the **ASP.NET Core 10 Hosting Bundle** on the server, then run `iisreset`.
2. Create a site in IIS with its own folder and its own application pool. Set the pool's **.NET CLR version** to
   **No Managed Code** and, under Advanced Settings, **Load User Profile** to **True**.
3. Add an HTTPS binding with a certificate (for example from [win-acme](https://www.win-acme.com/) for Let's Encrypt).
4. Copy the contents of `publish/` into the site's folder and add `appsettings.Production.json` there.
5. For each later release: run any new migration scripts first, then copy the new `publish/` contents over the old
   ones, keeping `appsettings.Production.json`. If a file is locked, put an `app_offline.htm` file in the folder while
   you copy and delete it afterwards.

### Linux with nginx

1. Install the ASP.NET Core 10 runtime (see https://learn.microsoft.com/dotnet/core/install/linux) and nginx.
2. Copy the contents of `publish/` to `/opt/portfolio-manager`, add `appsettings.Production.json` there, and create
   the keys folder: `sudo mkdir -p /var/lib/portfolio-manager/keys && sudo chown www-data /var/lib/portfolio-manager/keys`.
3. Create `/etc/systemd/system/portfolio-manager.service`:
   ```ini
   [Unit]
   Description=Share Portfolio Manager
   After=network.target

   [Service]
   WorkingDirectory=/opt/portfolio-manager
   ExecStart=/usr/bin/dotnet /opt/portfolio-manager/PortfolioManager.Web.dll
   Restart=always
   User=www-data
   Environment=ASPNETCORE_ENVIRONMENT=Production
   Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
   Environment=ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

   [Install]
   WantedBy=multi-user.target
   ```
   then `sudo systemctl enable --now portfolio-manager`.
4. Point nginx at it and add HTTPS with [certbot](https://certbot.eff.org/):
   ```nginx
   server {
       server_name portfolio.example.com;
       location / {
           proxy_pass http://127.0.0.1:5000;
           proxy_set_header Host $host;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```
   `ASPNETCORE_FORWARDEDHEADERS_ENABLED` lets the site see that nginx received the request over HTTPS.
5. SQL Server can run on the same machine (SQL Server for Linux, or the Docker image above) or anywhere the server can
   reach.

## 7. First steps

Once the site is running and you have registered:

1. **Set up the entity.** Each portfolio is one entity (a person, company, trust or SMSF). Use the account menu,
   **Manage portfolios** to add more, and **Portfolio settings** for its country, financial year and currency. Its
   legal name, ABN and address go in **Clients**, and appear on its financial statements and invoices.
2. **Bring in the data.** Add trades and payouts by hand, or use **Import** (CSV trades, Navexa, bank statements).
3. **Share it.** Account menu, **Share this portfolio**, then enter the person's email and pick a role:
   - **Owner:** view and change everything, and decide who else can open it.
   - **Editor:** view and change everything but not who it is shared with, for example the accountant or bookkeeper.
   - **Read only:** look at everything but change nothing, for example an auditor.

   If they haven't signed up yet, an invitation is saved. With email settings (step 6) the site emails it to them;
   otherwise send them the sign-up link the page shows. The entity opens for them as soon as they register with that
   email and log in.
4. **Invoice customers** (optional). To charge GST, VAT or sales tax, first set it up under **Tax**, **GST codes**
   (the button adds the usual codes for the entity's country). Then **Invoices**, **Invoice settings** for numbering,
   payment terms and the bank details printed on each invoice; **Customers** to add who you bill; and **New invoice**.
   A draft posts nothing. Approving posts it to the ledger (Accounts Receivable, the income account and the tax), and
   **Record a payment** on the invoice moves the money to the bank account. Use **Print or save as PDF** to send it,
   or **Email to customer** when the site has email settings.
5. **Connect an AI assistant** (optional). Account menu, **API keys and AI assistants**, then **Make key**. Tick
   **Read only** if the assistant should only look. Copy the key straight away (it is shown once), then give your
   assistant the MCP server address `https://your-site/mcp` with the header `Authorization: Bearer <key>`; for Claude
   Code that is `claude mcp add --transport http portfolio https://your-site/mcp --header "Authorization: Bearer <key>"`.
   In Claude itself (claude.ai, the desktop or phone app) no key is needed: Settings, Connectors, **Add custom
   connector**, paste `https://your-site/mcp`, and log in to your site and click **Allow** when it asks.
   The key can do what your login can, in the portfolios your login can open. Everything it changes is listed on the
   same page, and **Revoke** stops it at once. Other apps can use the same key with the JSON API at
   `https://your-site/api/v1/` (see README-razor-pages.md, "API and MCP server").

## Updating to a new version

1. Back up the database.
2. Run each script in `Database/Migrations` that is newer than your last update, oldest first.
3. Publish and copy the new build as above, keeping `appsettings.Production.json`.

## Getting help

Problems and questions go in the project's GitHub issues. Security problems should be reported privately as
described in [SECURITY.md](SECURITY.md).
