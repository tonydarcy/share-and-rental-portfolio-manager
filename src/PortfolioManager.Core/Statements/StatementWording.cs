using PortfolioManager.Core.Clients;

namespace PortfolioManager.Core.Statements;

/// <summary>The words that change with the entity type: titles, totals and the accounting policies.</summary>
public static class StatementWording
{
    public static string IncomeStatementTitle(string type) =>
        type == ClientEntityTypes.Smsf ? "Operating statement" : "Detailed profit and loss statement";

    public const string FinancialPositionTitle = "Statement of financial position";

    public static string ProfitBeforeTax(string type) => type == ClientEntityTypes.Smsf
        ? "Benefits accrued as a result of operations before income tax"
        : "Profit/(loss) before income tax";

    public static string Profit(string type) => type == ClientEntityTypes.Smsf
        ? "Benefits accrued as a result of operations"
        : "Net profit/(loss) for the year";

    public static string NetAssets(string type) => type == ClientEntityTypes.Smsf
        ? "Net assets available to pay benefits"
        : "Net assets";

    public static string EquityHeading(string type) => type switch
    {
        ClientEntityTypes.Smsf => "Represented by: liability for accrued benefits",
        ClientEntityTypes.Partnership => "Partners' funds",
        ClientEntityTypes.Individual => "Owner's equity",
        _ => "Equity",
    };

    public static string TotalEquity(string type) => type switch
    {
        ClientEntityTypes.Smsf => "Total liability for accrued benefits",
        ClientEntityTypes.Partnership => "Total partners' funds",
        ClientEntityTypes.Individual => "Total owner's equity",
        _ => "Total equity",
    };

    /// <summary>"the company", "the trust", "the fund", "the partnership"; an individual is named.</summary>
    public static string EntityNoun(ClientDetails c) => c.EntityType switch
    {
        ClientEntityTypes.Company => "the company",
        ClientEntityTypes.Trust => "the trust",
        ClientEntityTypes.Smsf => "the fund",
        ClientEntityTypes.Partnership => "the partnership",
        _ => c.LegalName,
    };

    /// <summary>Who is responsible for the statements: the directors, the trustee(s), the partners, or the individual.</summary>
    public static string Responsible(ClientDetails c) => c.EntityType switch
    {
        ClientEntityTypes.Company => "the directors",
        ClientEntityTypes.Trust or ClientEntityTypes.Smsf when c.CorporateTrustee => "the directors of the trustee company",
        ClientEntityTypes.Trust => "the trustee",
        ClientEntityTypes.Smsf => "the trustees",
        ClientEntityTypes.Partnership => "the partners",
        _ => c.LegalName,
    };

    /// <summary>True when <see cref="Responsible"/> is one person (an individual, or a trust's individual trustee).</summary>
    public static bool ResponsibleIsSingular(ClientDetails c) =>
        c.EntityType == ClientEntityTypes.Individual || (c.EntityType == ClientEntityTypes.Trust && !c.CorporateTrustee);

    public static string AssetLabel(AssetLine line) => line switch
    {
        AssetLine.Cash => "Cash and cash equivalents",
        AssetLine.Receivables => "Trade and other receivables",
        AssetLine.TaxCredits => "Tax credits receivable",
        AssetLine.Investments => "Investments",
        AssetLine.PropertyPlant => "Property, plant and equipment",
        AssetLine.Clearing => "Clearing and suspense accounts",
        _ => "Other assets",
    };

    public static string LiabilityLabel(LiabilityLine line, string type) => line switch
    {
        LiabilityLine.Payables => "Trade and other payables",
        LiabilityLine.TaxPayable => "Current tax liabilities",
        LiabilityLine.DistributionsPayable => type switch
        {
            ClientEntityTypes.Company => "Dividends payable",
            ClientEntityTypes.Trust => "Beneficiary entitlements payable",
            ClientEntityTypes.Smsf => "Benefits payable",
            ClientEntityTypes.Partnership => "Amounts owing to partners",
            _ => "Distributions payable",
        },
        LiabilityLine.Borrowings => "Borrowings",
        LiabilityLine.Clearing => "Unsettled trades and suspense accounts",
        _ => "Other liabilities",
    };

    public static string RevenueLabel(RevenueLine line) => line switch
    {
        RevenueLine.Dividends => "Dividend income (including franking credits)",
        RevenueLine.Interest => "Interest income",
        RevenueLine.TrustDistributions => "Trust distributions",
        RevenueLine.Rent => "Rental income",
        RevenueLine.Gains => "Gains on disposal of investments",
        RevenueLine.Contributions => "Contributions and rollovers",
        _ => "Other income",
    };

    /// <summary>
    /// Note 1. Paragraphs are "Heading|text"; the first one is the special purpose framework
    /// statement for the entity type.
    /// </summary>
    public static IReadOnlyList<string> Policies(ClientDetails c, int year)
    {
        var basis = string.IsNullOrWhiteSpace(c.ReportingBasis) ? ClientDetails.DefaultReportingBasis : c.ReportingBasis.Trim();
        var who = Responsible(c);
        var Who = char.ToUpperInvariant(who[0]) + who[1..];
        var purpose = c.EntityType switch
        {
            ClientEntityTypes.Company => "to meet the requirements of the company's constitution and the needs of the directors and members, and for the purpose of preparing the company's income tax return",
            ClientEntityTypes.Trust => "to meet the requirements of the trust deed and the needs of the trustee and beneficiaries, and for the purpose of preparing the trust's income tax return",
            ClientEntityTypes.Smsf => "to meet the requirements of the Superannuation Industry (Supervision) Act 1993 and Regulations and the fund's trust deed, and the needs of the trustee and members",
            ClientEntityTypes.Partnership => "to meet the requirements of the partnership agreement and the needs of the partners, and for the purpose of preparing the partnership's income tax return",
            _ => $"for the use of {c.LegalName} and for the purpose of preparing their income tax return",
        };
        var nonReporting = c.EntityType == ClientEntityTypes.Individual
            ? $"{c.LegalName} is not a reporting entity because there are no users dependent on general purpose financial statements."
            : $"{Who} {(ResponsibleIsSingular(c) ? "has" : "have")} prepared the financial statements on the basis that {EntityNoun(c)} is a non-reporting entity because there are no users dependent on general purpose financial statements.";

        var list = new List<string>
        {
            $"Basis of preparation|{nonReporting} This {basis} has been prepared {purpose}. It is not intended for any other purpose and may not be suitable for one.",
            "Historical cost|The financial statements have been prepared from the double-entry ledger on the basis of historical cost. They do not take into account changing money values or, except where specifically stated, current valuations of non-current assets. The accounting policies below are consistent with the previous year unless stated otherwise.",
            "Investments|Investments in listed shares and other securities are carried at cost, including brokerage, and are not revalued to market value. A return of capital reduces the cost of the investment.",
            "Revenue|Dividends, trust distributions, interest and rent are brought to account when they are received or credited. Franking credits attached to dividends are included in dividend income and shown as a tax credit receivable until they are claimed.",
            "Cash and cash equivalents|Cash and cash equivalents comprise cash at bank and deposits at call.",
            "Income tax|" + c.EntityType switch
            {
                ClientEntityTypes.Trust => "Under current income tax legislation the trust is not liable to pay income tax, provided its taxable income is distributed to beneficiaries who are presently entitled to it. Accordingly no provision for income tax has been made, except where recorded in the ledger.",
                ClientEntityTypes.Partnership => "The partnership is not liable to pay income tax. Each partner is assessed on their share of the partnership's net income, so no provision for income tax has been made.",
                ClientEntityTypes.Individual => "Income tax is assessed on the individual's taxable income from all sources and is not brought to account in these statements unless it is recorded in the ledger.",
                ClientEntityTypes.Smsf => "The fund is expected to be a complying superannuation fund, so its taxable income is taxed at the concessional rate. Income tax expense, where recorded, is the tax payable on the taxable income for the year. Deferred tax is not brought to account.",
                _ => "Income tax expense, where recorded, is the tax payable on the taxable income for the year. Deferred tax balances are not brought to account.",
            },
        };
        if (c.EntityType == ClientEntityTypes.Smsf)
            list.Add("Liability for accrued benefits|The fund is a defined contribution fund. The liability for accrued benefits is the fund's present obligation to pay benefits to members and beneficiaries, measured as the net assets available to pay benefits at the end of the year.");
        list.Add($"Comparative figures|Comparative figures are for the year ended 30 June {year - 1}.");
        return list;
    }
}
