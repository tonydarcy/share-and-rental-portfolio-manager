namespace PortfolioManager.Core.Backup;

/// <summary>A column holding the id of a row in another table that is copied too, so it is renumbered on restore.</summary>
/// <param name="Later">Set at the end, once every table is in: for a table that points at itself or at one restored after it.</param>
public sealed record IdColumn(string Column, string Table, bool Later = false);

/// <summary>
/// A column whose id points at a different table depending on another column, like a journal's source
/// (tblJournal.strSourceModule) or an attachment's kind. A kind in <see cref="Tables"/> with a null table, or one
/// not listed, isn't an id (a year, or nothing), so it is kept as it is or cleared.
/// </summary>
public sealed record SourceColumn(string Column, string KindColumn, IReadOnlyDictionary<string, string?> Tables)
{
    /// <summary>The table a kind points at; null when the value isn't a row id.</summary>
    public string? TableFor(string? kind) => kind is not null && Tables.TryGetValue(kind.Trim(), out var t) ? t : null;

    /// <summary>Kinds listed with no table keep their value (a year); kinds not listed at all lose it.</summary>
    public bool Keeps(string? kind) => kind is not null && Tables.TryGetValue(kind.Trim(), out var t) && t is null;
}

/// <summary>One table a backup copies.</summary>
public sealed record BackupTable(string Name)
{
    /// <summary>The column holding the portfolio id, for a table whose rows belong to a portfolio directly.</summary>
    public string? Owner { get; init; }

    /// <summary>The identity column, renumbered on restore.</summary>
    public string? Identity { get; init; }

    /// <summary>For a table whose rows hang off another table's rows: the column pointing at the parent.</summary>
    public string? ParentColumn { get; init; }
    public string? Parent { get; init; }

    public IReadOnlyList<IdColumn> Ids { get; init; } = [];
    public IReadOnlyList<SourceColumn> Sources { get; init; } = [];

    /// <summary>Lines kept as XML (<c>&lt;l a="account" t="tax code" i="item" t1="option" t2="option"/&gt;</c>).</summary>
    public IReadOnlyList<string> LineXml { get; init; } = [];

    /// <summary>Shared by every portfolio on the site (share names): only added when the site hasn't got that row.</summary>
    public IReadOnlyList<string> SharedKey { get; init; } = [];

    /// <summary>Extra filter for a shared table, as SQL with @p for the portfolio id and alias x for the table.</summary>
    public string? SharedFilter { get; init; }

    /// <summary>
    /// For a table with site-wide rows too (owner 0, like the standard chart of accounts): those rows are matched on
    /// these columns to the restoring site's own, and only added to the new portfolio when it hasn't got them.
    /// </summary>
    public IReadOnlyList<string> SiteRowKey { get; init; } = [];

    /// <summary>Locked periods go in last, as the lock triggers would refuse anything dated before them.</summary>
    public bool Last { get; init; }
}

/// <summary>
/// The tables a portfolio backup holds and how their ids join up, in the order they are restored. Logins, sharing,
/// API keys, connections to other systems (their tokens are secrets), audit trails and site-wide prices are left out.
/// When a migration adds a table or an id column that a portfolio owns, add it here (BackupPlanTests checks the shape).
/// </summary>
public static class BackupPlan
{
    private const string Accounts = "tblChartOfAccounts", TaxCodes = "tblTaxCodes", Options = "tblTrackingOptions",
        Trades = "tblShareTrades", Journals = "tblJournal", Lines = "tblJournalLines", Items = "tblInventoryItems",
        Invoices = "tblInvoices", Bills = "tblBills";

    private static BackupTable Owned(string name, string? identity = null, string owner = "iUserID") =>
        new(name) { Owner = owner, Identity = identity };

    private static BackupTable Child(string name, string parentColumn, string parent, string? identity = null) =>
        new(name) { ParentColumn = parentColumn, Parent = parent, Identity = identity };

    private static IdColumn[] Ids(params (string Column, string Table)[] ids) => ids.Select(i => new IdColumn(i.Column, i.Table)).ToArray();

    private static IdColumn[] Tracking => Ids(("iTrackingOption1ID", Options), ("iTrackingOption2ID", Options));

    private static IdColumn[] LineIds(params (string Column, string Table)[] more) =>
        [.. Ids(("iAccountID", Accounts), ("iTaxCodeID", TaxCodes), ("iItemID", Items)), .. Tracking, .. Ids(more)];

    /// <summary>tblJournal.strSourceModule: which table iSourceID points at. Null = the id is a year, kept as is.</summary>
    public static readonly IReadOnlyDictionary<string, string?> JournalSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["ShareTrade"] = Trades,
        ["BankTransaction"] = "tblBankStatement",
        ["Payout"] = "tblPayouts",
        ["ReturnOfCapital"] = "tblReturnsOfCapital",
        ["CorporateAction"] = "tblCorporateActions",
        ["LoanEntry"] = "tblLoanEntries",
        ["FundStatement"] = "tblFundTaxStatements",
        ["AssetDisposal"] = "tblFixedAssets",
        ["Depreciation"] = "tblDepreciationRuns",
        ["Bill"] = Bills,
        ["BillPayment"] = "tblBillPayments",
        ["Invoice"] = Invoices,
        ["InvoiceCost"] = Invoices,
        ["InvoicePayment"] = "tblInvoicePayments",
        ["ExpenseClaim"] = "tblExpenseClaims",
        ["FxRevaluation"] = "tblFxRevaluations",
        ["FxRevaluationReversal"] = "tblFxRevaluations",
        ["StockAdjustment"] = "tblStockAdjustments",
        ["TradingStock"] = null,
        ["InterestAccrual"] = null,
    };

    /// <summary>tblAttachments.strKind: which table iTargetID points at (Client and Year files have no target).</summary>
    public static readonly IReadOnlyDictionary<string, string?> AttachmentTargets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Trade"] = Trades,
        ["Journal"] = Journals,
        ["Workpaper"] = "tblWorkpapers",
        ["Query"] = "tblClientQueries",
        ["Bill"] = Bills,
        ["ExpenseClaim"] = "tblExpenseClaims",
        ["Property"] = "tblRentalProperties",
        ["RentalJob"] = "tblRentalJobs",
        ["Inspection"] = "tblRentalInspections",
    };

    public static readonly IReadOnlyList<BackupTable> Tables =
    [
        // The ledger's setup first: most other tables point at accounts, tax codes and tracking options
        Owned(Accounts, "iAccountID") with
        {
            Ids = [new IdColumn("iParentAccountID", Accounts, Later: true)],
            SiteRowKey = ["iAccountNumber", "strAccountName"],
        },
        Owned(TaxCodes, "iTaxCodeID"),
        Owned("tblTrackingCategories", "iCategoryID"),
        Child(Options, "iCategoryID", "tblTrackingCategories", "iOptionID"),
        Owned("tblCustomGroups", "iGroupID"),
        Owned("tblCustomers", "iCustomerID"),
        Owned("tblSuppliers", "iSupplierID") with { Ids = Ids(("iDefaultAccountID", Accounts), ("iDefaultTaxCodeID", TaxCodes)) },
        Owned(Items, "iItemID") with
        {
            Ids = Ids(("iSalesAccountID", Accounts), ("iPurchaseAccountID", Accounts), ("iInventoryAccountID", Accounts),
                ("iCogsAccountID", Accounts), ("iSalesTaxCodeID", TaxCodes), ("iPurchaseTaxCodeID", TaxCodes)),
        },

        // Shares, cash and income
        Owned(Trades, "iShareTradeId") with { Ids = [new IdColumn("iBankStatementId", "tblBankStatement", Later: true)] },
        Owned("tblBankStatement", "iBankStatementId") with { Ids = Ids(("iShareTradeId", Trades)) },
        Child("tblShareTradeGroups", "iShareTradeID", Trades, "iTradeGroupID") with { Ids = Ids(("iGroupID", "tblCustomGroups")) },
        Owned("tblPayouts", "iPayoutID") with { Ids = Ids(("iReinvestmentTradeID", Trades)) },
        Owned("tblCorporateActions", "iActionID"),
        Child("tblCorporateActionTrades", "iActionID", "tblCorporateActions") with { Ids = Ids(("iShareTradeId", Trades)) },
        Owned("tblReturnsOfCapital", "iReturnOfCapitalID"),
        Owned("tblParcelTransfers", "iParcelTransferID") with { Ids = Ids(("iBuyTradeID", Trades), ("iSellTradeID", Trades)) },
        Owned("tblSaleParcelAllocation") with { Ids = Ids(("iSellTradeID", Trades), ("iBuyTradeID", Trades)) },
        Owned("tblSaleParcelChoice") with { Ids = Ids(("iSellTradeID", Trades)) },
        Owned("tblCGTParcelMatching", "iParcelID") with { Ids = Ids(("iBuyTradeID", Trades), ("iSellTradeID", Trades)) },
        Owned("tblSharePrice", "iSharePriceId"),
        Owned("tblSharePriceCache") with { Ids = Ids(("iSharePriceId", "tblSharePrice")) },
        new("tblShareDetails")
        {
            SharedKey = ["strStockExchangeCode", "strStockCode"],
            SharedFilter = "EXISTS (SELECT 1 FROM tblShareTrades t WHERE t.iUserId = @p AND t.strStockExchangeCode = x.strStockExchangeCode AND t.strStockCode = x.strStockCode)",
        },
        Owned("tblFrankingEntry", "iEntryID"),
        Owned("tblFixedIncome", "iFixedIncomeID"),
        Owned("tblFundExposure", "iFundExposureID"),
        Owned("tblFundTaxStatements", "iFundTaxStatementID"),
        Owned("tblEssGrants", "iEssGrantID"),
        Owned("tblEssStatements", "iEssStatementID"),
        Owned("tblShareTrader"),
        Owned("tblShareTraderHoldings"),
        Owned("tblTaxAdjustment", "iAdjustmentID"),
        Owned("tblTaxYear"),
        Owned("tblTrustBeneficiary", "iBeneficiaryID"),
        Owned("tblSmsfMember", "iMemberID"),
        Owned("tblSmsfMemberAccount", "iMemberAccountID") with { Ids = Ids(("iMemberID", "tblSmsfMember")) },
        Owned("tblSmsfYearExtras"),
        Owned("tblTbarEvents", "iEventID") with { Ids = Ids(("iMemberID", "tblSmsfMember")) },
        Owned("tblDailyPortfolioHistory", "iHistoryID"),
        Owned("tblPortfolioValueSnapshot", "iSnapshotID"),

        // The ledger
        Owned("tblQuotesOrders", "iDocumentID") with
        {
            Ids = Ids(("iCustomerID", "tblCustomers"), ("iSupplierID", "tblSuppliers")),
            Sources = [new SourceColumn("iConvertedID", "strKind", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                { ["Quote"] = Invoices, ["PurchaseOrder"] = Bills })],
        },
        Child("tblQuoteOrderLines", "iDocumentID", "tblQuotesOrders", "iLineID") with { Ids = LineIds() },
        Owned(Journals, "iJournalID") with { Sources = [new SourceColumn("iSourceID", "strSourceModule", JournalSources)] },
        Child(Lines, "iJournalID", Journals, "iJournalLineID") with
        {
            Ids = [.. Ids(("iAccountID", Accounts), ("iTaxCodeID", TaxCodes), ("iMatchedJournalID", Journals)), .. Tracking],
        },
        Owned(Invoices, "iInvoiceID") with
        {
            Ids = [.. Ids(("iCustomerID", "tblCustomers"), ("iJournalID", Journals)), new IdColumn("iCreditedInvoiceID", Invoices, Later: true)],
        },
        Child("tblInvoiceLines", "iInvoiceID", Invoices, "iInvoiceLineID") with { Ids = LineIds() },
        Child("tblInvoicePayments", "iInvoiceID", Invoices, "iPaymentID") with
        {
            Ids = [.. Ids(("iAccountID", Accounts), ("iJournalID", Journals), ("iCreditNoteID", Invoices), ("iSuspenseLineID", Lines)),
                new IdColumn("iPairPaymentID", "tblInvoicePayments", Later: true)],
        },
        Owned(Bills, "iBillID") with
        {
            Ids = Ids(("iSupplierID", "tblSuppliers"), ("iPurchaseOrderID", "tblQuotesOrders"), ("iJournalID", Journals)),
        },
        Child("tblBillLines", "iBillID", Bills, "iBillLineID") with { Ids = LineIds() },
        Child("tblBillPayments", "iBillID", Bills, "iPaymentID") with
        {
            Ids = [.. Ids(("iAccountID", Accounts), ("iJournalID", Journals), ("iCreditID", Bills)),
                new IdColumn("iPairPaymentID", "tblBillPayments", Later: true)],
        },
        Owned("tblRepeatingInvoices", "iRepeatingInvoiceID") with { Ids = Ids(("iCustomerID", "tblCustomers")), LineXml = ["strLinesXml"] },
        Owned("tblRepeatingBills", "iRepeatingBillID") with { Ids = Ids(("iSupplierID", "tblSuppliers")), LineXml = ["strLinesXml"] },
        Owned("tblRecurringJournals", "iRecurringJournalID") with { LineXml = ["strLinesXml"] },
        Owned("tblStockAdjustments", "iAdjustmentID") with { Ids = Ids(("iAccountID", Accounts), ("iJournalID", Journals)) },
        Owned("tblInventoryMovements", "iMovementID") with
        {
            Ids = Ids(("iItemID", Items)),
            Sources =
            [
                new SourceColumn("iSourceID", "strSource", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    { ["Invoice"] = Invoices, ["Bill"] = Bills, ["Adjust"] = "tblStockAdjustments" }),
                new SourceColumn("iLineID", "strSource", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    { ["Invoice"] = "tblInvoiceLines", ["Bill"] = "tblBillLines" }),
            ],
        },
        Owned("tblFxRevaluations", "iRevaluationID") with { Ids = Ids(("iJournalID", Journals), ("iReversalJournalID", Journals)) },
        Owned("tblForeignCurrencyAccounts") with { Ids = Ids(("iAccountID", Accounts)) },
        Owned("tblLoans", "iLoanID") with { Ids = Ids(("iAccountID", Accounts)) },
        Child("tblLoanEntries", "iLoanID", "tblLoans", "iLoanEntryID"),
        Owned("tblFixedAssets", "iAssetID") with
        {
            Ids = Ids(("iAssetAccountID", Accounts), ("iAccumAccountID", Accounts), ("iExpenseAccountID", Accounts),
                ("iProceedsAccountID", Accounts), ("iDisposalJournalID", Journals)),
        },
        Owned("tblDepreciationRuns", "iRunID") with { Ids = Ids(("iJournalID", Journals)) },
        Child("tblDepreciationEntries", "iRunID", "tblDepreciationRuns") with { Ids = Ids(("iAssetID", "tblFixedAssets")) },
        Owned("tblBudgets", "iBudgetID") with { Ids = Ids(("iTrackingOptionID", Options)) },
        Child("tblBudgetLines", "iBudgetID", "tblBudgets") with { Ids = Ids(("iAccountID", Accounts)) },
        Owned("tblBankReconciliation", "iReconciliationID") with { Ids = Ids(("iAccountID", Accounts)) },
        Child("tblBankReconciliationLine", "iReconciliationID", "tblBankReconciliation") with { Ids = Ids(("iJournalLineID", Lines)) },
        Owned("tblAllocationRule", "iRuleID") with { Ids = Ids(("iAccountID", Accounts), ("iTaxCodeID", TaxCodes)) },
        Owned("tblLedgerExportMap", "iMapID") with { Ids = Ids(("iAccountID", Accounts)) },
        Owned("tblExpenseClaims", "iClaimID") with
        {
            Ids = [.. Ids(("iAccountID", Accounts), ("iTaxCodeID", TaxCodes), ("iJournalID", Journals), ("iPaymentJournalID", Journals)), .. Tracking],
        },
        Owned("tblSalesTaxSetup") with { Ids = Ids(("iControlAccountID", Accounts)) },
        Owned("tblBasSettings"),
        Owned("tblBasLabels"),
        Owned("tblInvoiceSettings"),

        // Rental properties
        Owned("tblRentalProperties", "iPropertyID"),
        Child("tblRentalAmounts", "iPropertyID", "tblRentalProperties"),
        Child("tblRentalAssets", "iPropertyID", "tblRentalProperties", "iAssetID"),
        Child("tblRentalCompliance", "iPropertyID", "tblRentalProperties", "iItemID"),
        Child("tblRentalTenancies", "iPropertyID", "tblRentalProperties", "iTenancyID"),
        Child("tblRentalInspections", "iPropertyID", "tblRentalProperties", "iInspectionID") with { Ids = Ids(("iTenancyID", "tblRentalTenancies")) },
        Child("tblRentalJobs", "iPropertyID", "tblRentalProperties", "iJobID"),
        Child("tblRentalLoanLinks", "iPropertyID", "tblRentalProperties") with { Ids = Ids(("iLoanID", "tblLoans")) },
        Child("tblRentalOwners", "iPropertyID", "tblRentalProperties", "iOwnerID"),
        Child("tblRentalRentPayments", "iTenancyID", "tblRentalTenancies", "iPaymentID"),
        Child("tblRentalSales", "iPropertyID", "tblRentalProperties"),
        Child("tblRentalTenantBills", "iPropertyID", "tblRentalProperties", "iBillID") with { Ids = Ids(("iTenancyID", "tblRentalTenancies")) },

        // Client file, year end and imports
        Owned("tblClientDetails"),
        Owned("tblClientOfficeholders", "iOfficeholderID"),
        Owned("tblClientQueries", "iQueryID") with { Ids = Ids(("iJournalID", Journals), ("iJournalLineID", Lines)) },
        Owned("tblWorkpapers", "iWorkpaperID") with { Ids = Ids(("iAccountID", Accounts)) },
        Owned("tblYearChecklist"),
        Owned("tblYearStatus"),
        Owned("tblPrefillItems", "iItemID"),
        Owned("tblNavexaMapping", owner: "iUserId") with { Ids = Ids(("iAccountID", Accounts)) },
        Owned("tblNavexaSyncItem", owner: "iUserId"),
        Owned("tblAttachments", "iAttachmentID") with { Sources = [new SourceColumn("iTargetID", "strKind", AttachmentTargets)] },

        Owned("tblLockedPeriod") with { Last = true },
        Owned("tblLockedPeriodHistory", "iHistoryID") with { Last = true },
    ];

    /// <summary>Tables a backup leaves out on purpose, and why (BackupPlanTests checks every table is one or the other).</summary>
    public static readonly IReadOnlyDictionary<string, string> LeftOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tblUsers"] = "the portfolio's own row: a restore makes a new one and copies only its settings",
        ["tblPortfolioPermissions"] = "sharing belongs to logins on one site",
        ["tblPortfolioInvites"] = "sharing belongs to logins on one site",
        ["tblFamilyGroups"] = "a login's grouping of portfolios",
        ["tblFamilyGroupMembers"] = "a login's grouping of portfolios",
        ["tblPracticeDetails"] = "the accountant login's own details",
        ["tblOnboarding"] = "a practice's invitation links",
        ["tblApiKeys"] = "secrets for one site",
        ["tblApiAuditLog"] = "audit trail of one site",
        ["tblAuditLog"] = "audit trail of one site",
        ["tblSecurityEvents"] = "sign-in history of one site",
        ["tblTermsAcceptances"] = "each site has its own terms",
        ["tblLog"] = "site log",
        ["tblAssistantSettings"] = "a login's Anthropic key (a secret)",
        ["tblAssistantChats"] = "a login's assistant chats",
        ["tblAlertSettings"] = "a login's email alerts",
        ["tblPriceAlerts"] = "a login's email alerts",
        ["tblRentalAlertsSent"] = "which alerts were emailed",
        ["tblAnnouncedActionDismissals"] = "points at the site's own announcements",
        ["tblAccountingConnections"] = "Xero and MYOB sign-ins (secrets)",
        ["tblAccountingSends"] = "history of sends to Xero and MYOB",
        ["tblNavexaConnection"] = "a saved Navexa key (a secret)",
        ["tblAnnouncedDividends"] = "site-wide feed",
        ["tblAnnouncedSplits"] = "site-wide feed",
        ["tblMarketIndexHistory"] = "site-wide prices",
        ["tblShareGroups"] = "site-wide index lists",
        ["tblProfitSummary"] = "old working table",
    };

    public static BackupTable? Find(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Columns holding a login (who prepared, approved or changed something). Logins aren't copied, so on restore
    /// these name the person restoring.
    /// </summary>
    public static bool IsLoginColumn(string column) =>
        column.EndsWith("ByUserID", StringComparison.OrdinalIgnoreCase)
        || column.EndsWith("ByLoginID", StringComparison.OrdinalIgnoreCase)
        || column.Equals("iClaimantUserID", StringComparison.OrdinalIgnoreCase);

    /// <summary>The portfolio's own settings (tblUsers) a backup carries. Its name, login and password never are.</summary>
    public static readonly IReadOnlyList<string> SettingColumns =
    [
        "strEntityType", "strPortfolioCurrency", "strTaxResidency", "strAccountingMode", "strCountryCode",
        "iYearStartMonth", "iYearStartDay", "strNumberFormat",
    ];

    /// <summary>
    /// SQL selecting the ids of a table's rows that belong to the portfolio (@p), following parents up to the owner.
    /// Built only from this plan's fixed names, never from a file.
    /// </summary>
    public static string RowFilter(BackupTable table, string alias)
    {
        if (table.Owner is not null)
            return table.SiteRowKey.Count > 0 ? $"{alias}.[{table.Owner}] IN (0, @p)" : $"{alias}.[{table.Owner}] = @p";
        if (table.SharedFilter is not null)
            return table.SharedFilter.Replace(" x.", $" {alias}.");
        var parent = Find(table.Parent!) ?? throw new InvalidOperationException($"{table.Name}: no parent {table.Parent}");
        var pa = alias + "p";
        return $"{alias}.[{table.ParentColumn}] IN (SELECT {pa}.[{parent.Identity}] FROM [{parent.Name}] {pa} WHERE {RowFilter(parent, pa)})";
    }
}
