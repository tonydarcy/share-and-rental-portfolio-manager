using System.Text.Json;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.Bills;
using PortfolioManager.Core.Cash;
using PortfolioManager.Core.Clients;
using PortfolioManager.Core.CorporateActions;
using PortfolioManager.Core.Data;
using PortfolioManager.Core.Email;
using PortfolioManager.Core.Invoicing;
using PortfolioManager.Core.Payouts;
using PortfolioManager.Core.Prices;
using PortfolioManager.Core.SalesTax;
using PortfolioManager.Core.Tax;
using PortfolioManager.Core.Trades;
using PortfolioManager.Core.Validation;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Api;

/// <summary>
/// The tools for payouts, cash, corporate actions, journals, invoices, bills and manual prices. Each does what its page
/// does, through the same Core service, and an edit takes only the fields that change (the rest keep their stored values).
/// </summary>
public sealed partial class AgentTools
{
    private static List<AgentTool> BuildBooks() =>
    [
        new("edit_payout", "Edit a payout",
            "Changes a dividend, distribution or interest payout (from list_payouts); send only what changes. Its journal is "
            + "posted again. The franking credit isn't worked out for you: send it too when the franked amount changes. A DRP payout keeps its code and amounts (they belong with the units it bought). For a rental entry use edit_rental_entry.",
            true, PayoutFields(new ToolSchema().Portfolio().Int("payoutId", "The payout, from list_payouts", required: true), required: false).Build(),
            EditPayout),

        new("add_drp", "Add a dividend reinvestment (DRP)",
            "Records a dividend paid as new units: the payout and the buy of the units at the DRP price together, as the Add DRP page does. "
            + "The units times the price must come within one unit's price of the dividend less tax withheld.",
            true, new ToolSchema().Portfolio()
                .Date("date", "Payment date", required: true)
                .String("stockExchangeCode", "Stock exchange, e.g. ASX", required: true)
                .String("stockCode", "Stock code, e.g. BHP", required: true)
                .Number("franked", "Franked amount")
                .Number("unfranked", "Unfranked amount")
                .Number("frankingCredit", "Franking credit")
                .Number("taxWithheld", "TFN amounts withheld")
                .Number("units", "New units allotted", required: true)
                .Number("price", "DRP price per unit", required: true)
                .String("comment", "Note to keep with the payout").Build(),
            AddDrp),

        new("add_rental_entry", "Add a rental entry",
            "Records rent and property expenses for a period (a rental payout), as the Add rental payout page does, posting its journal. "
            + "Amounts are the whole property's, positive; expenses are taken off the rent. The entry fills the rental schedule of the "
            + "property whose payout code is propertyCode (list_rental_properties). Its journal puts the net through the bank, so for cash "
            + "that came through an imported bank statement use allocate_bank_transaction_to_property instead. For an entry no cash moved "
            + "for (depreciation, capital works, borrowing costs written off, interest charged straight to the loan), give offsetAccountId: "
            + "the journal then goes against that account (e.g. accumulated depreciation, or the property loan) instead of the bank. "
            + "Depreciation and capital works entered here are added to any worked out from the property's assets on the site.",
            true, new ToolSchema().Portfolio()
                .Date("date", "Date of the entry", required: true)
                .String("propertyCode", "The property's payout code, e.g. 12SMITH", required: true)
                .String("exchangeCode", "The property's exchange code (default PROP)")
                .String("comment", "Note to keep with the entry")
                .Int("offsetAccountId", "For a non-cash entry: the account for the other side instead of the bank, from list_ledger_accounts")
                .Object("amounts", "The amounts by name; leave out the ones that are zero", RentalAmountsSchema(), required: true).Build(),
            AddRental),

        new("edit_rental_entry", "Edit a rental entry",
            "Changes a rental entry (from list_payouts); send only what changes. Amounts sent replace those amounts; the others stay. Its journal is posted again, against the same bank or offset account.",
            true, new ToolSchema().Portfolio()
                .Int("payoutId", "The rental entry, from list_payouts", required: true)
                .Date("date", "Date of the entry")
                .String("propertyCode", "The property's short code")
                .String("comment", "Note to keep with the entry")
                .Object("amounts", "Amounts to change, by name (0 clears one)", RentalAmountsSchema()).Build(),
            EditRental),

        new("edit_cash_transaction", "Edit a cash transaction",
            "Changes a deposit or withdrawal entered by hand (from list_cash_transactions); send only what changes. It is deleted and "
            + "recorded again in one step, posting a fresh journal, so it gets a new id (returned). A trade's settlement is changed through edit_trade.",
            true, new ToolSchema().Portfolio()
                .Int("cashTransactionId", "The entry, from list_cash_transactions", required: true)
                .Date("date", "Transaction date")
                .String("direction", "Deposit or Withdrawal", choices: ["Deposit", "Withdrawal"])
                .Number("amount", "Amount, more than zero")
                .String("bankAccount", "Bank account name")
                .String("currency", "Currency, e.g. AUD")
                .String("comment", "Description").Build(),
            EditCash),

        new("delete_cash_transaction", "Delete a cash transaction",
            "Deletes a deposit or withdrawal entered by hand, with its journal. A trade's settlement (delete or edit the trade instead) "
            + "and an entry ticked off on a bank reconciliation are refused.",
            true, new ToolSchema().Portfolio().Int("cashTransactionId", "The entry, from list_cash_transactions", required: true).Build(),
            DeleteCash),

        new("list_corporate_actions", "List corporate actions",
            "Share splits and consolidations (with whether each can be undone), returns of capital, and demergers, takeovers and code changes.",
            false, new ToolSchema().Portfolio().Build(),
            ListCorporateActions),

        new("add_split", "Record a share split or consolidation",
            "Records a split (2 for 1: newUnits 2, oldUnits 1) or consolidation (1 for 10: newUnits 1, oldUnits 10). Every earlier trade "
            + "in the holding is changed to the new units; the cost base and purchase dates stay the same. With preview true nothing is saved.",
            true, new ToolSchema().Portfolio()
                .String("market", "Stock exchange, e.g. ASX", required: true)
                .String("code", "Stock code", required: true)
                .Date("date", "First day the shares traded on the new basis (the ex-date)", required: true)
                .Int("newUnits", "New units", required: true)
                .Int("oldUnits", "For this many old units", required: true)
                .Bool("preview", "Only show the units and cost base before and after").Build(),
            AddSplit),

        new("delete_split", "Undo a share split",
            "Undoes a split or consolidation (from list_corporate_actions) and deletes it, putting the trades back as they were.",
            true, new ToolSchema().Portfolio().Int("actionId", "The split, from list_corporate_actions", required: true).Build(),
            DeleteSplit),

        new("add_return_of_capital", "Record a return of capital",
            "Records a return of capital (an amount per unit or a total), reducing each parcel's cost base and posting its journal. "
            + "Any amount above a parcel's cost base is a capital gain (CGT event G1). With preview true nothing is saved.",
            true, new ToolSchema().Portfolio()
                .String("market", "Stock exchange, e.g. ASX", required: true)
                .String("code", "Stock code", required: true)
                .Date("date", "Payment date", required: true)
                .Number("perUnit", "Amount per unit (or give total)")
                .Number("total", "Total received (or give perUnit)")
                .String("comment", "Note to keep with it")
                .Bool("preview", "Only show the cost base reduction and any gain").Build(),
            AddCapitalReturn),

        new("delete_return_of_capital", "Delete a return of capital",
            "Deletes a return of capital (from list_corporate_actions) and its journal.",
            true, new ToolSchema().Portfolio().Int("returnId", "The return of capital, from list_corporate_actions", required: true).Build(),
            DeleteCapitalReturn),

        new("list_journals", "List journals",
            "Ledger journals, newest first, with where each came from (manual, trade, payout, bank...). Only manual ones can be changed.",
            false, new ToolSchema().Portfolio()
                .Date("from", "Only journals on or after this date")
                .Date("to", "Only journals on or before this date")
                .Bool("manualOnly", "Only manual journals")
                .Int("limit", $"Most rows to return (default {DefaultLimit}).").Build(),
            ListJournals),

        new("get_journal", "Get a journal",
            "One journal with its lines (account, debit, credit, tax).",
            false, new ToolSchema().Portfolio().Int("journalId", "The journal, from list_journals", required: true).Build(),
            async c => await c.Service<JournalService>().GetAsync(c.PortfolioId, c.RequiredInt("journalId"))
                ?? throw new AgentToolException(404, "There is no journal with that id in this portfolio.")),

        new("save_journal", "Add or change a manual journal",
            "Posts a manual journal, or with journalId replaces a manual journal's date, description and lines. Debits must equal credits; "
            + "accountId comes from list_ledger_accounts and taxCodeId from list_tax_codes (the amount then includes the tax).",
            true, new ToolSchema().Portfolio()
                .Int("journalId", "A manual journal to change (leave out for a new one)")
                .Date("date", "Journal date (required for a new journal)")
                .String("description", "Description (required for a new journal)")
                .Array("lines", "The lines (required for a new journal; replace all lines when changing one)", new ToolSchema()
                    .Int("accountId", "Ledger account", required: true)
                    .String("description", "Line description (default: the journal's)")
                    .Number("debit", "Debit amount")
                    .Number("credit", "Credit amount")
                    .Int("taxCodeId", "Tax code")
                    .Number("tax", "The tax in the amount, when it isn't the code's rate")).Build(),
            SaveJournal),

        new("delete_journal", "Delete a manual journal",
            "Deletes a manual journal. Journals posted for trades, payouts, cash, invoices or imports are refused: change those records instead.",
            true, new ToolSchema().Portfolio().Int("journalId", "The journal, from list_journals", required: true).Build(),
            DeleteJournal),

        new("list_customers", "List customers",
            "Customers for invoices, with their ids, email and what they owe.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<InvoiceService>().GetCustomersAsync(c.PortfolioId)),

        new("save_customer", "Add or change a customer",
            "Adds a customer for invoices, or with customerId changes one (send only what changes).",
            true, new ToolSchema().Portfolio()
                .Int("customerId", "A customer to change (leave out for a new one)")
                .String("name", "Name (required for a new customer)")
                .String("contactName", "Contact person")
                .String("email", "Email address invoices are sent to")
                .String("phone", "Phone")
                .String("address", "Address")
                .String("taxNumber", "ABN or other tax number")
                .Bool("isActive", "Whether the customer is offered for new invoices").Build(),
            SaveCustomer),

        new("save_invoice", "Create or change an invoice",
            "Creates an invoice, or with invoiceId changes one (send only what changes; lines sent replace all lines). With approve true "
            + "it is approved and posted to the ledger; otherwise a new invoice stays a draft. accountId is an income account (list_ledger_accounts).",
            true, DocumentSchema(new ToolSchema().Portfolio()
                .Int("invoiceId", "An invoice to change (leave out for a new one)")
                .Int("customerId", "The customer, from list_customers (required for a new invoice)")).Build(),
            SaveInvoice),

        new("send_invoice", "Email an invoice",
            "Emails an approved invoice to the customer (or the address given), as the Email button on the invoice does. Replies go to "
            + "the email address of the login the key belongs to. Needs the site's email settings.",
            true, new ToolSchema().Portfolio()
                .Int("invoiceId", "The invoice, from list_invoices", required: true)
                .String("to", "Email address (default: the customer's)")
                .String("note", "A short note above the invoice").Build(),
            SendInvoice),

        new("add_invoice_payment", "Record an invoice payment",
            "Records money received for an approved invoice and posts it from Accounts Receivable to the bank account.",
            true, PaymentSchema("invoiceId", "The invoice, from list_invoices").Build(),
            async c => Saved(await c.Service<InvoiceService>().AddPaymentAsync(c.PortfolioId, c.RequiredInt("invoiceId"), Payment(c)),
                "Payment recorded and posted to the bank account.")),

        new("list_suppliers", "List suppliers",
            "Suppliers for bills, with their ids, default account and payment terms.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<BillService>().GetSuppliersAsync(c.PortfolioId)),

        new("save_supplier", "Add or change a supplier",
            "Adds a supplier for bills, or with supplierId changes one (send only what changes).",
            true, new ToolSchema().Portfolio()
                .Int("supplierId", "A supplier to change (leave out for a new one)")
                .String("name", "Name (required for a new supplier)")
                .String("contactName", "Contact person")
                .String("email", "Email")
                .String("phone", "Phone")
                .String("address", "Address")
                .String("taxNumber", "ABN or other tax number")
                .String("paymentDetails", "Their bank details for paying them")
                .Int("defaultAccountId", "Expense account their bills usually go to")
                .Int("defaultTaxCodeId", "Tax code their bills usually use")
                .Int("dueDays", "Days until their bills are due")
                .Bool("isActive", "Whether the supplier is offered for new bills").Build(),
            SaveSupplier),

        new("list_bills", "List bills",
            "Bills and supplier credits with supplier, dates, totals, amount paid and status.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<BillService>().GetBillsAsync(c.PortfolioId)),

        new("get_bill", "Get a bill",
            "One bill with its lines and payments.",
            false, new ToolSchema().Portfolio().Int("billId", "The bill, from list_bills", required: true).Build(),
            async c => await c.Service<BillService>().GetBillAsync(c.PortfolioId, c.RequiredInt("billId"))
                ?? throw new AgentToolException(404, "There is no bill with that id in this portfolio.")),

        new("save_bill", "Create or change a bill",
            "Creates a bill, or with billId changes one (send only what changes; lines sent replace all lines). With approve true it is "
            + "approved and posted to the ledger; otherwise a new bill stays a draft. accountId is an expense or asset account (list_ledger_accounts).",
            true, DocumentSchema(new ToolSchema().Portfolio()
                .Int("billId", "A bill to change (leave out for a new one)")
                .Int("supplierId", "The supplier, from list_suppliers (required for a new bill)")
                .String("kind", "Bill or SupplierCredit (default Bill)", choices: [BillKinds.Bill, BillKinds.SupplierCredit])
                .String("number", "The supplier's bill number")).Build(),
            SaveBill),

        new("add_bill_payment", "Record a bill payment",
            "Records money paid on an approved bill and posts it from Accounts Payable to the bank account.",
            true, PaymentSchema("billId", "The bill, from list_bills").Build(),
            async c => Saved(await c.Service<BillService>().AddPaymentAsync(c.PortfolioId, c.RequiredInt("billId"), Payment(c)),
                "Payment recorded and posted from the bank account.")),

        new("list_manual_prices", "List manual prices",
            "The prices this portfolio keeps itself, with their ids: those entered by hand for holdings with no price feed (managed funds, unlisted shares, property, other) and those recorded with its trades.",
            false, new ToolSchema().Portfolio().Build(),
            async c => await c.Service<ManualPriceService>().GetAsync(c.PortfolioId)),

        new("set_manual_price", "Set a manual price",
            $"Saves a price for a holding on a market with no price feed ({string.Join(", ", AssetTypes.ManualMarkets.Order())}), replacing any price on that date.",
            true, new ToolSchema().Portfolio()
                .String("market", "The holding's market, e.g. FUND", required: true)
                .String("code", "The holding's code", required: true)
                .Date("date", "Price date", required: true)
                .Number("price", "Price per unit", required: true).Build(),
            SetManualPrice),

        new("delete_manual_price", "Delete a manual price",
            "Deletes a price entered by hand (from list_manual_prices).",
            true, new ToolSchema().Portfolio().Int("priceId", "The price, from list_manual_prices", required: true).Build(),
            async c => Saved(await c.Service<ManualPriceService>().DeleteAsync(c.PortfolioId, c.RequiredInt("priceId")), "Price deleted.")),
    ];

    private static ToolSchema PayoutFields(ToolSchema s, bool required) => s
        .Date("date", "Payment date", required)
        .String("stockExchangeCode", "Stock exchange, e.g. ASX", required)
        .String("stockCode", "Stock code, e.g. BHP", required)
        .Number("total", "Cash received", required)
        .Number("franked", "Franked amount")
        .Number("unfranked", "Unfranked amount")
        .Number("frankingCredit", "Franking credit (not part of the cash)")
        .Number("interest", "Interest")
        .Number("trustIncome", "Trust income")
        .Number("other", "Other income")
        .Number("taxWithheld", "TFN amounts withheld")
        .Number("foreignTax", "Foreign tax paid")
        .String("comment", "Note to keep with the payout")
        .Bool("confirmed", "Whether the amounts are confirmed from a statement");

    private static ToolSchema RentalAmountsSchema()
    {
        var s = new ToolSchema();
        foreach (var f in RentalPayoutInput.Fields)
            s.Number(RentalPayoutInput.Key(f), f.Label + (f.IsIncome ? " (income)" : ""));
        return s;
    }

    private static ToolSchema DocumentSchema(ToolSchema s) => s
        .Date("issueDate", "Issue date (default today)")
        .Date("dueDate", "Due date (default: from the payment terms)")
        .String("reference", "Reference")
        .String("notes", "Notes")
        .Bool("amountsIncludeTax", "Whether line prices include tax")
        .String("currency", "Another currency's code, if not the portfolio's")
        .Number("exchangeRate", "Units of that currency per unit of portfolio currency")
        .Array("lines", "The lines (required for a new one)", new ToolSchema()
            .String("description", "Description", required: true)
            .Number("quantity", "Quantity (default 1)")
            .Number("unitPrice", "Price per unit", required: true)
            .Int("accountId", "Ledger account", required: true)
            .Int("taxCodeId", "Tax code, from list_tax_codes")
            .Int("itemId", "Inventory item"))
        .Bool("approve", "Approve it and post it to the ledger");

    private static ToolSchema PaymentSchema(string idName, string idDescription) => new ToolSchema().Portfolio()
        .Int(idName, idDescription, required: true)
        .Date("date", "Date paid", required: true)
        .Number("amount", "Amount paid", required: true)
        .Int("accountId", "The bank's ledger account, from list_ledger_accounts", required: true)
        .String("reference", "Reference")
        .Number("exchangeRate", "For one in another currency: the rate on the day it was paid");

    private static object Saved(ProcResult result, string message) =>
        result.Ok ? new { Message = message, result.Id } : throw new AgentToolException(400, result.Error ?? "Nothing was saved.");

    private static InvoicePaymentInput Payment(AgentCall c)
    {
        var p = c.Bind<InvoicePaymentInput>();
        if (c.Date("date") is null)
            throw new AgentToolException(400, "date is required.");
        return p;
    }

    private static async Task<object?> EditPayout(AgentCall c)
    {
        var payoutId = c.RequiredInt("payoutId");
        var payouts = c.Service<PayoutService>();
        var record = await payouts.GetAsync(c.PortfolioId, payoutId)
            ?? throw new AgentToolException(404, "There is no payout with that id in this portfolio. list_payouts gives the ids.");
        if (record.IsRental)
            throw new AgentToolException(400, "That is a rental entry. Use edit_rental_entry.");

        var input = c.BindOver(PayoutEdit.FromRecord(record));
        // As on the page, which has no net field: a changed total is also the net (kept amounts bring the stored net back)
        input.Net = null;
        var (keepAmounts, errors) = PayoutEdit.Prepare(record, input);
        if (errors.Count > 0)
            throw new AgentToolException(400, string.Join(" ", errors));
        if (!await payouts.UpdateAsync(c.PortfolioId, payoutId, input, keepAmounts))
            throw new AgentToolException(404, "There is no payout with that id in this portfolio.");
        return new
        {
            Message = $"Updated the {input.StockCode.Trim().ToUpperInvariant()} payout and posted its journal again."
                + (record.ReinvestmentTradeId is null ? "" : " It is a DRP payout, so its code and amounts were kept."),
            Payout = input,
        };
    }

    private static async Task<object?> AddDrp(AgentCall c)
    {
        var input = c.Bind<DrpInput>();
        if (c.Date("date") is null)
            throw new AgentToolException(400, "date is required.");
        if (input.Validate() is { Count: > 0 } errors)
            throw new AgentToolException(400, string.Join(" ", errors));

        // As the Add DRP page: there must be units to pay the dividend on
        var market = input.StockExchangeCode.Trim().ToUpperInvariant();
        var code = input.StockCode.Trim().ToUpperInvariant();
        var held = CgtParcels.Match(await c.Service<CgtDiscountService>().LoadTradesAsync(c.PortfolioId), DateOnly.FromDateTime(input.Date))
            .Open.Where(p => p.ExchangeCode.Equals(market, StringComparison.OrdinalIgnoreCase) && p.StockCode.Equals(code, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Units);
        if (held <= 0)
            throw new AgentToolException(400, $"No {code} units on {market} were held in this portfolio on {input.Date:yyyy-MM-dd}, so there was nothing to pay a dividend on.");

        var result = await c.Service<PayoutService>().AddDrpAsync(c.PortfolioId, input);
        if (!result.Ok)
            throw new AgentToolException(400, result.Error!);
        return new
        {
            Message = $"Recorded the {code} DRP: a dividend of {input.Cash:N2} reinvested in {input.Units:#,##0.########} units at {input.Price:0.####}"
                + (input.Residual == 0 ? "." : $", with {input.Residual:N2} left over."),
            Drp = input,
        };
    }

    private static async Task<object?> AddRental(AgentCall c)
    {
        var input = new RentalPayoutInput
        {
            PropertyCode = c.String("propertyCode") ?? "",
            ExchangeCode = c.String("exchangeCode") ?? "PROP",
            Date = (c.Date("date") ?? throw new AgentToolException(400, "date is required.")).ToDateTime(TimeOnly.MinValue),
            Comment = c.String("comment"),
        };
        SetRentalAmounts(c, input);
        if (input.Validate() is { Count: > 0 } errors)
            throw new AgentToolException(400, string.Join(" ", errors));

        var payouts = c.Service<PayoutService>();
        int? payoutId = null;
        if (c.Int("offsetAccountId") is { } offset)
        {
            var accounts = c.Service<ChartOfAccountsService>();
            if ((await accounts.GetJournalAccountsAsync(c.PortfolioId)).All(a => a.AccountId != offset))
                throw new AgentToolException(400, "That offset account isn't one of this portfolio's. list_ledger_accounts gives the ids.");
            if (!AccountingMode.PostsJournals(await accounts.GetAccountingModeAsync(c.PortfolioId)))
                throw new AgentToolException(400, "This portfolio uses single-entry accounting, so rental entries post no journal to offset. Leave out offsetAccountId.");
            payoutId = await payouts.AddRentalAsync(c.PortfolioId, input, offset);
        }
        else
            await payouts.AddRentalAsync(c.PortfolioId, input);
        var code = input.PropertyCode.Trim().ToUpperInvariant();
        return new
        {
            Message = $"Recorded the {code} rental entry, net {input.Net:N2}" + (payoutId is null ? "." : $", against the offset account (payout {payoutId}).")
                + (await HasPayoutCodeAsync(c, code) ? "" : " " + NotLinked(code)),
            Amounts = input.NamedAmounts(),
        };
    }

    private static async Task<object?> EditRental(AgentCall c)
    {
        var payoutId = c.RequiredInt("payoutId");
        var payouts = c.Service<PayoutService>();
        var input = await payouts.GetRentalAsync(c.PortfolioId, payoutId)
            ?? throw new AgentToolException(404, "There is no rental entry with that id in this portfolio. list_payouts gives the ids.");
        input.PropertyCode = c.String("propertyCode") ?? input.PropertyCode;
        input.Date = c.Date("date")?.ToDateTime(TimeOnly.MinValue) ?? input.Date;
        input.Comment = c.String("comment") ?? input.Comment;
        SetRentalAmounts(c, input);
        if (input.Validate() is { Count: > 0 } errors)
            throw new AgentToolException(400, string.Join(" ", errors));

        if (!await payouts.UpdateRentalAsync(c.PortfolioId, payoutId, input))
            throw new AgentToolException(404, "There is no rental entry with that id in this portfolio.");
        return new { Message = $"Updated the {input.PropertyCode.Trim().ToUpperInvariant()} rental entry, now net {input.Net:N2}.", Amounts = input.NamedAmounts() };
    }

    private static void SetRentalAmounts(AgentCall c, RentalPayoutInput input)
    {
        if (c.Arg("amounts") is not { } amounts)
            return;
        Dictionary<string, decimal> named;
        try
        {
            named = amounts.Deserialize<Dictionary<string, decimal>>(AgentJson.Options) ?? [];
        }
        catch (JsonException)
        {
            throw new AgentToolException(400, "amounts must be an object of names and plain numbers, e.g. {\"rentalIncome\": 2400}.");
        }
        if (input.SetAmounts(named) is [_, ..] unknown)
            throw new AgentToolException(400, $"Unknown amount name(s): {string.Join(", ", unknown)}. The names are: {string.Join(", ", RentalPayoutInput.Fields.Select(RentalPayoutInput.Key))}.");
    }

    private static async Task<object?> EditCash(AgentCall c)
    {
        var id = c.RequiredInt("cashTransactionId");
        var cash = c.Service<CashService>();
        var stored = await cash.GetTransactionAsync(c.PortfolioId, id)
            ?? throw new AgentToolException(404, "There is no cash transaction with that id in this portfolio. list_cash_transactions gives the ids.");
        if (stored.TradeId is > 0)
            throw new AgentToolException(409, $"This is the cash settlement of trade {stored.TradeId}. Change it with edit_trade.");

        var result = CashEntry.Validate(c.BindOver(CashService.ToForm(stored)), FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } entry)
            throw AgentToolException.Invalid(result.Errors);
        entry = entry with { Currency = CashEntry.ResolveCurrency(entry.Currency, entry.BankAccount, await cash.GetBankAccountsAsync(c.PortfolioId)) };

        var saved = await cash.ReplaceAsync(c.PortfolioId, id, entry);
        if (!saved.Ok)
            throw new AgentToolException(409, $"The cash transaction wasn't changed. {saved.Error}");
        return new
        {
            Message = $"Changed cash transaction {id}: now a {entry.Direction.ToString().ToLowerInvariant()} of {entry.Amount:N2} on {entry.Date:yyyy-MM-dd}. "
                + $"Its journal was posted again and its new id is {saved.Id}.",
            OldCashTransactionId = id,
            NewCashTransactionId = saved.Id,
            CashTransaction = entry,
        };
    }

    private static async Task<object?> DeleteCash(AgentCall c)
    {
        var id = c.RequiredInt("cashTransactionId");
        var result = await c.Service<CashService>().DeleteAsync(c.PortfolioId, id);
        return result.Ok
            ? new { Message = $"Deleted cash transaction {id} and its journal." }
            : throw new AgentToolException(result.Error?.Contains("not found") == true ? 404 : 409, $"The cash transaction wasn't deleted. {result.Error}");
    }

    private static async Task<object?> ListCorporateActions(AgentCall c)
    {
        var actions = c.Service<CorporateActionsService>();
        return new
        {
            Splits = (await actions.GetSplitsAsync(c.PortfolioId)).Select(s => new
            {
                s.ActionId, s.ExchangeCode, s.StockCode, s.Date, Kind = s.ActionType, Ratio = s.RatioText, s.TradesAdjusted, s.CanUndo,
            }),
            ReturnsOfCapital = await actions.GetCapitalReturnsAsync(c.PortfolioId),
            ParcelTransfers = await actions.GetParcelTransfersAsync(c.PortfolioId),
        };
    }

    private static async Task<object?> AddSplit(AgentCall c)
    {
        var result = ShareSplit.Validate(c.Bind<SplitForm>(), FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } split)
            throw AgentToolException.Invalid(result.Errors);

        var trades = await c.Service<CgtDiscountService>().LoadTradesAsync(c.PortfolioId);
        var preview = ShareSplit.Preview(trades, split.Market, split.Code, split.Date, split.Ratio);
        if (preview.TradesAdjusted == 0)
            throw new AgentToolException(400, $"There are no {split.Code} trades on {split.Market} before {split.Date:yyyy-MM-dd} in this portfolio, so there's nothing to change.");
        if (c.Bool("preview", false))
            return new { Message = "Preview only: nothing was saved.", Kind = split.Ratio.Kind, Ratio = split.Ratio.ToString(), Preview = preview };

        var saved = await c.Service<CorporateActionsService>().ApplySplitAsync(c.PortfolioId, split);
        if (!saved.Ok)
            throw new AgentToolException(400, saved.Error!);
        return new
        {
            Message = $"{split.Ratio.Kind} of {split.Code} ({split.Ratio}) from {split.Date:yyyy-MM-dd} recorded: {preview.TradesAdjusted:N0} earlier trade(s) changed to the new units.",
            ActionId = saved.Id,
            Preview = preview,
        };
    }

    private static async Task<object?> DeleteSplit(AgentCall c) =>
        await c.Service<CorporateActionsService>().DeleteSplitAsync(c.PortfolioId, c.RequiredInt("actionId")) switch
        {
            null => throw new AgentToolException(404, "There is no split with that id in this portfolio. list_corporate_actions gives the ids."),
            { Ok: false, Error: var error } => throw new AgentToolException(409, $"The split wasn't undone: {error}"),
            _ => new { Message = "Split undone and deleted: the trades are back in the old units." },
        };

    private static async Task<object?> AddCapitalReturn(AgentCall c)
    {
        var result = ReturnOfCapitalEntry.Validate(c.Bind<CapitalReturnForm>(), FormRules.LatestToday(DateTime.UtcNow));
        if (result.Value is not { } r)
            throw AgentToolException.Invalid(result.Errors);

        var id = c.PortfolioId;
        var actions = c.Service<CorporateActionsService>();
        var preview = ReturnOfCapitalEntry.Preview(await c.Service<CgtDiscountService>().LoadTradesAsync(id), await actions.GetCapitalReturnLotsAsync(id), r,
            await actions.GetSplitFactorsAfterAsync(id, r.Market, r.Code, r.Date));
        if (preview.Parcels.Count == 0)
            throw new AgentToolException(400, $"No {r.Code} units on {r.Market} were held in this portfolio at the end of {r.Date:yyyy-MM-dd}.");
        if (preview.Total <= 0 || preview.Total > FormRules.MaxMoney)
            throw new AgentToolException(400, "The total comes to less than a cent or is too large; check the amount.");
        var summary = new { preview.UnitsHeld, Total = Math.Round(preview.Total, 2), CostBaseBefore = Math.Round(preview.CostBaseBefore, 2), CostBaseReduction = Math.Round(preview.Reduction, 2), CapitalGainG1 = Math.Round(preview.Excess, 2) };
        if (c.Bool("preview", false))
            return new { Message = "Preview only: nothing was saved.", Preview = summary };

        var saved = await actions.AddCapitalReturnAsync(id, r, preview.Total);
        if (!saved.Ok)
            throw new AgentToolException(400, saved.Error!);
        return new
        {
            Message = $"Return of capital of {preview.Total:N2} on {r.Code} recorded. Cost base reduced by {preview.Reduction:N2}"
                + (preview.Excess > 0 ? $"; {preview.Excess:N2} is a capital gain (G1)." : "."),
            ReturnId = saved.Id,
            Preview = summary,
        };
    }

    private static async Task<object?> DeleteCapitalReturn(AgentCall c) =>
        await c.Service<CorporateActionsService>().DeleteCapitalReturnAsync(c.PortfolioId, c.RequiredInt("returnId")) switch
        {
            null => throw new AgentToolException(404, "There is no return of capital with that id in this portfolio. list_corporate_actions gives the ids."),
            { Ok: false, Error: var error } => throw new AgentToolException(409, $"The return of capital wasn't deleted: {error}"),
            _ => new { Message = "Return of capital and its journal deleted." },
        };

    private static async Task<object?> ListJournals(AgentCall c)
    {
        var rows = await c.Service<JournalService>().ListAsync(c.PortfolioId, c.Date("from"), c.Date("to"));
        if (c.Bool("manualOnly", false))
            rows = rows.Where(j => j.IsManual).ToList();
        return Limited(rows, c);
    }

    private static async Task<object?> SaveJournal(AgentCall c)
    {
        var journals = c.Service<JournalService>();
        var journalId = c.Int("journalId");
        JournalInput input;
        if (journalId is { } id)
        {
            var existing = await journals.GetAsync(c.PortfolioId, id)
                ?? throw new AgentToolException(404, "There is no journal with that id in this portfolio. list_journals gives the ids.");
            if (!existing.IsManual)
                throw new AgentToolException(409, $"Only manual journals can be changed. This one was posted for a {JournalRules.SourceLabel(existing.SourceModule).ToLowerInvariant()}; change that record instead.");
            var taxCodes = (await c.Service<SalesTaxService>().GetCodesAsync(c.PortfolioId)).ToDictionary(t => t.TaxCodeId);
            input = c.BindOver(JournalService.ToInput(existing, taxCodes));
        }
        else
        {
            if (c.Date("date") is null || c.String("description") is null || c.Arg("lines") is null)
                throw new AgentToolException(400, "A new journal needs date, description and lines.");
            input = c.Bind<JournalInput>();
        }
        input.Lines ??= [];

        var result = await journals.SaveAsync(c.PortfolioId, journalId, input);
        return result.Outcome switch
        {
            JournalChangeOutcome.Saved => new { Message = journalId is null ? "Journal posted." : "Journal updated.", result.JournalId },
            JournalChangeOutcome.NotFound => throw new AgentToolException(404, "There is no journal with that id in this portfolio."),
            JournalChangeOutcome.NotManual => throw new AgentToolException(409, "Only manual journals can be changed."),
            _ => throw new AgentToolException(400, string.Join(" ", result.Errors ?? ["The journal isn't valid."])),
        };
    }

    private static async Task<object?> DeleteJournal(AgentCall c)
    {
        var result = await c.Service<JournalService>().DeleteAsync(c.PortfolioId, c.RequiredInt("journalId"));
        return result.Outcome switch
        {
            JournalChangeOutcome.Saved => new { Message = "Journal deleted." },
            JournalChangeOutcome.NotFound => throw new AgentToolException(404, "There is no journal with that id in this portfolio. list_journals gives the ids."),
            _ => throw new AgentToolException(409, "Only manual journals can be deleted. Change or delete the trade, payout, cash entry or invoice it was posted for instead."),
        };
    }

    private static async Task<object?> SaveCustomer(AgentCall c)
    {
        var invoices = c.Service<InvoiceService>();
        var input = c.Int("customerId") is { } id
            ? c.BindOver(CustomerInput.From((await invoices.GetCustomersAsync(c.PortfolioId)).FirstOrDefault(x => x.CustomerId == id)
                ?? throw new AgentToolException(404, "There is no customer with that id in this portfolio. list_customers gives the ids.")))
            : c.Bind<CustomerInput>();
        return Saved(await invoices.SaveCustomerAsync(c.PortfolioId, input), input.CustomerId is null ? "Customer added." : "Customer saved.");
    }

    private static async Task<object?> SaveSupplier(AgentCall c)
    {
        var bills = c.Service<BillService>();
        var input = c.Int("supplierId") is { } id
            ? c.BindOver(SupplierInput.From((await bills.GetSuppliersAsync(c.PortfolioId)).FirstOrDefault(x => x.SupplierId == id)
                ?? throw new AgentToolException(404, "There is no supplier with that id in this portfolio. list_suppliers gives the ids.")))
            : c.Bind<SupplierInput>();
        return Saved(await bills.SaveSupplierAsync(c.PortfolioId, input), input.SupplierId is null ? "Supplier added." : "Supplier saved.");
    }

    private static async Task<object?> SaveInvoice(AgentCall c)
    {
        var invoices = c.Service<InvoiceService>();
        var lists = await c.Service<DocumentLists>().LoadAsync(c.PortfolioId, purchases: false);
        var invoiceId = c.Int("invoiceId");
        InvoiceInput input;
        if (invoiceId is { } id)
        {
            var existing = await invoices.GetInvoiceAsync(c.PortfolioId, id)
                ?? throw new AgentToolException(404, "There is no invoice with that id in this portfolio. list_invoices gives the ids.");
            if (existing.IsVoid)
                throw new AgentToolException(409, "A voided invoice can't be changed.");
            input = c.BindOver(InvoiceInput.From(existing));
        }
        else
        {
            if (c.Int("customerId") is null || c.Arg("lines") is null)
                throw new AgentToolException(400, "A new invoice needs customerId and lines.");
            var settings = await invoices.GetSettingsAsync(c.PortfolioId);
            input = c.BindOver(InvoiceInput.New(DateTime.Today, settings, lists.Accounts.FirstOrDefault()?.AccountId ?? 0));
            if (c.Date("issueDate") is { } issued && c.Date("dueDate") is null)
                input.DueDate = issued.ToDateTime(TimeOnly.MinValue).AddDays(settings.DueDays);
        }
        DefaultQuantities(input);

        var approve = c.Bool("approve", false);
        var result = await invoices.SaveInvoiceAsync(c.PortfolioId, invoiceId, input, approve, lists.AccountIds);
        if (!result.Ok)
            throw new AgentToolException(400, result.Error!);
        var saved = await invoices.GetInvoiceAsync(c.PortfolioId, result.Id ?? invoiceId ?? 0);
        return new
        {
            Message = approve ? "Invoice approved and posted to the ledger." : saved?.IsDraft == false ? "Invoice saved and its journal updated." : "Draft saved. Nothing is posted until it is approved.",
            InvoiceId = result.Id ?? invoiceId,
            Invoice = saved,
        };
    }

    private static async Task<object?> SendInvoice(AgentCall c)
    {
        var id = c.RequiredInt("invoiceId");
        var email = c.Service<IEmailSender>();
        var invoices = c.Service<InvoiceService>();
        var invoice = await invoices.GetInvoiceAsync(c.PortfolioId, id)
            ?? throw new AgentToolException(404, "There is no invoice with that id in this portfolio. list_invoices gives the ids.");
        if (!email.IsConfigured)
            throw new AgentToolException(409, "This site has no email settings, so invoices can't be emailed from it.");
        if (invoice.IsDraft || invoice.IsVoid)
            throw new AgentToolException(409, "Only an approved invoice can be emailed. Approve it with save_invoice first.");
        var to = c.String("to") ?? invoice.Customer.Email?.Trim();
        if (string.IsNullOrEmpty(to) || to.Length > 500 || !MimeKit.MailboxAddress.TryParse(to, out _))
            throw new AgentToolException(400, "Give the email address to send the invoice to (the customer has none on file).");

        var business = await c.Service<ClientService>().GetAsync(c.PortfolioId);
        var businessName = business?.LegalName is { Length: > 0 } n ? n : c.Portfolio.Name;
        var taxName = (await c.Service<SalesTaxService>().GetSetupAsync(c.PortfolioId))?.TaxName ?? "Tax";
        var note = c.String("note");
        await email.SendAsync(InvoiceEmail.Build(invoice, await invoices.GetSettingsAsync(c.PortfolioId), businessName, taxName, to, c.Caller.Email,
            note is { Length: > 1000 } ? note[..1000] : note));
        return new { Message = $"Invoice {invoice.Number} emailed to {to}." };
    }

    private static async Task<object?> SaveBill(AgentCall c)
    {
        var bills = c.Service<BillService>();
        var lists = await c.Service<DocumentLists>().LoadAsync(c.PortfolioId, purchases: true);
        var billId = c.Int("billId");
        BillInput input;
        if (billId is { } id)
        {
            var existing = await bills.GetBillAsync(c.PortfolioId, id)
                ?? throw new AgentToolException(404, "There is no bill with that id in this portfolio. list_bills gives the ids.");
            if (existing.IsVoid)
                throw new AgentToolException(409, "A voided bill can't be changed.");
            input = c.BindOver(BillInput.From(existing));
        }
        else
        {
            if (c.Int("supplierId") is not { } supplierId || c.Arg("lines") is null)
                throw new AgentToolException(400, "A new bill needs supplierId and lines.");
            var supplier = (await bills.GetSuppliersAsync(c.PortfolioId)).FirstOrDefault(s => s.SupplierId == supplierId);
            var start = BillInput.New(DateTime.Today, supplier?.DueDays ?? 30, supplier?.DefaultAccountId ?? lists.Accounts.FirstOrDefault()?.AccountId ?? 0);
            start.AmountsIncludeTax = true;
            input = c.BindOver(start);
            if (c.Date("issueDate") is { } issued && c.Date("dueDate") is null)
                input.DueDate = issued.ToDateTime(TimeOnly.MinValue).AddDays(supplier?.DueDays ?? 30);
        }
        DefaultQuantities(input);

        var approve = c.Bool("approve", false);
        var result = await bills.SaveBillAsync(c.PortfolioId, billId, input, approve, lists.AccountIds);
        if (!result.Ok)
            throw new AgentToolException(400, result.Error!);
        var saved = await bills.GetBillAsync(c.PortfolioId, result.Id ?? billId ?? 0);
        return new
        {
            Message = approve ? "Bill approved and posted to the ledger." : saved?.IsDraft == false ? "Bill saved and its journal updated." : "Draft saved. Nothing is posted until it is approved.",
            BillId = result.Id ?? billId,
            Bill = saved,
        };
    }

    /// <summary>A line sent without a quantity is one of it, as the form starts each line.</summary>
    private static void DefaultQuantities(DocumentInput input)
    {
        foreach (var line in input.Lines)
            line.Quantity ??= 1;
    }

    private static async Task<object?> SetManualPrice(AgentCall c)
    {
        var market = c.String("market")?.ToUpperInvariant() ?? throw new AgentToolException(400, "market is required.");
        var code = c.String("code")?.ToUpperInvariant() ?? throw new AgentToolException(400, "code is required.");
        var date = c.Date("date") ?? throw new AgentToolException(400, "date is required.");
        if (c.Decimal("price") is not { } price || price <= 0)
            throw new AgentToolException(400, "Enter a price above zero.");
        if (!AssetTypes.IsManual(market))
            throw new AgentToolException(400, $"Prices for {market} come from the price feed. Manual prices are for {string.Join(", ", AssetTypes.ManualMarkets.Order())}.");
        var held = (await c.Service<TradesService>().GetTradesAsync(c.PortfolioId, code, false))
            .Any(t => string.Equals(t.ExchangeCode?.Trim(), market, StringComparison.OrdinalIgnoreCase));
        if (!held)
            throw new AgentToolException(400, $"This portfolio has no {code} trades on {market}.");
        return Saved(await c.Service<ManualPriceService>().SaveAsync(c.PortfolioId, market, code, date, price), $"Saved {code} at {price:0.####} on {date:yyyy-MM-dd}.");
    }
}
