using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Holdings;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.Regions;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Validation;

namespace PortfolioManager.Web.Api;

/// <summary>
/// Everything an agent can do through the API and the MCP server. Each tool calls the same Core
/// services (and so the same stored procs) as the web pages, so journals and CGT stay right.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>Lists return at most this many rows unless the caller asks for more (up to MaxLimit).</summary>
    public const int DefaultLimit = 200;
    public const int MaxLimit = 5000;
    public const int MaxImportLines = 1000;

    private readonly Dictionary<string, AgentTool> _byName;

    public AgentTools()
    {
        All = [.. Build(), .. BuildBooks()];
        _byName = All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentTool> All { get; }

    public AgentTool? Find(string name) => _byName.GetValueOrDefault(name);

    private static List<AgentTool> Build() =>
    [
        new("list_portfolios", "List portfolios",
            "The portfolios this API key can open, with each one's id (used by every other tool) and whether the key can change it.",
            false, new ToolSchema().Build(), ListPortfolios, NeedsPortfolio: false),

        new("get_holdings", "Holdings",
            "Each holding in the portfolio with units, cost (buys less sales and returns of capital), market value, capital gain, payouts and total profit, plus totals.",
            false, new ToolSchema().Portfolio().Bool("openOnly", "Only holdings with units left (default true).").Build(),
            async c => await c.Service<HoldingsService>().GetHoldingsAsync(c.PortfolioId, null, c.Bool("openOnly", true))),

        new("list_trades", "List trades",
            "Share trades, newest first.",
            false, new ToolSchema().Portfolio()
                .String("stockCode", "Only this stock code, e.g. BHP.")
                .Int("limit", $"Most rows to return (default {DefaultLimit}).").Build(),
            async c => Limited(await c.Service<TradesService>().GetTradesAsync(c.PortfolioId, c.String("stockCode")?.ToUpperInvariant(), false), c)),

        new("add_trade", "Add a trade",
            "Records a share buy or sale through the same stored procedure as the Add trade page, posting its journals. "
            + "With recordCash true it also records the bank withdrawal (buy) or deposit (sale) at settlement.",
            true, new ToolSchema().Portfolio()
                .Date("date", "Trade date", required: true)
                .String("type", "Buy or Sell", required: true, choices: ["Buy", "Sell"])
                .String("market", "Stock exchange, e.g. ASX", required: true)
                .String("code", "Stock code, e.g. BHP", required: true)
                .String("name", "Stock name (optional)")
                .Number("quantity", "Number of units (a whole number)", required: true)
                .Number("price", "Price per unit in the trade currency", required: true)
                .Number("brokerage", "Brokerage in the trade currency (default 0)")
                .String("currency", "Trade currency, e.g. AUD", required: true)
                .Number("exchangeRate", "Units of trade currency per unit of portfolio currency (1 when they're the same)", required: true)
                .String("comment", "Note to keep with the trade")
                .Bool("recordCash", "Also record the cash settlement in a bank account")
                .String("bankAccount", "Bank account for the settlement (default: the one used last)")
                .Date("settlementDate", "Settlement date (default T+2)")
                .String("cashComment", "Note for the bank entry").Build(),
            AddTrade),

        new("edit_trade", "Edit a trade",
            "Changes a share trade. Send only what changes; everything else keeps its stored value. The trade is deleted and "
            + "recorded again in one step through the same stored procedures as the site, so its journals, cash settlement "
            + "and capital gains follow, and it gets a new id (returned). A trade from a dividend reinvestment, or one a split, "
            + "corporate action or saved parcel choice uses, can't be edited this way: the error says what to do instead.",
            true, new ToolSchema().Portfolio()
                .Int("tradeId", "The trade, from list_trades", required: true)
                .Date("date", "Trade date")
                .String("type", "Buy or Sell", choices: ["Buy", "Sell"])
                .String("market", "Stock exchange, e.g. ASX")
                .String("code", "Stock code, e.g. BHP")
                .String("name", "Stock name")
                .Number("quantity", "Number of units")
                .Number("price", "Price per unit in the trade currency")
                .Number("brokerage", "Brokerage in the trade currency")
                .String("currency", "Trade currency, e.g. AUD")
                .Number("exchangeRate", "Units of trade currency per unit of portfolio currency")
                .String("comment", "Note to keep with the trade")
                .Bool("recordCash", "Keep (true) or drop (false) the cash settlement. Default: keep it if the trade has one")
                .String("bankAccount", "Bank account for the settlement (default: the one it used)")
                .Date("settlementDate", "Settlement date (default: moves with the trade date)")
                .String("cashComment", "Note for the bank entry").Build(),
            EditTrade),

        new("delete_trade", "Delete a trade",
            "Deletes a share trade with its journals and the cash settlement recorded with it, as the Delete button on the "
            + "Trades page does. A trade from a dividend reinvestment is refused (delete the payout instead).",
            true, new ToolSchema().Portfolio().Int("tradeId", "The trade, from list_trades", required: true).Build(),
            DeleteTrade),

        new("list_cash_transactions", "List cash transactions",
            "Bank account entries (deposits and withdrawals) with running balances, newest first.",
            false, new ToolSchema().Portfolio()
                .String("account", "Only this bank account name.")
                .Int("limit", $"Most rows to return (default {DefaultLimit}).").Build(),
            async c => Limited(await c.Service<CashService>().GetTransactionsAsync(c.PortfolioId, c.String("account"), false), c)),

        new("add_cash_transaction", "Add a cash transaction",
            "Records a manual bank deposit or withdrawal through the same stored procedure as the Add cash page. "
            + "In double-entry portfolios it posts against Owner's Capital; use import_bank_transactions and allocate_bank_transaction to code it to another account.",
            true, new ToolSchema().Portfolio()
                .Date("date", "Transaction date", required: true)
                .String("direction", "Deposit or Withdrawal", required: true, choices: ["Deposit", "Withdrawal"])
                .Number("amount", "Amount, more than zero", required: true)
                .String("bankAccount", "Bank account name (default: the one used last)")
                .String("currency", "Currency, e.g. AUD (default: the account's or the portfolio's)")
                .String("comment", "Description").Build(),
            AddCash),

        new("list_payouts", "List payouts",
            "Dividends, distributions and interest received (and rental entries), newest first, with their franking and tax parts and totals.",
            false, new ToolSchema().Portfolio()
                .Date("from", "Only payouts on or after this date")
                .Date("to", "Only payouts on or before this date")
                .String("stockCode", "Only this stock code, e.g. BHP.")
                .Int("limit", $"Most rows to return (default {DefaultLimit}).").Build(),
            ListPayouts),

        new("add_payout", "Add a payout",
            "Records a dividend, distribution or interest payment through the same stored procedure as the Add payout page, "
            + "posting its journal. The cash received (total) must equal franked + unfranked + interest + trust income + other "
            + "less tax withheld and foreign tax. A payout in another currency is converted at exchangeRate, or that day's rate on file.",
            true, new ToolSchema().Portfolio()
                .Date("date", "Payment date", required: true)
                .String("stockExchangeCode", "Stock exchange, e.g. ASX", required: true)
                .String("stockCode", "Stock code, e.g. BHP (or the bank account name for interest)", required: true)
                .Number("total", "Cash received", required: true)
                .Number("franked", "Franked amount")
                .Number("unfranked", "Unfranked amount")
                .Number("frankingCredit", "Franking credit (not part of the cash)")
                .Number("interest", "Interest")
                .Number("trustIncome", "Trust income")
                .Number("other", "Other income")
                .Number("taxWithheld", "TFN amounts withheld")
                .Number("foreignTax", "Foreign tax paid")
                .String("currency", "Currency of the amounts, if not the portfolio's")
                .Number("exchangeRate", "Units of that currency per unit of portfolio currency")
                .String("comment", "Note to keep with the payout")
                .Bool("confirmed", "Whether the amounts are confirmed from a statement (default true)").Build(),
            AddPayout),

        new("delete_payout", "Delete a payout",
            "Deletes a payout with its journal, as the Delete button on the Payouts page does. Deleting a dividend "
            + "reinvestment (DRP) payout also deletes the units it bought.",
            true, new ToolSchema().Portfolio().Int("payoutId", "The payout, from list_payouts", required: true).Build(),
            DeletePayout),

        new("list_bank_accounts", "List bank accounts",
            "Ledger bank accounts that statements can be imported into, with their ids (for import_bank_transactions).",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<ChartOfAccountsService>().GetImportAccountsAsync(c.PortfolioId)),

        new("list_ledger_accounts", "List ledger accounts",
            "Chart of accounts entries a bank transaction can be allocated to, with their ids (for allocate_bank_transaction).",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<ChartOfAccountsService>().GetJournalAccountsAsync(c.PortfolioId)),

        new("list_tax_codes", "List tax codes",
            "The portfolio's active GST / VAT / sales tax codes, with ids (for allocate_bank_transaction).",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<SalesTaxService>().GetActiveCodesAsync(c.PortfolioId)),

        new("import_bank_transactions", "Import bank transactions",
            "Records bank statement lines into a bank account, as the Bank import page does. They land in Bank Import Suspense "
            + "until allocated. Lines already imported (same date, amount and description) are skipped unless includeDuplicates is true.",
            true, new ToolSchema().Portfolio()
                .Int("bankAccountId", "The bank account, from list_bank_accounts", required: true)
                .Array("transactions", $"The statement lines (at most {MaxImportLines})", new ToolSchema()
                    .Date("date", "Transaction date", required: true)
                    .String("description", "Description from the statement", required: true)
                    .Number("amount", "Positive for a deposit, negative for a withdrawal", required: true), required: true)
                .Bool("includeDuplicates", "Import lines that look already imported too (default false)").Build(),
            ImportBank),

        new("list_unallocated", "List unallocated bank transactions",
            "Imported bank lines still waiting to be allocated. Amount is positive for a deposit.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<BankImportService>().GetUnallocatedAsync(c.PortfolioId)),

        new("find_bank_transaction_matches", "Find matches for a bank transaction",
            "Payouts (for a deposit) and trades whose cash matches an unallocated bank line, for match_bank_transaction.",
            false, new ToolSchema().Portfolio().Int("lineId", "The line, from list_unallocated", required: true).Build(),
            FindMatches),

        new("allocate_bank_transaction", "Allocate a bank transaction",
            "Allocates an unallocated bank line to a ledger account, optionally with a tax code (the account gets the amount less tax).",
            true, new ToolSchema().Portfolio()
                .Int("lineId", "The line, from list_unallocated", required: true)
                .Int("accountId", "The ledger account, from list_ledger_accounts", required: true)
                .String("description", "Description for the journal (default: the bank's)")
                .Int("taxCodeId", "A tax code, from list_tax_codes")
                .Number("tax", "The tax in the amount, when it isn't the code's rate").Build(),
            Allocate),

        new("match_bank_transaction", "Match a bank transaction",
            "Matches an unallocated bank line to a payout or trade already recorded (give payoutId or tradeId), clearing it from suspense.",
            true, new ToolSchema().Portfolio()
                .Int("lineId", "The line, from list_unallocated", required: true)
                .Int("payoutId", "A payout, from find_bank_transaction_matches")
                .Int("tradeId", "A trade, from find_bank_transaction_matches").Build(),
            Match),

        new("list_invoices", "List invoices",
            "Invoices with customer, dates, totals, amount paid and status.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<InvoiceService>().GetInvoicesAsync(c.PortfolioId)),

        new("get_invoice", "Get an invoice",
            "One invoice with its lines and payments.",
            false, new ToolSchema().Portfolio().Int("invoiceId", "The invoice, from list_invoices", required: true).Build(),
            async c => await c.Service<InvoiceService>().GetInvoiceAsync(c.PortfolioId, c.RequiredInt("invoiceId"))
                ?? throw new AgentToolException(404, "There is no invoice with that id in this portfolio.")),

        new("report_capital_gains", "Capital gains report",
            "Realised capital gains for a period: each sale's parcels, cost base and gain, and totals.",
            false, PeriodSchema(),
            async c => await Period(c, async p => await c.Service<CapitalGainsService>().GetAsync(c.PortfolioId, p.From, p.To))),

        new("report_taxable_income", "Taxable income report",
            "Dividends, distributions, interest and other income for a period, with franking credits and withholding.",
            false, PeriodSchema(),
            async c => await Period(c, async p => await c.Service<TaxableIncomeService>().GetAsync(c.PortfolioId, p.From, p.To))),

        new("report_unrealised_gains", "Unrealised gains report",
            "Open parcels at a date with cost base, market value and unrealised gain.",
            false, new ToolSchema().Portfolio().Date("asAt", "Date (default today)").Bool("groupByStock", "One line per stock instead of per parcel").Build(),
            async c => await c.Service<UnrealisedGainsService>().GetAsync(c.PortfolioId, AsAt(c), c.Bool("groupByStock", false))),

        new("report_trial_balance", "Trial balance",
            "Ledger trial balance at a date.",
            false, new ToolSchema().Portfolio().Date("asAt", "Date (default today)").Build(),
            async c => await c.Service<LedgerReportService>().TrialBalanceAsync(c.PortfolioId, AsAt(c))),

        new("report_profit_and_loss", "Profit and loss",
            "Ledger profit and loss for a period.",
            false, PeriodSchema(),
            async c => await Period(c, async p => await c.Service<LedgerReportService>().ProfitLossAsync(c.PortfolioId, p.From, p.To))),

        new("report_balance_sheet", "Balance sheet",
            "Ledger balance sheet at a date.",
            false, new ToolSchema().Portfolio().Date("asAt", "Date (default today)").Build(),
            async c => await c.Service<LedgerReportService>().BalanceSheetAsync(c.PortfolioId, AsAt(c))),

        new("report_rental_schedule", "Rental property schedule",
            "Each rental property's schedule for a financial year (income, each expense, depreciation and capital works, for the whole property "
            + "and the owner's share) and the item 21 totals for the tax return.",
            false, new ToolSchema().Portfolio()
                .Int("financialYear", "The year the financial year ends in, e.g. 2026. Default: the last completed year.").Build(),
            RentalSchedule),
    ];

    private static async Task<object?> RentalSchedule(AgentCall c)
    {
        var year = c.Int("financialYear") ?? FinancialYear.LastCompleted(DateOnly.FromDateTime(DateTime.Today));
        var s = await c.Service<PortfolioManager.Core.TaxWork.RentalService>().GetYearAsync(c.PortfolioId, year);
        return new
        {
            FinancialYear = FinancialYear.Label(year),
            Item21 = new { GrossRent21P = s.GrossRent, Interest21Q = s.Interest, CapitalWorks21F = s.CapitalWorks, OtherDeductions21U = s.OtherDeductions, NetRent = s.Net },
            Properties = s.Properties.Select(p => new
            {
                p.Property.Id,
                p.Property.Address,
                p.Property.OwnershipPct,
                WeeksRented = p.Weeks,
                ExpensesClaimedPct = p.PrivateUseFactor * 100,
                Lines = p.Lines().Where(l => l.Whole != 0).Select(l => new { l.Name, WholeProperty = l.Whole, OwnersShare = l.Share }),
                GrossRent21P = p.GrossRent,
                Interest21Q = p.Interest,
                CapitalWorks21F = p.CapitalWorksShare,
                OtherDeductions21U = p.OtherDeductions,
                NetRent = p.Net,
            }),
        };
    }

    private static async Task<object?> ListPortfolios(AgentCall c)
    {
        var list = await c.Service<PortfolioService>().GetAccessibleAsync(c.Caller.UserId);
        return list.Select(p => new
        {
            p.PortfolioId,
            p.Name,
            p.EntityType,
            Role = PortfolioService.AccessLabel(p.Access),
            CanEdit = AgentToolRunner.CanEdit(c.Caller, p),
        }).ToList();
    }

    private static async Task<object?> AddTrade(AgentCall c)
    {
        var form = c.Bind<TradeForm>();
        var result = TradeEntry.Validate(form, FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } trade)
            throw AgentToolException.Invalid(result.Errors);

        var cash = c.Service<CashService>();
        if (trade.Cash is not null)
        {
            var currency = await cash.GetPortfolioCurrencyAsync(c.PortfolioId);
            if (TradeEntry.CheckSettlementAccount(trade, currency, await cash.GetBankAccountsAsync(c.PortfolioId)) is { } error)
                throw AgentToolException.Invalid([error]);
        }

        await c.Service<TradeService>().AddAsync(c.PortfolioId, trade);
        return new
        {
            Message = $"Recorded the {trade.TypeText.ToLowerInvariant()} of {trade.Quantity:N0} {trade.Code} at {trade.Price} {trade.Currency}"
                + (trade.Cash is null ? "." : ", with its cash settlement."),
            Trade = trade,
        };
    }

    private static async Task<object?> EditTrade(AgentCall c)
    {
        var tradeId = c.RequiredInt("tradeId");
        var trades = c.Service<TradesService>();
        if (await trades.GetTradeAsync(c.PortfolioId, tradeId) is not { } stored)
            throw new AgentToolException(404, "There is no trade with that id in this portfolio. list_trades gives the ids.");

        var form = TradeEdit.Merge(stored, await trades.GetTradeCashAsync(c.PortfolioId, tradeId), c.Bind<TradeChanges>());
        var result = TradeEntry.Validate(form, FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } trade)
            throw AgentToolException.Invalid(result.Errors);

        var cash = c.Service<CashService>();
        if (trade.Cash is not null)
        {
            var currency = await cash.GetPortfolioCurrencyAsync(c.PortfolioId);
            if (TradeEntry.CheckSettlementAccount(trade, currency, await cash.GetBankAccountsAsync(c.PortfolioId)) is { } error)
                throw AgentToolException.Invalid([error]);
        }

        var saved = await trades.ReplaceTradeAsync(c.PortfolioId, tradeId, trade);
        return saved.Outcome switch
        {
            TradeEditOutcome.NotFound => throw new AgentToolException(404, "There is no trade with that id in this portfolio."),
            TradeEditOutcome.Blocked => throw new AgentToolException(409, $"The trade wasn't changed. {saved.Reason}"),
            _ => new
            {
                Message = $"Changed trade {tradeId}: now the {trade.TypeText.ToLowerInvariant()} of {trade.Quantity:0.########} {trade.Code} "
                    + $"at {trade.Price} {trade.Currency} on {trade.Date:yyyy-MM-dd}"
                    + (trade.Cash is null ? "" : ", with its cash settlement")
                    + $". Its journals were posted again and its new id is {saved.NewTradeId}.",
                OldTradeId = tradeId,
                saved.NewTradeId,
                Trade = trade,
            },
        };
    }

    private static async Task<object?> DeleteTrade(AgentCall c)
    {
        var tradeId = c.RequiredInt("tradeId");
        var result = await c.Service<TradesService>().DeleteTradeAsync(c.PortfolioId, tradeId);
        return result.Outcome switch
        {
            TradeDeleteOutcome.NotFound => throw new AgentToolException(404, "There is no trade with that id in this portfolio. list_trades gives the ids."),
            TradeDeleteOutcome.Blocked => throw new AgentToolException(409, $"The trade wasn't deleted. {result.Reason}"),
            _ => new { Message = $"Deleted trade {tradeId}, along with any cash transaction and journal entries recorded with it." },
        };
    }

    private static async Task<object?> ListPayouts(AgentCall c)
    {
        var from = c.Date("from") is { } f ? f.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
        var to = c.Date("to") is { } t ? t.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
        var summary = await c.Service<PayoutService>().GetPayoutsAsync(c.PortfolioId, from, to);
        var code = c.String("stockCode")?.Trim();
        var rows = code is null
            ? summary.Payouts
            : summary.Payouts.Where(p => string.Equals(p.StockCode?.Trim(), code, StringComparison.OrdinalIgnoreCase)).ToList();
        var limited = Limited(rows, c);
        return code is null ? new { Totals = summary.Total, Payouts = limited } : new { Payouts = limited };
    }

    private static async Task<object?> AddPayout(AgentCall c)
    {
        var input = c.Bind<PayoutInput>();
        if (c.Arg("date") is null)
            throw new AgentToolException(400, "date is required.");
        var portfolioCurrency = RegionSettings.Current.Currency;
        var errors = input.Validate().Concat(input.ValidateCurrency(portfolioCurrency)).ToList();
        if (errors.Count > 0)
            throw new AgentToolException(400, string.Join(" ", errors));

        var foreign = input.IsForeign(portfolioCurrency);
        if (foreign && input.ExchangeRate is null)
        {
            // That day's rate from the price feed, as the Add payout page does
            input.ExchangeRate = (await c.Service<FxRateService>().GetAsync(input.Currency!, portfolioCurrency, DateOnly.FromDateTime(input.Date)))?.Rate
                ?? throw new AgentToolException(400, $"There's no {input.Currency!.Trim().ToUpperInvariant()} exchange rate on file for {input.Date:yyyy-MM-dd}. Send exchangeRate from the statement.");
        }

        await c.Service<PayoutService>().AddAsync(c.PortfolioId, input, portfolioCurrency);
        return new
        {
            Message = $"Recorded the {input.StockCode.Trim().ToUpperInvariant()} payout of "
                + (foreign ? $"{input.Currency!.Trim().ToUpperInvariant()} {input.Total:N2} at {input.ExchangeRate:0.######}." : $"{input.Total:N2}."),
            Payout = input,
        };
    }

    private static async Task<object?> DeletePayout(AgentCall c)
    {
        var payoutId = c.RequiredInt("payoutId");
        return await c.Service<PayoutService>().DeleteAsync(c.PortfolioId, payoutId) switch
        {
            null => throw new AgentToolException(404, "There is no payout with that id in this portfolio. list_payouts gives the ids."),
            { Ok: false, Error: var error } => throw new AgentToolException(409, $"The payout wasn't deleted: {error}"),
            _ => new { Message = $"Deleted payout {payoutId} and its journal, with any units it reinvested (DRP)." },
        };
    }

    private static async Task<object?> AddCash(AgentCall c)
    {
        var form = c.Bind<CashForm>();
        var result = CashEntry.Validate(form, FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } entry)
            throw AgentToolException.Invalid(result.Errors);

        var cash = c.Service<CashService>();
        entry = entry with { Currency = CashEntry.ResolveCurrency(entry.Currency, entry.BankAccount, await cash.GetBankAccountsAsync(c.PortfolioId)) };
        await cash.AddAsync(c.PortfolioId, entry);
        return new
        {
            Message = $"Recorded the {entry.Direction.ToString().ToLowerInvariant()} of {entry.Amount:N2}.",
            CashTransaction = entry,
        };
    }

    /// <summary>A statement line as an agent sends it.</summary>
    public sealed class ImportLine
    {
        public DateOnly? Date { get; set; }
        public string? Description { get; set; }
        public decimal? Amount { get; set; }
    }

    public sealed class ImportRequest
    {
        public List<ImportLine>? Transactions { get; set; }
    }

    /// <summary>Checks the lines an agent sent, as the statement parser checks a file's.</summary>
    public static (List<BankTransaction> Lines, List<string> Errors) CheckImport(IReadOnlyList<ImportLine>? lines, DateOnly today)
    {
        var errors = new List<string>();
        var ok = new List<BankTransaction>();
        if (lines is null || lines.Count == 0)
            errors.Add("Send at least one transaction.");
        else if (lines.Count > MaxImportLines)
            errors.Add($"Send at most {MaxImportLines} transactions at a time.");
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var n = i + 1;
                if (l.Date is not { } date || date < FormRules.EarliestDate || date > today)
                    errors.Add($"Transaction {n}: give a date that isn't in the future.");
                else if (l.Amount is not { } amount || amount == 0 || Math.Abs(amount) > FormRules.MaxMoney || decimal.Round(amount, 2) != amount)
                    errors.Add($"Transaction {n}: give an amount in cents that isn't zero.");
                else
                {
                    var text = System.Text.RegularExpressions.Regex.Replace(l.Description ?? "", @"\s+", " ").Trim();
                    if (text.Length == 0)
                        errors.Add($"Transaction {n}: give a description.");
                    else
                        ok.Add(new BankTransaction(date, text.Length > BankStatementParser.MaxDescription ? text[..BankStatementParser.MaxDescription] : text, amount));
                }
            }
        }
        return (ok, errors);
    }

    private static async Task<object?> ImportBank(AgentCall c)
    {
        var bankAccountId = c.RequiredInt("bankAccountId");
        var imports = c.Service<BankImportService>();
        if ((await c.Service<ChartOfAccountsService>().GetImportAccountsAsync(c.PortfolioId)).All(a => a.AccountId != bankAccountId))
            throw new AgentToolException(400, "That bank account isn't one this portfolio can import into. list_bank_accounts gives the ids.");

        var (lines, errors) = CheckImport(c.Bind<ImportRequest>().Transactions, FormRules.LatestToday(DateTime.UtcNow));
        if (errors.Count > 0)
            throw new AgentToolException(400, string.Join(" ", errors));

        var skipped = 0;
        if (!c.Bool("includeDuplicates", false))
        {
            var existing = await imports.GetImportedAsync(c.PortfolioId, bankAccountId, lines.Min(l => l.Date), lines.Max(l => l.Date));
            var statement = BankStatementParser.FlagDuplicates(
                new BankStatement("API", lines.Select((t, i) => new BankStatementLine(i + 1, t, null)).ToList()), existing);
            skipped = statement.DuplicateCount;
            lines = statement.Lines.Where(l => l.IsValid && !l.Duplicate).Select(l => l.Transaction!).ToList();
        }

        var result = await imports.ImportAsync(c.PortfolioId, bankAccountId, lines);
        return new
        {
            Message = $"Imported {result.Imported} transaction{(result.Imported == 1 ? "" : "s")}"
                + (skipped > 0 ? $" and skipped {skipped} already imported." : ".")
                + (result.Imported > 0 ? " Use list_unallocated and allocate_bank_transaction to code them." : ""),
            result.Imported,
            Skipped = skipped,
            result.BatchId,
        };
    }

    private static async Task<UnallocatedLine> LineAsync(AgentCall c, int lineId) =>
        await c.Service<BankImportService>().GetLineAsync(c.PortfolioId, lineId)
            ?? throw new AgentToolException(404, "That bank transaction wasn't found, or it has already been allocated.");

    private static async Task<object?> FindMatches(AgentCall c)
    {
        var line = await LineAsync(c, c.RequiredInt("lineId"));
        var imports = c.Service<BankImportService>();
        return new
        {
            Line = line,
            Payouts = line.IsDeposit ? await imports.GetPayoutMatchesAsync(c.PortfolioId, line.LineId) : [],
            Trades = await imports.GetTradeMatchesAsync(c.PortfolioId, line.LineId),
        };
    }

    private static async Task<object?> Allocate(AgentCall c)
    {
        var lineId = c.RequiredInt("lineId");
        var result = await c.Service<BankImportService>().AllocateToAccountAsync(c.PortfolioId, lineId, c.RequiredInt("accountId"),
            c.String("description"), c.Int("taxCodeId"), c.Int("taxCodeId") is null ? null : c.Decimal("tax"));
        return Allocated(result, "Allocated.");
    }

    private static async Task<object?> Match(AgentCall c)
    {
        var lineId = c.RequiredInt("lineId");
        var imports = c.Service<BankImportService>();
        var result = (c.Int("payoutId"), c.Int("tradeId")) switch
        {
            ({ } payoutId, null) => await imports.MatchPayoutAsync(c.PortfolioId, lineId, payoutId),
            (null, { } tradeId) => await imports.MatchTradeAsync(c.PortfolioId, lineId, tradeId),
            _ => throw new AgentToolException(400, "Give either payoutId or tradeId."),
        };
        return Allocated(result, "Matched.");
    }

    private static object Allocated(AllocationResult result, string message) =>
        result.Ok ? new { Message = message } : throw new AgentToolException(400, result.Error!);

    private static object Limited<T>(IReadOnlyList<T> rows, AgentCall c)
    {
        var limit = Math.Clamp(c.Int("limit") ?? DefaultLimit, 1, MaxLimit);
        return new { Total = rows.Count, Returned = Math.Min(limit, rows.Count), Rows = rows.Take(limit).ToList() };
    }

    private static System.Text.Json.Nodes.JsonObject PeriodSchema() => new ToolSchema().Portfolio()
        .Int("financialYear", "The year the financial year ends in, e.g. 2026 for 1 Jul 2025 to 30 Jun 2026 in Australia. Default: the last completed year.")
        .Date("from", "Start date, instead of a financial year")
        .Date("to", "End date, instead of a financial year").Build();

    private static async Task<object?> Period(AgentCall c, Func<ReportPeriod, Task<object?>> run)
    {
        var period = FinancialYear.Resolve(c.Int("financialYear"), c.Date("from"), c.Date("to"), DateOnly.FromDateTime(DateTime.Today));
        return new { Period = period, Report = await run(period) };
    }

    private static DateOnly AsAt(AgentCall c) => c.Date("asAt") ?? DateOnly.FromDateTime(DateTime.Today);
}
