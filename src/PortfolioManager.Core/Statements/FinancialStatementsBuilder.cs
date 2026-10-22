using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.Tax;

namespace PortfolioManager.Core.Statements;

/// <summary>
/// Builds the statements from three trial balances: at the end of the year, the end of the year
/// before (the comparative balance sheet and this year's opening balances) and the end of the
/// year before that (last year's opening balances). Revenue and expense accounts are never closed
/// off, so a year's profit is the change in their cumulative balances, and profit to date is
/// rolled into equity at each year end without any closing journal.
/// </summary>
public static class FinancialStatementsBuilder
{
    public static FinancialStatements Build(
        ClientDetails client, int year,
        LedgerSnapshot twoYearsAgo, LedgerSnapshot lastYear, LedgerSnapshot thisYear,
        string? accountingMode = "DoubleEntry")
    {
        var b = new Builder(client, year, twoYearsAgo, lastYear, thisYear);
        return b.Build(accountingMode);
    }

    private sealed class Builder(ClientDetails client, int year, LedgerSnapshot s0, LedgerSnapshot s1, LedgerSnapshot s2)
    {
        private readonly string _type = client.EntityType;
        private readonly List<StatementNote> _notes = [];
        private readonly List<string> _warnings = [];

        private readonly IReadOnlyList<LedgerBalance> _accounts = s0.Accounts.Concat(s1.Accounts).Concat(s2.Accounts)
            .GroupBy(a => a.Key).Select(g => g.First())
            .OrderBy(a => a.Number ?? int.MaxValue).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        private IEnumerable<LedgerBalance> OfType(string type) => _accounts.Where(a => a.Type == type);

        // Balances at the two year ends and movements in each year
        private static decimal Sum(IEnumerable<LedgerBalance> accts, LedgerSnapshot s) => accts.Sum(a => s.Balance(a.Key));
        private static decimal Debits(IEnumerable<LedgerBalance> accts, LedgerSnapshot s) => accts.Sum(a => s.Debit(a.Key));
        private static decimal Credits(IEnumerable<LedgerBalance> accts, LedgerSnapshot s) => accts.Sum(a => s.Credit(a.Key));

        private int AddNote(string title, IReadOnlyList<string> paragraphs, IReadOnlyList<NoteTable> tables)
        {
            var number = _notes.Count + 1;
            _notes.Add(new StatementNote(number, title, paragraphs, tables));
            return number;
        }

        /// <summary>A note table of each account's year-end balance, with a total.</summary>
        private NoteTable BalanceTable(string? caption, IEnumerable<LedgerBalance> accts, string totalLabel, Func<LedgerBalance, string>? label = null)
        {
            var rows = accts
                .Select(a => new StatementRow(label?.Invoke(a) ?? a.Name, s2.Balance(a.Key), s1.Balance(a.Key)))
                .Where(r => !r.IsZero)
                .ToList();
            rows.Add(new StatementRow(totalLabel, rows.Sum(r => r.Current), rows.Sum(r => r.Prior), RowStyle.Total));
            return new NoteTable(caption, rows);
        }

        public FinancialStatements Build(string? accountingMode)
        {
            AddNote("Summary of significant accounting policies", StatementWording.Policies(client, year), []);

            // ---- Income statement ----
            var revenueGroups = OfType("Revenue")
                .GroupBy(a => StatementClassifier.ClassifyRevenue(a.Number, a.Name))
                .OrderBy(g => g.Key)
                .Select(g => (Line: g.Key, Accounts: g.ToList(), Current: Sum(g, s2) - Sum(g, s1), Prior: Sum(g, s1) - Sum(g, s0)))
                .Where(g => g.Current != 0 || g.Prior != 0)
                .ToList();
            var revenue = revenueGroups.Sum(g => g.Current);
            var priorRevenue = revenueGroups.Sum(g => g.Prior);

            int? revenueNote = null;
            if (revenueGroups.Count > 0)
            {
                var rows = new List<StatementRow>();
                foreach (var g in revenueGroups)
                {
                    rows.Add(StatementRow.Heading(StatementWording.RevenueLabel(g.Line)));
                    rows.AddRange(g.Accounts
                        .Select(a => new StatementRow(a.Name, s2.Balance(a.Key) - s1.Balance(a.Key), s1.Balance(a.Key) - s0.Balance(a.Key), Indent: 1))
                        .Where(r => !r.IsZero));
                    rows.Add(new StatementRow("Total " + StatementWording.RevenueLabel(g.Line).ToLowerInvariant(), g.Current, g.Prior, RowStyle.Subtotal));
                }
                rows.Add(new StatementRow("Total revenue", revenue, priorRevenue, RowStyle.Total));
                revenueNote = AddNote("Revenue", [], [new NoteTable(null, rows)]);
            }

            var expenses = OfType("Expense")
                .Select(a => (Account: a, Kind: StatementClassifier.ClassifyExpense(a.Number, a.Name),
                    Current: s2.Balance(a.Key) - s1.Balance(a.Key), Prior: s1.Balance(a.Key) - s0.Balance(a.Key)))
                .Where(e => e.Current != 0 || e.Prior != 0)
                .ToList();
            var operating = expenses.Where(e => e.Kind == ExpenseLine.Operating).ToList();
            var taxExpenses = expenses.Where(e => e.Kind == ExpenseLine.IncomeTax).ToList();
            var opex = operating.Sum(e => e.Current);
            var priorOpex = operating.Sum(e => e.Prior);
            var tax = taxExpenses.Sum(e => e.Current);
            var priorTax = taxExpenses.Sum(e => e.Prior);
            var profit = revenue - opex - tax;
            var priorProfit = priorRevenue - priorOpex - priorTax;

            // ---- Balance sheet lines ----
            var assetGroups = OfType("Asset")
                .GroupBy(a => StatementClassifier.ClassifyAsset(a.Number, a.Name))
                .OrderBy(g => g.Key)
                .Select(g => (Line: g.Key, Accounts: g.ToList(), Current: Sum(g, s2), Prior: Sum(g, s1)))
                .Where(g => g.Current != 0 || g.Prior != 0)
                .ToList();
            var liabilityGroups = OfType("Liability")
                .GroupBy(a => StatementClassifier.ClassifyLiability(a.Number, a.Name))
                .OrderBy(g => g.Key)
                .Select(g => (Line: g.Key, Accounts: g.ToList(), Current: Sum(g, s2), Prior: Sum(g, s1)))
                .Where(g => g.Current != 0 || g.Prior != 0)
                .ToList();

            int? taxNote = null;
            var taxAssets = assetGroups.Where(g => g.Line == AssetLine.TaxCredits).SelectMany(g => g.Accounts).ToList();
            var taxLiabilities = liabilityGroups.Where(g => g.Line == LiabilityLine.TaxPayable).SelectMany(g => g.Accounts).ToList();
            if (taxExpenses.Count > 0 || taxAssets.Count > 0 || taxLiabilities.Count > 0)
            {
                var tables = new List<NoteTable>();
                if (taxExpenses.Count > 0)
                {
                    var rows = taxExpenses.Select(e => new StatementRow(e.Account.Name, e.Current, e.Prior)).ToList();
                    rows.Add(new StatementRow("Income tax expense", tax, priorTax, RowStyle.Total));
                    tables.Add(new NoteTable("Income tax expense", rows));
                }
                if (taxAssets.Count > 0)
                    tables.Add(BalanceTable("Tax credits receivable", taxAssets, "Total tax credits receivable"));
                if (taxLiabilities.Count > 0)
                    tables.Add(BalanceTable("Current tax liabilities", taxLiabilities, "Total current tax liabilities"));
                taxNote = AddNote("Income tax", [], tables);
            }

            var assetRows = new List<StatementRow>();
            foreach (var g in assetGroups)
            {
                var note = g.Line == AssetLine.TaxCredits ? taxNote : AddNote(StatementWording.AssetLabel(g.Line),
                    AssetParagraphs(g.Line, AtMarketValue(g.Accounts)),
                    [g.Line == AssetLine.Investments
                        ? BalanceTable(AtMarketValue(g.Accounts) ? "Listed securities" : "Listed securities at cost", g.Accounts, "Total investments", a => StatementClassifier.InvestmentName(a.Name))
                        : BalanceTable(null, g.Accounts, "Total " + StatementWording.AssetLabel(g.Line).ToLowerInvariant())]);
                assetRows.Add(new StatementRow(StatementWording.AssetLabel(g.Line), g.Current, g.Prior, Note: note));
            }
            var assets = assetGroups.Sum(g => g.Current);
            var priorAssets = assetGroups.Sum(g => g.Prior);
            assetRows.Add(new StatementRow("Total assets", assets, priorAssets, RowStyle.Subtotal));

            var liabilityRows = new List<StatementRow>();
            foreach (var g in liabilityGroups)
            {
                var label = StatementWording.LiabilityLabel(g.Line, _type);
                var note = g.Line == LiabilityLine.TaxPayable ? taxNote
                    : AddNote(label, [], [BalanceTable(null, g.Accounts, "Total " + label.ToLowerInvariant())]);
                liabilityRows.Add(new StatementRow(label, g.Current, g.Prior, Note: note));
            }
            var liabilities = liabilityGroups.Sum(g => g.Current);
            var priorLiabilities = liabilityGroups.Sum(g => g.Prior);
            liabilityRows.Add(new StatementRow("Total liabilities", liabilities, priorLiabilities, RowStyle.Subtotal));

            // ---- Equity ----
            var (equityRows, equity, priorEquity) = BuildEquity(profit, priorProfit);

            var position = new PositionTotals(assets, liabilities, equity);
            var priorPosition = new PositionTotals(priorAssets, priorLiabilities, priorEquity);

            // ---- Statements ----
            var incomeBlocks = new List<StatementBlock>();
            var revenueRows = revenueGroups
                .Select(g => new StatementRow(StatementWording.RevenueLabel(g.Line), g.Current, g.Prior, Note: revenueNote))
                .ToList();
            revenueRows.Add(new StatementRow("Total revenue", revenue, priorRevenue, RowStyle.Subtotal));
            incomeBlocks.Add(new StatementBlock("Revenue", revenueRows));

            var expenseRows = operating
                .Select(e => new StatementRow(e.Account.Name, e.Current, e.Prior))
                .ToList();
            expenseRows.Add(new StatementRow("Total expenses", opex, priorOpex, RowStyle.Subtotal));
            incomeBlocks.Add(new StatementBlock("Expenses", expenseRows));

            var resultRows = new List<StatementRow>();
            if (taxExpenses.Count > 0)
            {
                resultRows.Add(new StatementRow(StatementWording.ProfitBeforeTax(_type), revenue - opex, priorRevenue - priorOpex, RowStyle.Subtotal));
                resultRows.Add(new StatementRow("Income tax expense", tax, priorTax, Note: taxNote));
            }
            resultRows.Add(new StatementRow(StatementWording.Profit(_type), profit, priorProfit, RowStyle.Total));
            incomeBlocks.Add(new StatementBlock(null, resultRows));

            var positionBlocks = new List<StatementBlock>
            {
                new("Assets", assetRows),
                new("Liabilities", liabilityRows),
                new(null, [new StatementRow(StatementWording.NetAssets(_type), position.NetAssets, priorPosition.NetAssets, RowStyle.Total)]),
                new(StatementWording.EquityHeading(_type), equityRows),
            };

            Checks(position, priorPosition, accountingMode);

            return new FinancialStatements(
                client, year,
                FinancialYear.StartDate(year), s2.Date, s1.Date,
                new Statement(StatementWording.IncomeStatementTitle(_type), incomeBlocks),
                new Statement(StatementWording.FinancialPositionTitle, positionBlocks),
                _notes, profit, priorProfit, position, priorPosition, _warnings);
        }

        /// <summary>A revaluation (Accounting > Market value revaluation) has posted to the adjustment account.</summary>
        private bool AtMarketValue(IEnumerable<LedgerBalance> accounts) =>
            accounts.Any(a => a.Number == Accounting.RevaluationService.AdjustmentAccount && (s2.Balance(a.Key) != 0 || s1.Balance(a.Key) != 0));

        private static IReadOnlyList<string> AssetParagraphs(AssetLine line, bool atMarketValue) => line switch
        {
            AssetLine.Investments when atMarketValue => ["Investments are shown at market value at year end, using closing prices. Each security's cost, including brokerage, is adjusted to market value through the market value adjustment account; changes in market value are shown as unrealised gains in the income statement."],
            AssetLine.Investments => ["Investments are shown at cost, including brokerage. Market values are not brought to account."],
            AssetLine.Clearing => ["Balances left in clearing or suspense accounts at year end, such as trades awaiting settlement or bank lines not yet allocated."],
            _ => [],
        };

        // ---- Equity by entity type ----

        private (List<StatementRow> Rows, decimal Equity, decimal PriorEquity) BuildEquity(decimal profit, decimal priorProfit)
        {
            var equityAccounts = OfType("Equity").ToList();
            List<LedgerBalance> Kind(params EquityKind[] kinds) =>
                equityAccounts.Where(a => kinds.Contains(StatementClassifier.ClassifyEquity(a.Number, a.Name))).ToList();

            var capital = Kind(EquityKind.Capital);
            var current = Kind(EquityKind.CurrentAccount);
            var retained = Kind(EquityKind.Retained);
            var paid = Kind(EquityKind.Distributions);
            var members = Kind(EquityKind.MemberBenefits);
            var other = Kind(EquityKind.Other);

            // Retained profits include every year's profit to the date, as no closing journals are posted
            decimal Retained(LedgerSnapshot s) => Sum(retained, s) + s.CumulativeProfit;
            decimal Total(LedgerSnapshot s) => Sum(equityAccounts, s) + s.CumulativeProfit;

            var rows = new List<StatementRow>();
            void Row(string label, Func<LedgerSnapshot, decimal> value, int? note = null, bool always = false)
            {
                var row = new StatementRow(label, value(s2), value(s1), Note: note);
                if (always || !row.IsZero)
                    rows.Add(row);
            }

            var paidWord = _type switch
            {
                ClientEntityTypes.Company => "Dividends paid",
                ClientEntityTypes.Trust => "Distributions to beneficiaries",
                ClientEntityTypes.Smsf => "Benefits paid",
                ClientEntityTypes.Partnership => "Partners' drawings",
                _ => "Drawings",
            };

            switch (_type)
            {
                case ClientEntityTypes.Company:
                case ClientEntityTypes.Trust:
                {
                    var company = _type == ClientEntityTypes.Company;
                    var capitalLabel = company ? "Issued capital" : "Settled sum and unitholders' capital";
                    var retainedLabel = company ? "Retained earnings" : "Undistributed income";
                    int? capitalNote = capital.Count > 0
                        ? AddNote(capitalLabel, [], [BalanceTable(null, capital, "Total " + capitalLabel.ToLowerInvariant())])
                        : null;
                    var retainedNote = AddNote(retainedLabel, [], [Movement(
                        s => Retained(s) + Sum(paid, s),
                        ("Profit/(loss) for the year", profit, priorProfit),
                        (paidWord, Movement(paid, s1, s2), Movement(paid, s0, s1)))]);
                    Row(capitalLabel, s => Sum(capital, s), capitalNote);
                    Row(retainedLabel, s => Retained(s) + Sum(paid, s), retainedNote, always: true);
                    Row("Other equity", s => Sum(current, s) + Sum(members, s) + Sum(other, s), OtherNote(current.Concat(members).Concat(other)));
                    break;
                }
                case ClientEntityTypes.Smsf:
                {
                    var note = AddNote("Liability for accrued benefits",
                        ["Changes in the liability for accrued benefits during the year. Contributions and rollovers credited directly to member accounts, and transfers between members, are shown as other movements."],
                        [Movement(Total,
                            (StatementWording.Profit(_type), profit, priorProfit),
                            (paidWord, Movement(paid, s1, s2), Movement(paid, s0, s1)))]);
                    foreach (var m in members)
                        Row(m.Name, s => s.Balance(m.Key), note);
                    Row(members.Count > 0 ? "Benefits accrued not yet allocated to members" : "Liability for accrued benefits",
                        s => Total(s) - Sum(members, s), note, always: true);
                    if (members.Count == 0)
                        _warnings.Add("No member benefit accounts were found (equity accounts named member, accumulation or benefit, or numbered 3400-3499), so the liability for accrued benefits isn't split by member.");
                    break;
                }
                case ClientEntityTypes.Partnership:
                {
                    int? capitalNote = capital.Count > 0
                        ? AddNote("Partners' capital accounts", [], [BalanceTable(null, capital, "Total partners' capital")])
                        : null;
                    var currentNote = AddNote("Partners' current accounts",
                        ["The profit for the year is shown before it is allocated to the partners' individual current accounts."],
                        [Movement(
                            s => Sum(current, s) + Retained(s) + Sum(paid, s),
                            ("Profit/(loss) for the year", profit, priorProfit),
                            (paidWord, Movement(paid, s1, s2), Movement(paid, s0, s1)))]);
                    Row("Partners' capital accounts", s => Sum(capital, s), capitalNote);
                    Row("Partners' current accounts", s => Sum(current, s) + Retained(s) + Sum(paid, s), currentNote, always: true);
                    Row("Other equity", s => Sum(members, s) + Sum(other, s), OtherNote(members.Concat(other)));
                    break;
                }
                default:
                {
                    // An individual's cash deposits and withdrawals post to the capital account (3100)
                    var capitalAccounts = capital.Concat(current).ToList();
                    var note = AddNote("Owner's equity",
                        ["Money paid in to the capital account is shown as capital introduced; money taken out of it, and any drawings account, as drawings."],
                        [Movement(Total,
                            ("Capital introduced", Credits(capitalAccounts, s2) - Credits(capitalAccounts, s1), Credits(capitalAccounts, s1) - Credits(capitalAccounts, s0)),
                            ("Profit/(loss) for the year", profit, priorProfit),
                            (paidWord, -(Debits(capitalAccounts, s2) - Debits(capitalAccounts, s1)) + Movement(paid, s1, s2),
                                       -(Debits(capitalAccounts, s1) - Debits(capitalAccounts, s0)) + Movement(paid, s0, s1)))]);
                    Row("Capital account", s => Sum(capitalAccounts, s), note);
                    Row("Retained profits", Retained, note, always: true);
                    Row("Drawings", s => Sum(paid, s), note);
                    Row("Other equity", s => Sum(members, s) + Sum(other, s), OtherNote(members.Concat(other)));
                    break;
                }
            }

            var equity = Total(s2);
            var priorEquity = Total(s1);
            rows.Add(new StatementRow(StatementWording.TotalEquity(_type), equity, priorEquity, RowStyle.Total));
            return (rows, equity, priorEquity);
        }

        private static decimal Movement(IEnumerable<LedgerBalance> accts, LedgerSnapshot from, LedgerSnapshot to) => Sum(accts, to) - Sum(accts, from);

        /// <summary>A note listing the equity accounts that went to the Other line, and a warning naming them.</summary>
        private int? OtherNote(IEnumerable<LedgerBalance> accts)
        {
            var list = accts.Where(a => s2.Balance(a.Key) != 0 || s1.Balance(a.Key) != 0).ToList();
            if (list.Count == 0)
                return null;
            _warnings.Add("These equity accounts are shown as \"Other equity\" because their name and number don't say what they are: "
                + string.Join(", ", list.Select(a => a.Number is { } n ? $"{n} {a.Name}" : a.Name)) + ".");
            return AddNote("Other equity", [], [BalanceTable(null, list, "Total other equity")]);
        }

        /// <summary>
        /// Opening balance, the named movements, any other movement that makes up the difference,
        /// and the closing balance, for both years.
        /// </summary>
        private NoteTable Movement(Func<LedgerSnapshot, decimal> balance, params (string Label, decimal Current, decimal Prior)[] parts)
        {
            var rows = new List<StatementRow> { new("Opening balance", balance(s1), balance(s0)) };
            rows.AddRange(parts.Select(p => new StatementRow(p.Label, p.Current, p.Prior)).Where(r => !r.IsZero));
            var close = balance(s2);
            var priorClose = balance(s1);
            var otherRow = new StatementRow("Other movements",
                close - balance(s1) - parts.Sum(p => p.Current),
                priorClose - balance(s0) - parts.Sum(p => p.Prior));
            if (!otherRow.IsZero)
                rows.Add(otherRow);
            rows.Add(new StatementRow("Closing balance", close, priorClose, RowStyle.Total));
            return new NoteTable(null, rows);
        }

        private void Checks(PositionTotals position, PositionTotals prior, string? accountingMode)
        {
            if (!client.Saved)
                _warnings.Add("The client details haven't been saved, so the name and entity type come from the portfolio. Check them on the Client details page.");
            if (!AccountingMode.PostsJournals(accountingMode))
                _warnings.Add("This portfolio uses single-entry accounting, so trades, cash transactions and payouts don't post to the ledger. These statements only include manual journals and imported bank statements.");

            foreach (var (s, totals) in new[] { (s2, position), (s1, prior) })
            {
                if (!totals.Reconciles)
                    _warnings.Add($"At {StatementFormat.Date(s.Date)} assets less liabilities ({StatementFormat.Money(totals.NetAssets)}) don't equal equity ({StatementFormat.Money(totals.Equity)}): they differ by {StatementFormat.Money(Math.Abs(totals.Difference))}. Some journals don't balance; check the trial balance.");
                else if (s.TotalDebit != s.TotalCredit)
                    _warnings.Add($"The trial balance at {StatementFormat.Date(s.Date)} doesn't balance: debits {StatementFormat.Money(s.TotalDebit)}, credits {StatementFormat.Money(s.TotalCredit)}.");
                if (s.UngroupedDifference != 0)
                    _warnings.Add($"At {StatementFormat.Date(s.Date)} some ledger accounts have a type other than Asset, Liability, Equity, Revenue or Expense, so the trial balance leaves them out ({StatementFormat.Money(Math.Abs(s.UngroupedDifference))} net). Fix their type in the chart of accounts.");
            }

            foreach (var a in _accounts.Where(a => a.Type is "Asset" or "Liability" && StatementClassifier.IsClearing(a.Name) && s2.Balance(a.Key) != 0))
                _warnings.Add($"{a.Name} has a balance of {StatementFormat.Money(s2.Balance(a.Key))} at year end. It is usually a trade without its settlement, or a bank line not yet allocated; clear it before finalising.");

            var investments = OfType("Asset").Where(a => StatementClassifier.ClassifyAsset(a.Number, a.Name) == AssetLine.Investments).ToList();
            foreach (var a in investments.Where(a => s2.Balance(a.Key) < 0))
                _warnings.Add($"{a.Name} has a credit balance of {StatementFormat.Money(-s2.Balance(a.Key))}.");
        }
    }
}
