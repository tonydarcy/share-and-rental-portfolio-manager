using System.Data;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Rentals;

/// <summary>Everything kept for running a property, all at once (the lists are small).</summary>
public sealed record PropertyBook(
    IReadOnlyList<Tenancy> Tenancies,
    IReadOnlyList<RentPayment> Payments,
    IReadOnlyList<TenantBill> Bills,
    IReadOnlyList<RentalJob> Jobs,
    IReadOnlyList<RentalInspection> Inspections,
    IReadOnlyList<ComplianceItem> Compliance)
{
    public static readonly PropertyBook Empty = new([], [], [], [], [], []);

    public PropertyBook For(int propertyId)
    {
        var tenancies = Tenancies.Where(t => t.PropertyId == propertyId).ToList();
        var ids = tenancies.Select(t => t.Id).ToHashSet();
        return new PropertyBook(tenancies, Payments.Where(p => ids.Contains(p.TenancyId)).ToList(), Bills.Where(b => b.PropertyId == propertyId).ToList(),
            Jobs.Where(j => j.PropertyId == propertyId).ToList(), Inspections.Where(i => i.PropertyId == propertyId).ToList(),
            Compliance.Where(c => c.PropertyId == propertyId).ToList());
    }
}

/// <summary>Tenancies, rent, tenant bills, maintenance, inspections and compliance (2026-10-23-02-running-a-property.sql).</summary>
public sealed class PropertyService(IDbConnectionFactory db)
{
    private sealed record TenancyRow(int Id, int PropertyId, string Tenants, string? Email, string? Phone, DateTime Start, DateTime? LeaseEnd, decimal Rent,
        string Frequency, decimal? Bond, string? BondLodgement, DateTime? RentReview, DateTime? Vacated, string? Note);
    private sealed record PaymentRow(int Id, int TenancyId, DateTime Paid, decimal Amount, string? Note);
    private sealed record BillRow(int Id, int PropertyId, int? TenancyId, string Category, string Description, DateTime Billed, decimal Amount, DateTime? Paid);
    private sealed record JobRow(int Id, int PropertyId, DateTime Done, string Description, string? Tradesperson, decimal Cost, string Treatment, string? InvoiceRef);
    private sealed record InspectionRow(int Id, int PropertyId, int? TenancyId, string Kind, DateTime Done, string? Notes);
    private sealed record ComplianceRow(int Id, int PropertyId, string Kind, DateTime? LastDone, int? Months, DateTime? Due, string? Note);

    /// <summary>The portfolio's records for every property. Empty until the migration has been run.</summary>
    public async Task<PropertyBook> GetAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            using var grid = await con.QueryMultipleAsync("""
                SELECT T.iTenancyID AS Id, T.iPropertyID AS PropertyId, T.strTenants AS Tenants, T.strEmail AS Email, T.strPhone AS Phone,
                       CAST(T.dtmStart AS datetime) AS Start, CAST(T.dtmLeaseEnd AS datetime) AS LeaseEnd, T.dblRent AS Rent, T.strFrequency AS Frequency,
                       T.dblBond AS Bond, T.strBondLodgement AS BondLodgement, CAST(T.dtmRentReview AS datetime) AS RentReview,
                       CAST(T.dtmVacated AS datetime) AS Vacated, T.strNote AS Note
                FROM tblRentalTenancies T JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY T.dtmStart DESC;
                SELECT R.iPaymentID AS Id, R.iTenancyID AS TenancyId, CAST(R.dtmPaid AS datetime) AS Paid, R.dblAmount AS Amount, R.strNote AS Note
                FROM tblRentalRentPayments R JOIN tblRentalTenancies T ON T.iTenancyID = R.iTenancyID JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID
                WHERE P.iUserID = @portfolioId ORDER BY R.dtmPaid DESC, R.iPaymentID DESC;
                SELECT B.iBillID AS Id, B.iPropertyID AS PropertyId, B.iTenancyID AS TenancyId, B.strCategory AS Category, B.strDescription AS Description,
                       CAST(B.dtmBilled AS datetime) AS Billed, B.dblAmount AS Amount, CAST(B.dtmPaid AS datetime) AS Paid
                FROM tblRentalTenantBills B JOIN tblRentalProperties P ON P.iPropertyID = B.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY B.dtmBilled DESC;
                SELECT J.iJobID AS Id, J.iPropertyID AS PropertyId, CAST(J.dtmDone AS datetime) AS Done, J.strDescription AS Description,
                       J.strTradesperson AS Tradesperson, J.dblCost AS Cost, J.strTreatment AS Treatment, J.strInvoiceRef AS InvoiceRef
                FROM tblRentalJobs J JOIN tblRentalProperties P ON P.iPropertyID = J.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY J.dtmDone DESC;
                SELECT I.iInspectionID AS Id, I.iPropertyID AS PropertyId, I.iTenancyID AS TenancyId, I.strKind AS Kind, CAST(I.dtmDone AS datetime) AS Done,
                       I.strNotes AS Notes
                FROM tblRentalInspections I JOIN tblRentalProperties P ON P.iPropertyID = I.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY I.dtmDone DESC;
                SELECT C.iItemID AS Id, C.iPropertyID AS PropertyId, C.strKind AS Kind, CAST(C.dtmLastDone AS datetime) AS LastDone, C.iMonths AS Months,
                       CAST(C.dtmDue AS datetime) AS Due, C.strNote AS Note
                FROM tblRentalCompliance C JOIN tblRentalProperties P ON P.iPropertyID = C.iPropertyID WHERE P.iUserID = @portfolioId ORDER BY C.strKind;
                """, new { portfolioId });
            var tenancies = (await grid.ReadAsync<TenancyRow>()).Select(r => new Tenancy(r.Id, r.PropertyId, r.Tenants, r.Email, r.Phone, D(r.Start), D(r.LeaseEnd),
                r.Rent, r.Frequency, r.Bond, r.BondLodgement, D(r.RentReview), D(r.Vacated), r.Note)).ToList();
            var payments = (await grid.ReadAsync<PaymentRow>()).Select(r => new RentPayment(r.Id, r.TenancyId, D(r.Paid), r.Amount, r.Note)).ToList();
            var bills = (await grid.ReadAsync<BillRow>()).Select(r => new TenantBill(r.Id, r.PropertyId, r.TenancyId, r.Category, r.Description, D(r.Billed),
                r.Amount, D(r.Paid))).ToList();
            var jobs = (await grid.ReadAsync<JobRow>()).Select(r => new RentalJob(r.Id, r.PropertyId, D(r.Done), r.Description, r.Tradesperson, r.Cost,
                r.Treatment, r.InvoiceRef)).ToList();
            var inspections = (await grid.ReadAsync<InspectionRow>()).Select(r => new RentalInspection(r.Id, r.PropertyId, r.TenancyId, r.Kind, D(r.Done),
                r.Notes)).ToList();
            var compliance = (await grid.ReadAsync<ComplianceRow>()).Select(r => new ComplianceItem(r.Id, r.PropertyId, r.Kind, D(r.LastDone), r.Months,
                D(r.Due), r.Note)).ToList();
            return new PropertyBook(tenancies, payments, bills, jobs, inspections, compliance);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return PropertyBook.Empty;
        }
    }

    public async Task<ProcResult> SaveTenancyAsync(int portfolioId, Tenancy t)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalTenancy", new
        {
            iUserID = portfolioId, iPropertyID = t.PropertyId, iTenancyID = t.Id, strTenants = t.Tenants, strEmail = t.Email, strPhone = t.Phone,
            dtmStart = Dt(t.Start), dtmLeaseEnd = Dt(t.LeaseEnd), dblRent = t.Rent, strFrequency = t.Frequency, dblBond = t.Bond,
            strBondLodgement = t.BondLodgement, dtmRentReview = Dt(t.RentReview), dtmVacated = Dt(t.Vacated), strNote = t.Note,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public Task<ProcResult> DeleteTenancyAsync(int portfolioId, int tenancyId) =>
        ResultAsync("uspDeleteRentalTenancy", new { iUserID = portfolioId, iTenancyID = tenancyId });

    public Task<ProcResult> AddPaymentAsync(int portfolioId, RentPayment p) =>
        ResultAsync("uspAddRentalRentPayment", new { iUserID = portfolioId, iTenancyID = p.TenancyId, dtmPaid = Dt(p.Paid), dblAmount = p.Amount, strNote = p.Note });

    public Task<ProcResult> DeletePaymentAsync(int portfolioId, int paymentId) =>
        ResultAsync("uspDeleteRentalRentPayment", new { iUserID = portfolioId, iPaymentID = paymentId });

    public Task<ProcResult> SaveBillAsync(int portfolioId, TenantBill b) =>
        ResultAsync("uspSaveRentalTenantBill", new
        {
            iUserID = portfolioId, iPropertyID = b.PropertyId, iBillID = b.Id, iTenancyID = b.TenancyId, strCategory = b.Category, strDescription = b.Description,
            dtmBilled = Dt(b.Billed), dblAmount = b.Amount, dtmPaid = Dt(b.Paid),
        });

    public Task<ProcResult> DeleteBillAsync(int portfolioId, int billId) =>
        ResultAsync("uspDeleteRentalTenantBill", new { iUserID = portfolioId, iBillID = billId });

    public async Task<ProcResult> SaveJobAsync(int portfolioId, RentalJob j)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalJob", new
        {
            iUserID = portfolioId, iPropertyID = j.PropertyId, iJobID = j.Id, dtmDone = Dt(j.Done), strDescription = j.Description,
            strTradesperson = j.Tradesperson, dblCost = j.Cost, strTreatment = j.Treatment, strInvoiceRef = j.InvoiceRef,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public Task<ProcResult> DeleteJobAsync(int portfolioId, int jobId) =>
        ResultAsync("uspDeleteRentalJob", new { iUserID = portfolioId, iJobID = jobId });

    public async Task<ProcResult> SaveInspectionAsync(int portfolioId, RentalInspection i)
    {
        await using var con = db.Create();
        var row = await con.QuerySingleAsync<(string Result, int? Id)>("uspSaveRentalInspection", new
        {
            iUserID = portfolioId, iPropertyID = i.PropertyId, iInspectionID = i.Id, iTenancyID = i.TenancyId, strKind = i.Kind, dtmDone = Dt(i.Done), strNotes = i.Notes,
        }, commandType: CommandType.StoredProcedure);
        return ProcResult.From(row.Result, "Success", row.Id);
    }

    public Task<ProcResult> DeleteInspectionAsync(int portfolioId, int inspectionId) =>
        ResultAsync("uspDeleteRentalInspection", new { iUserID = portfolioId, iInspectionID = inspectionId });

    public Task<ProcResult> SaveComplianceAsync(int portfolioId, ComplianceItem c) =>
        ResultAsync("uspSaveRentalCompliance", new
        {
            iUserID = portfolioId, iPropertyID = c.PropertyId, iItemID = c.Id, strKind = c.Kind, dtmLastDone = Dt(c.LastDone), iMonths = c.Months, dtmDue = Dt(c.Due),
            strNote = c.Note,
        });

    public Task<ProcResult> DeleteComplianceAsync(int portfolioId, int itemId) =>
        ResultAsync("uspDeleteRentalCompliance", new { iUserID = portfolioId, iItemID = itemId });

    /// <summary>Adds the usual checks for the property's state, leaving out any already there.</summary>
    public async Task<int> AddPresetsAsync(int portfolioId, int propertyId, string? state)
    {
        var existing = (await GetAsync(portfolioId)).Compliance.Where(c => c.PropertyId == propertyId).Select(c => c.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in CompliancePresets.For(state).Where(p => !existing.Contains(p.Kind)))
            if ((await SaveComplianceAsync(portfolioId, new ComplianceItem(0, propertyId, p.Kind, null, p.Months, null, null))).Ok)
                added++;
        return added;
    }

    /// <summary>Which reminder keys have already been emailed for the portfolio.</summary>
    public async Task<IReadOnlySet<string>> GetSentAlertsAsync(int portfolioId)
    {
        await using var con = db.Create();
        try
        {
            return (await con.QueryAsync<string>("SELECT strKey FROM tblRentalAlertsSent WHERE iPortfolioID = @portfolioId", new { portfolioId })).ToHashSet();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return new HashSet<string>();
        }
    }

    public async Task MarkAlertSentAsync(int portfolioId, string key)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspMarkRentalAlertSent", new { iPortfolioID = portfolioId, strKey = key }, commandType: CommandType.StoredProcedure);
    }

    private async Task<ProcResult> ResultAsync(string proc, object args)
    {
        await using var con = db.Create();
        return ProcResult.From(await con.ExecuteScalarAsync<string>(proc, args, commandType: CommandType.StoredProcedure), "Success");
    }

    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);
    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
    private static DateTime Dt(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static DateTime? Dt(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
}
