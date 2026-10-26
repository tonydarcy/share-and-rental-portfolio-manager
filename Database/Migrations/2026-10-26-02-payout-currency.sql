-- =============================================
-- Migration: payouts in another currency
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build that takes a payout's currency.
-- Run it after 2026-10-15-ledger-fixes.sql. Safe to run more than once. It only adds things.
--
-- A dividend from a foreign share can be entered in its own currency (US dollars, say) with the exchange rate. The site
-- converts each amount to the portfolio's currency, to the cent, before saving, so every report, return label and
-- journal stays in the portfolio's currency as before. This keeps what was entered:
--   * tblPayouts gets strCurrency, dblExchangeRate (units of that currency per one unit of the portfolio's, as on
--     trades) and dblForeignTotal (the cash received in that currency). All NULL for a payout in the portfolio's currency.
--   * uspAddPayout takes @strCurrency, @dblExchangeRate and @dblForeignTotal (optional) and saves them.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.uspPostPayoutJournal', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblPayouts', 'strCurrency') IS NULL
    ALTER TABLE dbo.tblPayouts ADD strCurrency VARCHAR(3) NULL;
IF COL_LENGTH('dbo.tblPayouts', 'dblExchangeRate') IS NULL
    ALTER TABLE dbo.tblPayouts ADD dblExchangeRate FLOAT NULL;
IF COL_LENGTH('dbo.tblPayouts', 'dblForeignTotal') IS NULL
    ALTER TABLE dbo.tblPayouts ADD dblForeignTotal FLOAT NULL;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddPayout]
(
    @iUserId Integer,
    @strStockExchangeCode VarChar(500),
    @strStockCode VarChar(500),
    @dtmTransactionDate SmallDateTime,
    @dblTotal Float,
    @dblNetAmount Float,
    @dblFrankedAmount Float,
    @dblUnFrankedAmount Float,
    @dblInterestAmount Float,
    @dblTFNWithholdingAmount Float,
    @dblForeignIncomeTaxAmount Float,
    @dblOtherAmount Float,
    @strComment VarChar(500),
    @strPayoutConfirmed VarChar(500),
    @dblFrankingCredit Float = 0,
    @dblTrustIncome Float = 0,
    -- Rental Specific Columns
    @dblRentalIncomeAmount Float = 0,
    @dblOtherRentalRelatedIncomeAmount Float = 0,
    @dblAdvertisingForTenantsExpenseAmount Float = 0,
    @dblBodyCorportateFeesExpenseAmount Float = 0,
    @dblBorrowingExpensesAmount Float = 0,
    @dblCleaningExpenseAmount Float = 0,
    @dblCouncilRatesExpenseAmount Float = 0,
    @dblCapitalAllowancesExpenseAmount Float = 0,
    @dblGardeningAndLawnMowingExpenseAmount Float = 0,
    @dblInsuranceExpenseAmount Float = 0,
    @dblInterestOnLoansExpenseAmount Float = 0,
    @dblLandTaxExpenseAmount Float = 0,
    @dblLegalFeesExpenseAmount Float = 0,
    @dblPestControlExpenseAmount Float = 0,
    @dblPropertyAgentFeesAndCommissionExpenseAmount Float = 0,
    @dblRepairsAndMaintenanceExpenseAmount Float = 0,
    @dblCapitalWorksSpecialBuildWriteOffExpenseAmount Float = 0,
    @dblStationaryTelephoneAndPostageExpenseAmount Float = 0,
    @dblTravelExpenseAmount Float = 0,
    @dblWaterChargesExpenseAmount Float = 0,
    @dblSundryRentalExpense1Amount Float = 0,
    @dblSundryRentalExpense2Amount Float = 0,
    @dblSundryRentalExpense3Amount Float = 0,
    @dblSundryRentalExpense4Amount Float = 0,
    @dblSundryRentalExpense5Amount Float = 0,
    @dblSundryRentalExpense6Amount Float = 0,
    @dblSundryRentalExpense7Amount Float = 0,
    @dblSundryRentalExpense8Amount Float = 0,
    @dblSundryRentalExpense9Amount Float = 0,
    @dblSundryRentalExpense10Amount Float = 0,
    @iTargetAccountID Int = NULL, -- Optional override for the "Bank/Cash" side
    -- A payout received in another currency: the amounts above are already in the portfolio's currency
    @strCurrency VarChar(3) = NULL,
    @dblExchangeRate Float = NULL,
    @dblForeignTotal Float = NULL
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        22-Apr-2021
-- Version:     1.001
-- Description:	Records a new payout (Dividend, Interest etc.)
-- =============================================
-- Author:		Tony Darcy
-- Date:        16-May-2024
-- Version:     1.002
-- Description:	Added Franking Credit & Trust Income Columns
-- =============================================
-- Author:		Tony Darcy & Gemini
-- Date:        5-Dec-2025
-- Version:     1.003
-- Description:	Added logic for double entry
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.004
-- Description:	Journal posted by uspPostPayoutJournal so it always balances
--              (interest, other, franking credits, foreign tax, rental lines);
--              payout and journal saved in one transaction
-- =============================================
-- Date:        26-Oct-2026
-- Version:     1.005
-- Description:	Keeps the currency, exchange rate and foreign cash total of a payout received in another currency
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    INSERT INTO [dbo].[tblPayouts](
        [iUserID], [strStockExchangeCode], [strStockCode], [dtmTransactionDate],
        [dblTotal], [dblNetAmount], [dblFrankedAmount], [dblUnFrankedAmount],
        [dblInterestAmount], [dblTFNWithholdingAmount], [dblForeignIncomeTaxAmount],
        [dblOtherAmount], [strComment], [strPayoutConfirmed],
        [dblFrankingCredit], [dblTrustIncome],
        [dblRentalIncomeAmount], [dblOtherRentalRelatedIncomeAmount],
        [dblAdvertisingForTenantsExpenseAmount], [dblBodyCorportateFeesExpenseAmount],
        [dblBorrowingExpensesAmount], [dblCleaningExpenseAmount], [dblCouncilRatesExpenseAmount],
        [dblCapitalAllowancesExpenseAmount], [dblGardeningAndLawnMowingExpenseAmount],
        [dblInsuranceExpenseAmount], [dblInterestOnLoansExpenseAmount], [dblLandTaxExpenseAmount],
        [dblLegalFeesExpenseAmount], [dblPestControlExpenseAmount],
        [dblPropertyAgentFeesAndCommissionExpenseAmount], [dblRepairsAndMaintenanceExpenseAmount],
        [dblCapitalWorksSpecialBuildWriteOffExpenseAmount], [dblStationaryTelephoneAndPostageExpenseAmount],
        [dblTravelExpenseAmount], [dblWaterChargesExpenseAmount],
        [dblSundryRentalExpense1Amount], [dblSundryRentalExpense2Amount], [dblSundryRentalExpense3Amount],
        [dblSundryRentalExpense4Amount], [dblSundryRentalExpense5Amount], [dblSundryRentalExpense6Amount],
        [dblSundryRentalExpense7Amount], [dblSundryRentalExpense8Amount], [dblSundryRentalExpense9Amount],
        [dblSundryRentalExpense10Amount],
        [strCurrency], [dblExchangeRate], [dblForeignTotal]
    )
    VALUES (
        @iUserId, @strStockExchangeCode, @strStockCode, @dtmTransactionDate,
        @dblTotal, @dblNetAmount, @dblFrankedAmount, @dblUnFrankedAmount,
        @dblInterestAmount, @dblTFNWithholdingAmount, @dblForeignIncomeTaxAmount,
        @dblOtherAmount, @strComment, @strPayoutConfirmed,
        @dblFrankingCredit, @dblTrustIncome,
        @dblRentalIncomeAmount, @dblOtherRentalRelatedIncomeAmount,
        @dblAdvertisingForTenantsExpenseAmount, @dblBodyCorportateFeesExpenseAmount,
        @dblBorrowingExpensesAmount, @dblCleaningExpenseAmount, @dblCouncilRatesExpenseAmount,
        @dblCapitalAllowancesExpenseAmount, @dblGardeningAndLawnMowingExpenseAmount,
        @dblInsuranceExpenseAmount, @dblInterestOnLoansExpenseAmount, @dblLandTaxExpenseAmount,
        @dblLegalFeesExpenseAmount, @dblPestControlExpenseAmount,
        @dblPropertyAgentFeesAndCommissionExpenseAmount, @dblRepairsAndMaintenanceExpenseAmount,
        @dblCapitalWorksSpecialBuildWriteOffExpenseAmount, @dblStationaryTelephoneAndPostageExpenseAmount,
        @dblTravelExpenseAmount, @dblWaterChargesExpenseAmount,
        @dblSundryRentalExpense1Amount, @dblSundryRentalExpense2Amount, @dblSundryRentalExpense3Amount,
        @dblSundryRentalExpense4Amount, @dblSundryRentalExpense5Amount, @dblSundryRentalExpense6Amount,
        @dblSundryRentalExpense7Amount, @dblSundryRentalExpense8Amount, @dblSundryRentalExpense9Amount,
        @dblSundryRentalExpense10Amount,
        NULLIF(UPPER(LTRIM(RTRIM(@strCurrency))), ''), @dblExchangeRate, @dblForeignTotal
    );

    DECLARE @NewPayoutID INT = SCOPE_IDENTITY();

    -- Double entry (Hybrid / DoubleEntry portfolios only; the proc checks)
    EXEC dbo.uspPostPayoutJournal @iUserId, @NewPayoutID, @iTargetAccountID;

    COMMIT TRANSACTION;

    SELECT 'Payout Added Successfully';
END
GO
