# Contributing

Thanks for helping. You don't need to write code to contribute: reporting a wrong figure, explaining how an accountant
actually does a job, or testing a new feature is just as useful.

## Ways to help

- **Report a bug or a wrong number** with the issue form. For tax or accounting figures, say what you expected and why
  (a ruling, a worked example, what Xero or Navexa shows).
- **Suggest a feature.** Check [ROADMAP.md](ROADMAP.md) first; if it's there, add your use case to the issue.
- **Write code.** Items marked "good first issue" in the roadmap are self-contained. Comment on the issue before you
  start something large, so work isn't duplicated.

## Setting up

Follow [SETUP.md](SETUP.md) to get a copy running with its own database. Then:

```
dotnet build PortfolioManager.slnx
dotnet test PortfolioManager.slnx
```


## How the code is laid out

[CLAUDE.md](CLAUDE.md) is the short guide to the layout and rules (it's written for AI coding assistants but reads
fine for people). The essentials:

- Each feature has a service in `src/PortfolioManager.Core/<Area>/`, a page in `src/PortfolioManager.Web/Pages/<Area>/`
  and a test file in `tests/PortfolioManager.Tests/`. Copy the shape of a neighbouring feature.
- **Trades, cash, payouts, corporate actions and journals are written through the stored procedures**, never with
  inline SQL. The procedures post the double-entry journals and keep FIFO capital gains right.
- Database changes go in a new `Database/Migrations/YYYY-MM-DD-<name>.sql` that is safe to run twice
  (`CREATE OR ALTER`, `IF NOT EXISTS`), with a line in the "Database migration" section of `README-razor-pages.md`.
  Scripts run in file name order, so a script that needs another one must sort after it (number same-day scripts:
  `YYYY-MM-DD-01-<name>.sql`). CI builds a fresh database from `CreateDatabase.sql` and every migration, twice.
- Every page works on the portfolio picked in the navbar (`CurrentPortfolio`). Never trust a portfolio id from a form
  or query string.
- Style: .NET 10, file-scoped namespaces, primary constructors, `async` all the way. Screen text in plain English.

## Pull requests

- One feature or fix per pull request, with tests for any calculation.
- Describe the change for a non-developer: what a user sees before and after.
- The build and tests must pass (they run automatically on every pull request).
- No secrets, connection strings, API keys or real client data in commits or screenshots.
- By submitting a pull request you agree your contribution is licensed under the project's
  [AGPL-3.0 licence](LICENSE), and you confirm you have the right to submit it
  ([Developer Certificate of Origin](https://developercertificate.org/)): add `Signed-off-by: Your Name <email>` to
  each commit with `git commit -s`.

## Conduct

Be kind and assume good faith. See the [Code of Conduct](CODE_OF_CONDUCT.md).
