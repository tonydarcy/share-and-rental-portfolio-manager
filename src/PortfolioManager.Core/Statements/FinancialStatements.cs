using PortfolioManager.Core.Clients;

namespace PortfolioManager.Core.Statements;

public enum RowStyle { Item, Heading, Subtotal, Total }

/// <summary>One line of a statement or note, with this year's and last year's amounts.</summary>
public sealed record StatementRow(string Label, decimal? Current, decimal? Prior, RowStyle Style = RowStyle.Item, int? Note = null, int Indent = 0)
{
    public bool IsHeading => Style == RowStyle.Heading;
    public bool IsZero => (Current ?? 0) == 0 && (Prior ?? 0) == 0;

    public static StatementRow Heading(string label) => new(label, null, null, RowStyle.Heading);
}

/// <summary>A titled part of a statement (e.g. Revenue, Assets) and its rows.</summary>
public sealed record StatementBlock(string? Heading, IReadOnlyList<StatementRow> Rows);

public sealed record Statement(string Title, IReadOnlyList<StatementBlock> Blocks)
{
    public IEnumerable<StatementRow> Rows => Blocks.SelectMany(b => b.Rows);
}

public sealed record NoteTable(string? Caption, IReadOnlyList<StatementRow> Rows);

public sealed record StatementNote(int Number, string Title, IReadOnlyList<string> Paragraphs, IReadOnlyList<NoteTable> Tables)
{
    /// <summary>Policy paragraphs that start with a heading in the form "Heading|text".</summary>
    public static (string? Heading, string Text) Split(string paragraph) =>
        paragraph.IndexOf('|') is var i and > 0 ? (paragraph[..i], paragraph[(i + 1)..]) : (null, paragraph);
}

/// <summary>Assets, liabilities and equity at one date. They agree when every journal balances.</summary>
public sealed record PositionTotals(decimal Assets, decimal Liabilities, decimal Equity)
{
    public decimal NetAssets => Assets - Liabilities;
    public decimal Difference => NetAssets - Equity;
    public bool Reconciles => Difference == 0;
}

/// <summary>A full set of financial statements for one client and financial year, with comparatives.</summary>
public sealed record FinancialStatements(
    ClientDetails Client,
    int Year,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly PriorPeriodEnd,
    Statement IncomeStatement,
    Statement FinancialPosition,
    IReadOnlyList<StatementNote> Notes,
    decimal Profit,
    decimal PriorProfit,
    PositionTotals Position,
    PositionTotals PriorPosition,
    IReadOnlyList<string> Warnings)
{
    public string YearEndText => $"30 June {Year}";
    public bool Reconciles => Position.Reconciles && PriorPosition.Reconciles;
    public bool IsEmpty => !IncomeStatement.Rows.Concat(FinancialPosition.Rows).Any(r => !r.IsHeading && !r.IsZero);
}
