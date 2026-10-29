using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.TaxWork;

namespace PortfolioManager.Web.Api;

/// <summary>
/// The tools for rental properties. A property reaches its rental schedule through its payout code: rental entries
/// (add_rental_entry, or a bank line allocated with allocate_bank_transaction_to_property) carrying that code fill it.
/// </summary>
public sealed partial class AgentTools
{
    private static List<AgentTool> BuildProperty() =>
    [
        new("list_rental_properties", "List rental properties",
            "The portfolio's rental properties (Property > Rental properties), with their ids and payout codes. Rental entries with a "
            + "property's payout code fill its rental schedule; a property with no payout code gets nothing from them.",
            false, new ToolSchema().Portfolio().Build(),
            ListProperties),

        new("save_rental_property", "Add or change a rental property",
            "Adds a rental property (leave out propertyId) or changes one; an edit takes only what changes. Set payoutCode to the short "
            + "code to use as propertyCode on its rental entries, e.g. 12SMITH.",
            true, new ToolSchema().Portfolio()
                .Int("propertyId", "The property to change, from list_rental_properties (leave out to add one)")
                .String("address", "The property's address (required for a new property)")
                .String("payoutCode", "Short code its rental entries use, e.g. 12SMITH")
                .Number("ownershipPct", "The portfolio owner's share, 0 to 100 (default 100)")
                .Date("firstEarned", "When it was first rented out")
                .Bool("active", "Still held and rented (default true)")
                .String("note", "Note to keep with the property").Build(),
            SaveProperty),

        new("allocate_bank_transaction_to_property", "Allocate a bank transaction to a rental property",
            "Records an unallocated bank line as a rental entry for a property and matches the line to it, so the amounts show on that "
            + "property's rental schedule and in the ledger (rental income and rental property expenses) with the cash counted once. "
            + "Give the whole property's amounts (not the owner's share), positive, split as on the statement: for a deposit, rent less "
            + "any expenses taken out must equal the deposit (e.g. an agent's payment of rent less fees); for a withdrawal, the expenses "
            + "less any rent must equal it. The entry takes the line's date.",
            true, new ToolSchema().Portfolio()
                .Int("lineId", "The line, from list_unallocated", required: true)
                .String("propertyCode", "The property's payout code, from list_rental_properties", required: true)
                .String("comment", "Note to keep with the entry (default: the bank's description)")
                .Object("amounts", "The amounts by name; leave out the ones that are zero", RentalAmountsSchema(), required: true).Build(),
            AllocateToProperty),
    ];

    private static async Task<object?> ListProperties(AgentCall c) =>
        (await c.Service<RentalService>().GetPropertiesAsync(c.PortfolioId)).Select(p => new
        {
            PropertyId = p.Id,
            p.Address,
            p.PayoutCode,
            p.OwnershipPct,
            p.FirstEarned,
            p.Active,
            p.Note,
            CoOwners = p.Others.Select(o => new { o.Name, o.Pct }),
        }).ToList();

    private static async Task<object?> SaveProperty(AgentCall c)
    {
        var rentals = c.Service<RentalService>();
        var id = c.Int("propertyId") ?? 0;
        RentalProperty? stored = null;
        if (id != 0)
            stored = await rentals.GetPropertyAsync(c.PortfolioId, id)
                ?? throw new AgentToolException(404, "There is no rental property with that id in this portfolio. list_rental_properties gives the ids.");

        var address = c.String("address") ?? stored?.Address;
        if (string.IsNullOrWhiteSpace(address))
            throw new AgentToolException(400, "address is required for a new property.");
        var pct = c.Decimal("ownershipPct") ?? stored?.OwnershipPct ?? 100;
        if (pct is < 0 or > 100)
            throw new AgentToolException(400, "ownershipPct must be from 0 to 100.");

        var saved = await rentals.SavePropertyAsync(c.PortfolioId, id, address.Trim(), c.Date("firstEarned") ?? stored?.FirstEarned, pct,
            c.Bool("active", stored?.Active ?? true), c.String("note") ?? stored?.Note);
        if (!saved.Ok)
            throw new AgentToolException(400, saved.Error!);
        id = id != 0 ? id : saved.Id ?? throw new AgentToolException(500, "The new property's id wasn't returned.");

        if (c.String("payoutCode") is { } code)
        {
            code = code.Trim().ToUpperInvariant();
            if (code.Length is 0 or > 50)
                throw new AgentToolException(400, "payoutCode must be 1 to 50 characters.");
            var details = await rentals.SaveDetailsAsync(c.PortfolioId, id, code, stored?.State, stored?.Value, stored?.Valued);
            if (!details.Ok)
                throw new AgentToolException(400, details.Error!);
        }

        var property = await rentals.GetPropertyAsync(c.PortfolioId, id);
        return new { Message = stored is null ? "Added the property." : "Updated the property.", PropertyId = id, property?.Address, property?.PayoutCode };
    }

    private static async Task<object?> AllocateToProperty(AgentCall c)
    {
        var lineId = c.RequiredInt("lineId");
        var input = new RentalPayoutInput
        {
            PropertyCode = c.String("propertyCode") ?? "",
            ExchangeCode = "PROP",
            Comment = c.String("comment"),
        };
        SetRentalAmounts(c, input);
        if (input.Validate() is { Count: > 0 } errors)
            throw new AgentToolException(400, string.Join(" ", errors));
        if (!await HasPayoutCodeAsync(c, input.PropertyCode))
            throw new AgentToolException(400, NotLinked(input.PropertyCode));

        var imports = c.Service<BankImportService>();
        if (input.Comment is null && (await imports.GetUnallocatedAsync(c.PortfolioId)).FirstOrDefault(l => l.LineId == lineId) is { } line)
            input.Comment = line.Description.Length > 500 ? line.Description[..500] : line.Description;
        var result = await imports.AddRentalAndMatchAsync(c.PortfolioId, lineId, input);
        return result.Ok
            ? new { Message = $"Recorded the {input.PropertyCode.Trim().ToUpperInvariant()} rental entry, net {input.Net:N2}, and matched the bank line to it.", Amounts = input.NamedAmounts() }
            : throw new AgentToolException(400, result.Error!);
    }

    /// <summary>Whether a rental property has this payout code, so entries with it reach its schedule.</summary>
    private static async Task<bool> HasPayoutCodeAsync(AgentCall c, string code) =>
        (await c.Service<RentalService>().GetPropertiesAsync(c.PortfolioId))
            .Any(p => string.Equals(p.PayoutCode, code.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string NotLinked(string code) =>
        $"No rental property has the payout code {code.Trim().ToUpperInvariant()}, so entries with it don't reach a rental schedule. "
        + "list_rental_properties gives the codes; save_rental_property adds a property or sets its code.";
}
