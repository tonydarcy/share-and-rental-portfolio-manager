using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Inventory;

/// <summary>A stock item or price list entry. For a tracked item, Quantity and TotalCost are what is on hand now.</summary>
public sealed record InventoryItem(
    int ItemId,
    string Code,
    string Name,
    string? Description,
    bool Tracked,
    decimal? SalePrice,
    decimal? PurchasePrice,
    int? SalesAccountId,
    int? PurchaseAccountId,
    int? InventoryAccountId,
    int? CogsAccountId,
    int? SalesTaxCodeId,
    int? PurchaseTaxCodeId,
    bool Active,
    decimal Quantity,
    decimal TotalCost)
{
    public decimal? AverageCost => Tracked && Quantity > 0 ? Math.Round(TotalCost / Quantity, 4) : null;
    public string Display => $"{Code} - {Name}";
}

/// <summary>Stock in (positive) or out (negative), with its cost in the portfolio's currency.</summary>
public sealed record StockMovement(int MovementId, int ItemId, DateOnly Date, string Source, int SourceId, string? Document, decimal Quantity, decimal Cost);

public sealed record StockAdjustment(int AdjustmentId, DateOnly Date, string? Note, int AccountId, int? JournalId, int Items, decimal Cost);

/// <summary>One line of a stocktake: what the books say was on hand at the date and what was counted.</summary>
public sealed class StocktakeLine
{
    public int ItemId { get; set; }
    public decimal OnHand { get; set; }
    public decimal? Counted { get; set; }

    /// <summary>For stock found that the books didn't have: its cost each (blank uses the average cost).</summary>
    public decimal? UnitCost { get; set; }

    public decimal Change => Counted is { } c ? c - OnHand : 0;
}

/// <summary>The add / edit item form.</summary>
public sealed class ItemInput
{
    public int ItemId { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool Tracked { get; set; } = true;
    public decimal? SalePrice { get; set; }
    public decimal? PurchasePrice { get; set; }
    public int? SalesAccountId { get; set; }
    public int? PurchaseAccountId { get; set; }
    public int? InventoryAccountId { get; set; }
    public int? CogsAccountId { get; set; }
    public int? SalesTaxCodeId { get; set; }
    public int? PurchaseTaxCodeId { get; set; }
    public bool Active { get; set; } = true;

    public static ItemInput From(InventoryItem i) => new()
    {
        ItemId = i.ItemId, Code = i.Code, Name = i.Name, Description = i.Description, Tracked = i.Tracked, SalePrice = i.SalePrice,
        PurchasePrice = i.PurchasePrice, SalesAccountId = i.SalesAccountId, PurchaseAccountId = i.PurchaseAccountId,
        InventoryAccountId = i.InventoryAccountId, CogsAccountId = i.CogsAccountId, SalesTaxCodeId = i.SalesTaxCodeId,
        PurchaseTaxCodeId = i.PurchaseTaxCodeId, Active = i.Active,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Code) || Code.Trim().Length > 30)
            errors.Add("Enter a code of up to 30 characters, such as WIDGET-1.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200)
            errors.Add("Enter the item's name, up to 200 characters.");
        if ((Description?.Length ?? 0) > 500)
            errors.Add("The description can be at most 500 characters.");
        if (SalePrice < 0 || PurchasePrice < 0)
            errors.Add("Prices can't be negative.");
        if (Tracked && (InventoryAccountId is not > 0 || CogsAccountId is not > 0))
            errors.Add("A tracked item needs an inventory account and a cost of goods sold account.");
        return errors;
    }
}

public static class InventoryRules
{
    /// <summary>The quantity on hand at the end of a date, from the movements.</summary>
    public static decimal OnHandAt(IEnumerable<StockMovement> movements, int itemId, DateOnly date) =>
        movements.Where(m => m.ItemId == itemId && m.Date <= date).Sum(m => m.Quantity);

    /// <summary>The stocktake's changes as the XML uspSaveStockAdjustment reads; counts that match are left out.</summary>
    public static string AdjustmentXml(IEnumerable<StocktakeLine> lines) =>
        new XElement("items", lines.Where(l => l.Counted is not null && l.Change != 0).Select(l => new XElement("i",
            new XAttribute("id", l.ItemId),
            new XAttribute("q", l.Change.ToString(CultureInfo.InvariantCulture)),
            l.Change > 0 && l.UnitCost is { } c ? new XAttribute("c", c.ToString(CultureInfo.InvariantCulture)) : null))).ToString(SaveOptions.DisableFormatting);
}

/// <summary>Items, stock on hand and stocktakes (2026-10-21-24-inventory.sql). Stock moves when invoices and bills are approved.</summary>
public sealed class InventoryService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<InventoryItem>> GetItemsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync("SELECT * FROM tblInventoryItems WHERE iUserID = @portfolioId ORDER BY strCode", new { portfolioId });
            return rows.Cast<IDictionary<string, object?>>().Select(Map).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public static InventoryItem Map(IDictionary<string, object?> r) => new(
        Accounting.Db.Int(r, "iItemID") ?? 0,
        Accounting.Db.Str(r, "strCode"),
        Accounting.Db.Str(r, "strName"),
        Accounting.Db.Str(r, "strDescription") is { Length: > 0 } d ? d : null,
        Accounting.Db.Bool(r, "bTracked") ?? false,
        Accounting.Db.Dec(r, "dblSalePrice"),
        Accounting.Db.Dec(r, "dblPurchasePrice"),
        Accounting.Db.Int(r, "iSalesAccountID"),
        Accounting.Db.Int(r, "iPurchaseAccountID"),
        Accounting.Db.Int(r, "iInventoryAccountID"),
        Accounting.Db.Int(r, "iCogsAccountID"),
        Accounting.Db.Int(r, "iSalesTaxCodeID"),
        Accounting.Db.Int(r, "iPurchaseTaxCodeID"),
        Accounting.Db.Bool(r, "bActive") ?? true,
        Accounting.Db.Dec(r, "dblQuantity") ?? 0,
        Accounting.Db.Dec(r, "dblTotalCost") ?? 0);

    public async Task<ProcResult> SaveItemAsync(int portfolioId, ItemInput input)
    {
        if (input.Validate() is [var first, ..])
            return ProcResult.Failed(first);
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveInventoryItem", new
        {
            iUserId = portfolioId, iItemID = input.ItemId, strCode = input.Code!.Trim(), strName = input.Name!.Trim(), strDescription = input.Description,
            bTracked = input.Tracked, dblSalePrice = input.SalePrice, dblPurchasePrice = input.PurchasePrice,
            iSalesAccountID = input.SalesAccountId is > 0 ? input.SalesAccountId : null,
            iPurchaseAccountID = input.PurchaseAccountId is > 0 ? input.PurchaseAccountId : null,
            iInventoryAccountID = input.InventoryAccountId, iCogsAccountID = input.CogsAccountId,
            iSalesTaxCodeID = input.SalesTaxCodeId, iPurchaseTaxCodeID = input.PurchaseTaxCodeId, bActive = input.Active,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteItemAsync(int portfolioId, int itemId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteInventoryItem",
            new { iUserId = portfolioId, iItemID = itemId }, commandType: CommandType.StoredProcedure), "Success");
    }

    /// <summary>Every stock movement (or one item's), oldest first, with the invoice or bill it came from.</summary>
    public async Task<IReadOnlyList<StockMovement>> GetMovementsAsync(int portfolioId, int? itemId = null)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, int ItemId, DateTime Date, string Source, int SourceId, string? Document, decimal Qty, decimal Cost)>("""
                SELECT M.iMovementID, M.iItemID, M.dtmDate, M.strSource, M.iSourceID,
                       CASE M.strSource WHEN 'Invoice' THEN (SELECT strInvoiceNumber FROM tblInvoices WHERE iInvoiceID = M.iSourceID)
                                        WHEN 'Bill' THEN (SELECT ISNULL(B.strBillNumber, '#' + CAST(B.iBillID AS VARCHAR(20))) + ' - ' + S.strName
                                                          FROM tblBills B JOIN tblSuppliers S ON S.iSupplierID = B.iSupplierID WHERE B.iBillID = M.iSourceID)
                                        ELSE (SELECT CAST(strNote AS VARCHAR(500)) FROM tblStockAdjustments WHERE iAdjustmentID = M.iSourceID) END,
                       M.dblQuantity, M.dblCost
                FROM tblInventoryMovements M
                WHERE M.iUserID = @portfolioId AND (@itemId IS NULL OR M.iItemID = @itemId)
                ORDER BY M.dtmDate, CASE WHEN M.dblQuantity > 0 THEN 0 ELSE 1 END, M.iMovementID
                """, new { portfolioId, itemId });
            return rows.Select(r => new StockMovement(r.Id, r.ItemId, DateOnly.FromDateTime(r.Date), r.Source, r.SourceId, r.Document, r.Qty, r.Cost)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<StockAdjustment>> GetAdjustmentsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            var rows = await con.QueryAsync<(int Id, DateTime Date, string? Note, int Account, int? Journal, int Items, decimal Cost)>("""
                SELECT A.iAdjustmentID, A.dtmDate, A.strNote, A.iAccountID, A.iJournalID,
                       (SELECT COUNT(*) FROM tblInventoryMovements M WHERE M.strSource = 'Adjust' AND M.iSourceID = A.iAdjustmentID),
                       ISNULL((SELECT SUM(M.dblCost) FROM tblInventoryMovements M WHERE M.strSource = 'Adjust' AND M.iSourceID = A.iAdjustmentID), 0)
                FROM tblStockAdjustments A WHERE A.iUserID = @portfolioId ORDER BY A.dtmDate DESC, A.iAdjustmentID DESC
                """, new { portfolioId });
            return rows.Select(r => new StockAdjustment(r.Id, DateOnly.FromDateTime(r.Date), r.Note, r.Account, r.Journal, r.Items, r.Cost)).ToList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return [];
        }
    }

    /// <summary>Posts a stocktake's differences (or any adjustment) on the date against <paramref name="accountId"/>.</summary>
    public async Task<ProcResult> AdjustAsync(int portfolioId, DateOnly date, int accountId, string? note, IEnumerable<StocktakeLine> lines)
    {
        var xml = InventoryRules.AdjustmentXml(lines);
        if (!xml.Contains("<i "))
            return ProcResult.Failed("Nothing to adjust: every count matches what is on hand.");
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveStockAdjustment", new
        {
            iUserId = portfolioId, dtDate = date.ToDateTime(TimeOnly.MinValue), iAccountID = accountId,
            strNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), xmlItems = xml,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public async Task<ProcResult> DeleteAdjustmentAsync(int portfolioId, int adjustmentId)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>("uspDeleteStockAdjustment",
            new { iUserId = portfolioId, iAdjustmentID = adjustmentId }, commandType: CommandType.StoredProcedure), "Success");
    }
}
