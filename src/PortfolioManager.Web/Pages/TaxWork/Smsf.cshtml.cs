using Microsoft.AspNetCore.Mvc;
using PortfolioManager.Core.Accounting;
using PortfolioManager.Core.TaxWork;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.TaxWork;

/// <summary>SMSF members: their yearly accounts, earnings and tax allocation, contribution caps and minimum pensions.</summary>
public class SmsfModel(CurrentPortfolio current, TaxWorkpaperService workpapers, LockedPeriodService locks, SmsfMemberService members)
    : TaxYearPageModel(current, workpapers, locks)
{
    [BindProperty]
    public SmsfMemberInput Member { get; set; } = new();

    [BindProperty]
    public SmsfAccountInput Account { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? EditMember { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? EditAccount { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var data = Workpaper!.Smsf ?? SmsfYearData.Empty;
        if (EditMember is { } m && data.Members.FirstOrDefault(x => x.MemberId == m) is { } member)
            Member = new SmsfMemberInput { MemberId = member.MemberId, Name = member.Name, BirthDate = member.BirthDate?.ToDateTime(TimeOnly.MinValue) };
        if (EditAccount is { } a && data.Accounts.FirstOrDefault(x => x.MemberAccountId == a) is { } acc)
            Account = new SmsfAccountInput
            {
                MemberAccountId = acc.MemberAccountId, MemberId = acc.MemberId, Phase = acc.Phase, Opening = acc.Opening,
                Concessional = acc.Concessional, NonConcessional = acc.NonConcessional, RolloverIn = acc.RolloverIn,
                PensionPaid = acc.PensionPaid, LumpSum = acc.LumpSum, Insurance = acc.Insurance,
                PensionStart = acc.PensionStart?.ToDateTime(TimeOnly.MinValue), StartBalance = acc.StartBalance,
            };
        return Page();
    }

    /// <summary>The members' balances and flows as a CSV for the actuary's certificate.</summary>
    public async Task<IActionResult> OnGetActuaryAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        var w = Workpaper!;
        var label = Core.Tax.FinancialYear.Label(Year);
        return File(SmsfActuaryFile.Build(w.Client.LegalName, w.Client.Abn, label, Core.Tax.FinancialYear.StartDate(Year), w.Members ?? []),
            "text/csv", $"Actuary data {label}.csv");
    }

    public async Task<IActionResult> OnPostMemberAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await members.SaveMemberAsync(Portfolio!.PortfolioId, Member), Member.MemberId is null ? "Member added." : "Member saved.");
    }

    public async Task<IActionResult> OnPostDeleteMemberAsync(int memberId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await members.DeleteMemberAsync(Portfolio!.PortfolioId, memberId), "Member deleted.");
    }

    public async Task<IActionResult> OnPostAccountAsync()
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await members.SaveAccountAsync(Portfolio!.PortfolioId, Year, YearEnd, Account), "Member account saved.");
    }

    public async Task<IActionResult> OnPostDeleteAccountAsync(int memberAccountId)
    {
        if (await LoadAsync() is { } refusal)
            return refusal;
        return Done(await members.DeleteAccountAsync(Portfolio!.PortfolioId, Year, YearEnd, memberAccountId), "Member account deleted.");
    }
}
