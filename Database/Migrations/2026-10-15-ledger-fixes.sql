-- =============================================
-- Migration: double-entry ledger fixes
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the new site
-- build that edits payouts, adds rental payouts and deletes double-entry trades.
-- The old site keeps working: no procedure is dropped or renamed, and every
-- changed procedure only gains optional parameters at the end.
-- Safe to run more than once.
--
-- What it changes:
--   * uspAddPayout posts a balanced journal for every kind of payout
--     (franked, unfranked, franking credit, interest, trust, other, TFN and
--     foreign tax withheld, rental income and expenses).
--   * uspAddRentalPayout posts the same journal (Hybrid / DoubleEntry only).
--   * uspUpdatePayout re-posts the payout's journal so it matches the new
--     amounts, and can now also change the code, franking credit and trust income.
--   * uspUpdateRentalPayout (new) edits a rental payout and re-posts its journal.
--   * uspDeletePayout also deletes the payout's journal.
--   * uspDeleteShareTrade (new) deletes a trade, its journals, its settlement
--     cash row and journal in one transaction. DeleteAShareTrade now calls it.
--   * Adds the system ledger accounts the payout journal needs, if missing.
--
-- Afterwards run 2026-10-15-check-unbalanced-journals.sql to see which
-- existing journals don't balance (it only reads).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Pre-flight: the account numbers the payout journal uses must not already
--    belong to a different kind of account. If they do, nothing below runs.
-- =============================================
IF EXISTS (
    SELECT 1
    FROM tblChartOfAccounts CA
    JOIN (VALUES (1100, 'Asset'), (1400, 'Asset'), (1410, 'Asset'), (1420, 'Asset'),
                 (4100, 'Revenue'), (4200, 'Revenue'), (4300, 'Revenue'), (4400, 'Revenue'),
                 (4900, 'Revenue'), (5400, 'Expense')) AS V(iAccountNumber, strAccountType)
      ON CA.iAccountNumber = V.iAccountNumber
    WHERE CA.strAccountType <> V.strAccountType
)
BEGIN
    SELECT CA.iUserID, CA.iAccountID, CA.iAccountNumber, CA.strAccountName,
           CA.strAccountType, V.strAccountType AS ExpectedType
    FROM tblChartOfAccounts CA
    JOIN (VALUES (1100, 'Asset'), (1400, 'Asset'), (1410, 'Asset'), (1420, 'Asset'),
                 (4100, 'Revenue'), (4200, 'Revenue'), (4300, 'Revenue'), (4400, 'Revenue'),
                 (4900, 'Revenue'), (5400, 'Expense')) AS V(iAccountNumber, strAccountType)
      ON CA.iAccountNumber = V.iAccountNumber
    WHERE CA.strAccountType <> V.strAccountType;

    RAISERROR('Stopped: an account number the payout journal needs is already used for a different type of account (listed above). Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =============================================
-- 2. Ledger account lookup (creates a missing system account)
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetLedgerAccount]
(
    @iUserId        INT,
    @iAccountNumber INT,
    @strAccountName VARCHAR(255),
    @strAccountType VARCHAR(50),
    @iAccountID     INT OUTPUT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Finds the ledger account with this number for a portfolio
--              (its own account first, then the system one, iUserID = 0).
--              If neither exists, adds it as a system account under the
--              x000 header account when there is one. Returns no result set.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @iAccountID = NULL;

    SELECT TOP 1 @iAccountID = iAccountID
    FROM tblChartOfAccounts
    WHERE iAccountNumber = @iAccountNumber AND (iUserID = @iUserId OR iUserID = 0)
    ORDER BY iUserID DESC, iAccountID;

    IF @iAccountID IS NULL
    BEGIN
        DECLARE @iParentAccountID INT;
        SELECT TOP 1 @iParentAccountID = iAccountID
        FROM tblChartOfAccounts
        WHERE iUserID = 0
          AND iAccountNumber = (@iAccountNumber / 1000) * 1000
          AND iAccountNumber <> @iAccountNumber
        ORDER BY iAccountID;

        INSERT INTO tblChartOfAccounts (iUserID, iParentAccountID, iAccountNumber, strAccountName, strAccountType, bIsPostable)
        VALUES (0, @iParentAccountID, @iAccountNumber, @strAccountName, @strAccountType, 1);
        SET @iAccountID = SCOPE_IDENTITY();
    END
END
GO

-- =============================================
-- 3. Make sure the system accounts exist (existing ones are kept as they are)
-- =============================================
DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 1400, 'TFN Withholding Credits', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 1410, 'Franking Credits Receivable', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 1420, 'Foreign Income Tax Offsets', 'Asset', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4100, 'Interest Income', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4200, 'Dividend Income', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4300, 'Trust Distribution Income', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4400, 'Rental Income', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4900, 'Other Investment Income', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 5400, 'Rental Property Expenses', 'Expense', @iAccountID OUTPUT;

-- The accounts payout journals will post to
SELECT iAccountID, iAccountNumber, strAccountName, strAccountType
FROM tblChartOfAccounts
WHERE iUserID = 0 AND iAccountNumber IN (1100, 1400, 1410, 1420, 4100, 4200, 4300, 4400, 4900, 5400)
ORDER BY iAccountNumber;
GO

-- =============================================
-- 4. Payout journal
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspPostPayoutJournal]
(
    @iUserId          INT,
    @iPayoutID        INT,
    @iTargetAccountID INT = NULL, -- the "bank" side; NULL finds the bank account
    @iJournalID       INT = NULL  -- re-post into this journal (its lines must already be cleared)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Posts the journal for a payout from its saved row in tblPayouts.
--              Debits are positive, credits negative; the lines always balance.
--                Dr  Bank or target account         cash received (Cr if negative)
--                Dr  1400 TFN Withholding Credits   TFN amount withheld
--                Dr  1420 Foreign Income Tax Offsets foreign tax withheld
--                Dr  1410 Franking Credits Receivable franking credit
--                Dr  5400 Rental Property Expenses  each rental expense
--                Cr  4200 Dividend Income           franked, unfranked, franking credit gross-up
--                Cr  4100 Interest Income           interest
--                Cr  4300 Trust Distribution Income trust income
--                Cr  4400 Rental Income             rent and other rental income
--                Cr  4900 Other Investment Income   other income
--              Any gap between the cash received and the income split goes to
--              Other Investment Income so the journal still balances.
--              A new journal is only posted in Hybrid / DoubleEntry mode.
--              Returns no result set.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblPayouts WHERE iPayoutID = @iPayoutID AND iUserID = @iUserId)
        RETURN;

    IF @iJournalID IS NULL
    BEGIN
        DECLARE @strAccountingMode VARCHAR(20);
        SELECT TOP 1 @strAccountingMode = ISNULL(strAccountingMode, 'SingleEntry') FROM tblUsers WHERE iUserId = @iUserId;
        IF ISNULL(@strAccountingMode, 'SingleEntry') NOT IN ('Hybrid', 'DoubleEntry')
            RETURN;
    END

    -- A. The payout's amounts as signed journal amounts (+ debit, - credit)
    DECLARE @Parts TABLE (iSeq INT NOT NULL, strRole VARCHAR(20) NOT NULL, Amt DECIMAL(19,4) NOT NULL, strDescription VARCHAR(500) NOT NULL);

    INSERT INTO @Parts (iSeq, strRole, Amt, strDescription)
    SELECT V.iSeq, V.strRole, ROUND(CAST(V.Amt AS DECIMAL(19,4)), 2), V.strDescription
    FROM tblPayouts P
    CROSS APPLY (VALUES
        (1,  'Cash',      ISNULL(P.dblTotal, 0), CASE WHEN ISNULL(P.dblTotal, 0) < 0 THEN 'Cash paid' ELSE 'Cash received' END),
        (2,  'TFN',       ISNULL(P.dblTFNWithholdingAmount, 0), 'TFN amount withheld'),
        (3,  'Foreign',   ISNULL(P.dblForeignIncomeTaxAmount, 0), 'Foreign tax withheld'),
        (4,  'Franking',  ISNULL(P.dblFrankingCredit, 0), 'Franking credit'),
        (10, 'Dividend', -ISNULL(P.dblFrankedAmount, 0), 'Franked dividend'),
        (11, 'Dividend', -ISNULL(P.dblFrankingCredit, 0), 'Franking credit gross-up'),
        (12, 'Dividend', -ISNULL(P.dblUnFrankedAmount, 0), 'Unfranked dividend'),
        (13, 'Interest', -ISNULL(P.dblInterestAmount, 0), 'Interest'),
        (14, 'Trust',    -ISNULL(P.dblTrustIncome, 0), 'Trust distribution'),
        (15, 'Other',    -ISNULL(P.dblOtherAmount, 0), 'Other income'),
        (16, 'Rental',   -ISNULL(P.dblRentalIncomeAmount, 0), 'Rental income'),
        (17, 'Rental',   -ISNULL(P.dblOtherRentalRelatedIncomeAmount, 0), 'Other rental related income'),
        (20, 'RentalExp', ISNULL(P.dblAdvertisingForTenantsExpenseAmount, 0), 'Advertising for tenants'),
        (21, 'RentalExp', ISNULL(P.dblBodyCorportateFeesExpenseAmount, 0), 'Body corporate fees'),
        (22, 'RentalExp', ISNULL(P.dblBorrowingExpensesAmount, 0), 'Borrowing expenses'),
        (23, 'RentalExp', ISNULL(P.dblCleaningExpenseAmount, 0), 'Cleaning'),
        (24, 'RentalExp', ISNULL(P.dblCouncilRatesExpenseAmount, 0), 'Council rates'),
        (25, 'RentalExp', ISNULL(P.dblCapitalAllowancesExpenseAmount, 0), 'Capital allowances (depreciation)'),
        (26, 'RentalExp', ISNULL(P.dblGardeningAndLawnMowingExpenseAmount, 0), 'Gardening and lawn mowing'),
        (27, 'RentalExp', ISNULL(P.dblInsuranceExpenseAmount, 0), 'Insurance'),
        (28, 'RentalExp', ISNULL(P.dblInterestOnLoansExpenseAmount, 0), 'Interest on loans'),
        (29, 'RentalExp', ISNULL(P.dblLandTaxExpenseAmount, 0), 'Land tax'),
        (30, 'RentalExp', ISNULL(P.dblLegalFeesExpenseAmount, 0), 'Legal fees'),
        (31, 'RentalExp', ISNULL(P.dblPestControlExpenseAmount, 0), 'Pest control'),
        (32, 'RentalExp', ISNULL(P.dblPropertyAgentFeesAndCommissionExpenseAmount, 0), 'Property agent fees and commission'),
        (33, 'RentalExp', ISNULL(P.dblRepairsAndMaintenanceExpenseAmount, 0), 'Repairs and maintenance'),
        (34, 'RentalExp', ISNULL(P.dblCapitalWorksSpecialBuildWriteOffExpenseAmount, 0), 'Capital works deductions'),
        (35, 'RentalExp', ISNULL(P.dblStationaryTelephoneAndPostageExpenseAmount, 0), 'Stationery, telephone and postage'),
        (36, 'RentalExp', ISNULL(P.dblTravelExpenseAmount, 0), 'Travel'),
        (37, 'RentalExp', ISNULL(P.dblWaterChargesExpenseAmount, 0), 'Water charges'),
        (38, 'RentalExp', ISNULL(P.dblSundryRentalExpense1Amount, 0), 'Sundry expense 1'),
        (39, 'RentalExp', ISNULL(P.dblSundryRentalExpense2Amount, 0), 'Sundry expense 2'),
        (40, 'RentalExp', ISNULL(P.dblSundryRentalExpense3Amount, 0), 'Sundry expense 3'),
        (41, 'RentalExp', ISNULL(P.dblSundryRentalExpense4Amount, 0), 'Sundry expense 4'),
        (42, 'RentalExp', ISNULL(P.dblSundryRentalExpense5Amount, 0), 'Sundry expense 5'),
        (43, 'RentalExp', ISNULL(P.dblSundryRentalExpense6Amount, 0), 'Sundry expense 6'),
        (44, 'RentalExp', ISNULL(P.dblSundryRentalExpense7Amount, 0), 'Sundry expense 7'),
        (45, 'RentalExp', ISNULL(P.dblSundryRentalExpense8Amount, 0), 'Sundry expense 8'),
        (46, 'RentalExp', ISNULL(P.dblSundryRentalExpense9Amount, 0), 'Sundry expense 9'),
        (47, 'RentalExp', ISNULL(P.dblSundryRentalExpense10Amount, 0), 'Sundry expense 10')
    ) AS V(iSeq, strRole, Amt, strDescription)
    WHERE P.iPayoutID = @iPayoutID AND P.iUserID = @iUserId;

    DELETE FROM @Parts WHERE Amt = 0;

    -- B. Balance it: a split that doesn't match the cash received goes to Other Investment Income
    DECLARE @Difference DECIMAL(19,4) = (SELECT ISNULL(SUM(Amt), 0) FROM @Parts);
    IF @Difference <> 0
        INSERT INTO @Parts (iSeq, strRole, Amt, strDescription)
        VALUES (99, 'Other', -@Difference, 'Difference between cash received and the income split');

    IF NOT EXISTS (SELECT 1 FROM @Parts)
        RETURN;

    -- C. Accounts for each role that has an amount
    DECLARE @Accounts TABLE (strRole VARCHAR(20) NOT NULL PRIMARY KEY, iAccountID INT NULL);
    INSERT INTO @Accounts (strRole) SELECT DISTINCT strRole FROM @Parts;
    DECLARE @iAccountID INT;

    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Cash')
    BEGIN
        SET @iAccountID = NULL;
        IF @iTargetAccountID > 0
            -- Only an account of this portfolio or a system account
            SELECT @iAccountID = iAccountID FROM tblChartOfAccounts
            WHERE iAccountID = @iTargetAccountID AND (iUserID = @iUserId OR iUserID = 0);
        ELSE
            SELECT TOP 1 @iAccountID = iAccountID FROM tblChartOfAccounts
            WHERE (iUserID = @iUserId OR iUserID = 0) AND iAccountNumber >= 1100 AND iAccountNumber < 1200
            ORDER BY iUserID DESC, iAccountID;

        IF @iAccountID IS NULL AND ISNULL(@iTargetAccountID, 0) <= 0
            EXEC dbo.uspGetLedgerAccount @iUserId, 1100, 'Cash at Bank', 'Asset', @iAccountID OUTPUT;
        -- THROW (not RAISERROR) so XACT_ABORT rolls back the caller's payout too
        IF @iAccountID IS NULL
            THROW 50001, 'The target account for this payout does not belong to the portfolio.', 1;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Cash';
    END

    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'TFN')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 1400, 'TFN Withholding Credits', 'Asset', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'TFN';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Franking')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 1410, 'Franking Credits Receivable', 'Asset', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Franking';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Foreign')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 1420, 'Foreign Income Tax Offsets', 'Asset', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Foreign';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Interest')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 4100, 'Interest Income', 'Revenue', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Interest';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Dividend')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 4200, 'Dividend Income', 'Revenue', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Dividend';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Trust')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 4300, 'Trust Distribution Income', 'Revenue', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Trust';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Rental')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 4400, 'Rental Income', 'Revenue', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Rental';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'Other')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 4900, 'Other Investment Income', 'Revenue', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'Other';
    END
    IF EXISTS (SELECT 1 FROM @Accounts WHERE strRole = 'RentalExp')
    BEGIN
        EXEC dbo.uspGetLedgerAccount @iUserId, 5400, 'Rental Property Expenses', 'Expense', @iAccountID OUTPUT;
        UPDATE @Accounts SET iAccountID = @iAccountID WHERE strRole = 'RentalExp';
    END

    -- D. Journal header
    DECLARE @dtmDate DATE, @strDescription VARCHAR(500);
    SELECT @dtmDate = ISNULL(CAST(dtmTransactionDate AS DATE), CAST(GETDATE() AS DATE)),
           @strDescription = ISNULL(NULLIF(strComment, ''), 'Payout ' + ISNULL(strStockCode, ''))
    FROM tblPayouts
    WHERE iPayoutID = @iPayoutID AND iUserID = @iUserId;

    IF @iJournalID IS NULL
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID)
        VALUES (@iUserId, @dtmDate, @strDescription, 'Payout', @iPayoutID);
        SET @iJournalID = SCOPE_IDENTITY();
    END
    ELSE
        UPDATE tblJournal
        SET dtmTransactionDate = @dtmDate, strDescription = @strDescription
        WHERE iJournalID = @iJournalID AND iUserID = @iUserId;

    -- E. Lines (a missing account fails the insert, so nothing half-posted is kept)
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    SELECT @iJournalID, A.iAccountID,
           CASE WHEN P.Amt > 0 THEN P.Amt ELSE 0 END,
           CASE WHEN P.Amt < 0 THEN -P.Amt ELSE 0 END,
           P.strDescription
    FROM @Parts P
    JOIN @Accounts A ON A.strRole = P.strRole
    ORDER BY P.iSeq;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRepostPayoutJournal]
(
    @iUserId   INT,
    @iPayoutID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: After a payout is edited, rebuilds its journal lines from the
--              saved row. Keeps the journal id and the account the cash went
--              to (bank, or the import suspense account it was allocated from).
--              A payout without a journal (single entry, DRP) is left alone.
--              Returns no result set.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iJournalID INT = (
        SELECT MIN(iJournalID) FROM tblJournal
        WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID);
    IF @iJournalID IS NULL
        RETURN;

    -- The cash line: marked by description since this migration, otherwise
    -- the first balance sheet line that isn't a tax or franking credit
    DECLARE @iTargetAccountID INT;
    SELECT TOP 1 @iTargetAccountID = JL.iAccountID
    FROM tblJournal J
    JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
    WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Payout' AND J.iSourceID = @iPayoutID
      AND JL.strDescription IN ('Cash received', 'Cash paid')
    ORDER BY J.iJournalID, JL.iJournalLineID;

    IF @iTargetAccountID IS NULL
        SELECT TOP 1 @iTargetAccountID = JL.iAccountID
        FROM tblJournal J
        JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
        JOIN tblChartOfAccounts CA ON CA.iAccountID = JL.iAccountID
        WHERE J.iUserID = @iUserId AND J.strSourceModule = 'Payout' AND J.iSourceID = @iPayoutID
          AND CA.strAccountType NOT IN ('Revenue', 'Expense')
          AND CA.iAccountNumber NOT IN (1400, 1410, 1420)
        ORDER BY J.iJournalID, JL.iJournalLineID;

    BEGIN TRANSACTION;

    -- Lines cascade with their journal
    DELETE FROM tblJournal
    WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID AND iJournalID <> @iJournalID;

    DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID;

    EXEC dbo.uspPostPayoutJournal @iUserId, @iPayoutID, @iTargetAccountID, @iJournalID;

    COMMIT TRANSACTION;
END
GO

-- =============================================
-- 5. uspAddPayout: same parameters, balanced journal
-- =============================================
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
    @iTargetAccountID Int = NULL -- Optional override for the "Bank/Cash" side
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
        [dblSundryRentalExpense10Amount]
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
        @dblSundryRentalExpense10Amount
    );

    DECLARE @NewPayoutID INT = SCOPE_IDENTITY();

    -- Double entry (Hybrid / DoubleEntry portfolios only; the proc checks)
    EXEC dbo.uspPostPayoutJournal @iUserId, @NewPayoutID, @iTargetAccountID;

    COMMIT TRANSACTION;

    SELECT 'Payout Added Successfully';
END
GO

-- =============================================
-- 6. uspAddRentalPayout: same parameters, now posts the journal too
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddRentalPayout]
(
		@iUserId As Integer,
		@strStockExchangeCode VarChar(500),
		@strStockCode VarChar(500),
		@dtmTransactionDate SmallDateTime,
		@strComment VarChar(500),
		@dblRentalIncomeAmount As Float,
		@dblOtherRentalRelatedIncomeAmount As Float,
		@dblAdvertisingForTenantsExpenseAmount As Float,
		@dblBodyCorportateFeesExpenseAmount As Float,
		@dblBorrowingExpensesAmount As Float,
		@dblCleaningExpenseAmount As Float,
		@dblCouncilRatesExpenseAmount As Float,
		@dblCapitalAllowancesExpenseAmount As Float,
		@dblGardeningAndLawnMowingExpenseAmount As Float,
		@dblInsuranceExpenseAmount As Float,
		@dblInterestOnLoansExpenseAmount As Float,
		@dblLandTaxExpenseAmount As Float,
		@dblLegalFeesExpenseAmount As Float,
		@dblPestControlExpenseAmount As Float,
		@dblPropertyAgentFeesAndCommissionExpenseAmount As Float,
		@dblRepairsAndMaintenanceExpenseAmount As Float,
		@dblCapitalWorksSpecialBuildWriteOffExpenseAmount As Float,
		@dblStationaryTelephoneAndPostageExpenseAmount As Float,
		@dblTravelExpenseAmount As Float,
		@dblWaterChargesExpenseAmount As Float,
		@dblSundryRentalExpense1Amount As Float,
		@dblSundryRentalExpense2Amount As Float,
		@dblSundryRentalExpense3Amount As Float,
		@dblSundryRentalExpense4Amount As Float,
		@dblSundryRentalExpense5Amount As Float,
		@dblSundryRentalExpense6Amount As Float,
		@dblSundryRentalExpense7Amount As Float,
		@dblSundryRentalExpense8Amount As Float,
		@dblSundryRentalExpense9Amount As Float,
		@dblSundryRentalExpense10Amount Float
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        9-Mar-2022
-- Version:     1.001
-- Description:	Records a new rental payout
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Posts the journal (Hybrid / DoubleEntry) like uspAddPayout,
--              in the same transaction as the payout
-- =============================================
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	Declare @dblTotal Float;
	Declare @strPayoutConfirmed VarChar(500) = 'Yes';

	Set @dblTotal = COALESCE(@dblRentalIncomeAmount, 0) + COALESCE(@dblOtherRentalRelatedIncomeAmount, 0)
					- COALESCE(@dblAdvertisingForTenantsExpenseAmount, 0)
					- COALESCE(@dblBodyCorportateFeesExpenseAmount, 0)
					- COALESCE(@dblBorrowingExpensesAmount, 0)
					- COALESCE(@dblCleaningExpenseAmount, 0)
					- COALESCE(@dblCouncilRatesExpenseAmount, 0)
					- COALESCE(@dblCapitalAllowancesExpenseAmount, 0)
					- COALESCE(@dblGardeningAndLawnMowingExpenseAmount, 0)
					- COALESCE(@dblInsuranceExpenseAmount, 0)
					- COALESCE(@dblInterestOnLoansExpenseAmount, 0)
					- COALESCE(@dblLandTaxExpenseAmount, 0)
					- COALESCE(@dblLegalFeesExpenseAmount, 0)
					- COALESCE(@dblPestControlExpenseAmount, 0)
					- COALESCE(@dblPropertyAgentFeesAndCommissionExpenseAmount, 0)
					- COALESCE(@dblRepairsAndMaintenanceExpenseAmount, 0)
					- COALESCE(@dblCapitalWorksSpecialBuildWriteOffExpenseAmount, 0)
					- COALESCE(@dblStationaryTelephoneAndPostageExpenseAmount, 0)
					- COALESCE(@dblTravelExpenseAmount, 0)
					- COALESCE(@dblWaterChargesExpenseAmount, 0)
					- COALESCE(@dblSundryRentalExpense1Amount, 0)
					- COALESCE(@dblSundryRentalExpense2Amount, 0)
					- COALESCE(@dblSundryRentalExpense3Amount, 0)
					- COALESCE(@dblSundryRentalExpense4Amount, 0)
					- COALESCE(@dblSundryRentalExpense5Amount, 0)
					- COALESCE(@dblSundryRentalExpense6Amount, 0)
					- COALESCE(@dblSundryRentalExpense7Amount, 0)
					- COALESCE(@dblSundryRentalExpense8Amount, 0)
					- COALESCE(@dblSundryRentalExpense9Amount, 0)
					- COALESCE(@dblSundryRentalExpense10Amount, 0);

	BEGIN TRANSACTION;

	INSERT INTO [dbo].[tblPayouts](
		[iUserID], [strStockExchangeCode], [strStockCode], [dtmTransactionDate],
		[dblTotal], [dblNetAmount], [dblFrankedAmount], [dblUnFrankedAmount],
		[dblInterestAmount], [dblTFNWithholdingAmount], [dblForeignIncomeTaxAmount], [dblOtherAmount],
		[strComment], [strPayoutConfirmed],
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
		[dblSundryRentalExpense10Amount]
	)
	VALUES (
		@iUserId, @strStockExchangeCode, @strStockCode, @dtmTransactionDate,
		@dblTotal, Null, Null, Null,
		Null, Null, Null, Null,
		@strComment, @strPayoutConfirmed,
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
		@dblSundryRentalExpense10Amount
	);

	DECLARE @NewPayoutID INT = SCOPE_IDENTITY();

	-- Double entry (Hybrid / DoubleEntry portfolios only; the proc checks)
	EXEC dbo.uspPostPayoutJournal @iUserId, @NewPayoutID;

	COMMIT TRANSACTION;

	Select 'Payout Added Sucessfully';
END
GO

-- =============================================
-- 7. uspUpdatePayout: optional code / franking credit / trust income, re-posts the journal
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspUpdatePayout]
(
	@iUserId Integer,
	@iPayoutID Integer,
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
	@strPayoutConfirmed VarChar(500), --"Yes" or "No"
	-- Optional: NULL keeps the saved value
	@strStockExchangeCode VarChar(500) = NULL,
	@strStockCode VarChar(500) = NULL,
	@dblFrankingCredit Float = NULL,
	@dblTrustIncome Float = NULL
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        22-Apr-2021
-- Version:     1.001
-- Description:	Updates an existing payout (Dividend, Interest etc.)
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Optional code, franking credit and trust income; re-posts the
--              payout's journal (if it has one) to match the new amounts
-- =============================================
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	BEGIN TRANSACTION;

	UPDATE [dbo].[tblPayouts]
	SET dtmTransactionDate = @dtmTransactionDate,
		dblTotal = @dblTotal,
		dblNetAmount = @dblNetAmount,
		dblFrankedAmount = @dblFrankedAmount,
		dblUnFrankedAmount = @dblUnFrankedAmount,
		dblInterestAmount = @dblInterestAmount,
		dblTFNWithholdingAmount = @dblTFNWithholdingAmount,
		dblForeignIncomeTaxAmount = @dblForeignIncomeTaxAmount,
		dblOtherAmount = @dblOtherAmount,
		strComment = @strComment,
		strPayoutConfirmed = @strPayoutConfirmed,
		strStockExchangeCode = ISNULL(@strStockExchangeCode, strStockExchangeCode),
		strStockCode = ISNULL(@strStockCode, strStockCode),
		dblFrankingCredit = ISNULL(@dblFrankingCredit, dblFrankingCredit),
		dblTrustIncome = ISNULL(@dblTrustIncome, dblTrustIncome)
	WHERE iUserID = @iUserId
	AND iPayoutID = @iPayoutID;

	IF @@ROWCOUNT > 0
		EXEC dbo.uspRepostPayoutJournal @iUserId, @iPayoutID;

	COMMIT TRANSACTION;

	Select 'Payout Updated Sucessfully';
END
GO

-- =============================================
-- 8. uspUpdateRentalPayout (new)
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspUpdateRentalPayout]
(
	@iUserId Integer,
	@iPayoutID Integer,
	@strStockExchangeCode VarChar(500),
	@strStockCode VarChar(500),
	@dtmTransactionDate SmallDateTime,
	@strComment VarChar(500),
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
	@strPayoutConfirmed VarChar(500) = NULL -- NULL keeps the saved value
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Updates a rental payout (total = income less expenses, as in
--              uspAddRentalPayout) and re-posts its journal if it has one
-- =============================================
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	DECLARE @dblTotal Float =
		COALESCE(@dblRentalIncomeAmount, 0) + COALESCE(@dblOtherRentalRelatedIncomeAmount, 0)
		- COALESCE(@dblAdvertisingForTenantsExpenseAmount, 0)
		- COALESCE(@dblBodyCorportateFeesExpenseAmount, 0)
		- COALESCE(@dblBorrowingExpensesAmount, 0)
		- COALESCE(@dblCleaningExpenseAmount, 0)
		- COALESCE(@dblCouncilRatesExpenseAmount, 0)
		- COALESCE(@dblCapitalAllowancesExpenseAmount, 0)
		- COALESCE(@dblGardeningAndLawnMowingExpenseAmount, 0)
		- COALESCE(@dblInsuranceExpenseAmount, 0)
		- COALESCE(@dblInterestOnLoansExpenseAmount, 0)
		- COALESCE(@dblLandTaxExpenseAmount, 0)
		- COALESCE(@dblLegalFeesExpenseAmount, 0)
		- COALESCE(@dblPestControlExpenseAmount, 0)
		- COALESCE(@dblPropertyAgentFeesAndCommissionExpenseAmount, 0)
		- COALESCE(@dblRepairsAndMaintenanceExpenseAmount, 0)
		- COALESCE(@dblCapitalWorksSpecialBuildWriteOffExpenseAmount, 0)
		- COALESCE(@dblStationaryTelephoneAndPostageExpenseAmount, 0)
		- COALESCE(@dblTravelExpenseAmount, 0)
		- COALESCE(@dblWaterChargesExpenseAmount, 0)
		- COALESCE(@dblSundryRentalExpense1Amount, 0)
		- COALESCE(@dblSundryRentalExpense2Amount, 0)
		- COALESCE(@dblSundryRentalExpense3Amount, 0)
		- COALESCE(@dblSundryRentalExpense4Amount, 0)
		- COALESCE(@dblSundryRentalExpense5Amount, 0)
		- COALESCE(@dblSundryRentalExpense6Amount, 0)
		- COALESCE(@dblSundryRentalExpense7Amount, 0)
		- COALESCE(@dblSundryRentalExpense8Amount, 0)
		- COALESCE(@dblSundryRentalExpense9Amount, 0)
		- COALESCE(@dblSundryRentalExpense10Amount, 0);

	BEGIN TRANSACTION;

	UPDATE [dbo].[tblPayouts]
	SET strStockExchangeCode = @strStockExchangeCode,
		strStockCode = @strStockCode,
		dtmTransactionDate = @dtmTransactionDate,
		strComment = @strComment,
		strPayoutConfirmed = ISNULL(@strPayoutConfirmed, strPayoutConfirmed),
		dblTotal = @dblTotal,
		dblRentalIncomeAmount = @dblRentalIncomeAmount,
		dblOtherRentalRelatedIncomeAmount = @dblOtherRentalRelatedIncomeAmount,
		dblAdvertisingForTenantsExpenseAmount = @dblAdvertisingForTenantsExpenseAmount,
		dblBodyCorportateFeesExpenseAmount = @dblBodyCorportateFeesExpenseAmount,
		dblBorrowingExpensesAmount = @dblBorrowingExpensesAmount,
		dblCleaningExpenseAmount = @dblCleaningExpenseAmount,
		dblCouncilRatesExpenseAmount = @dblCouncilRatesExpenseAmount,
		dblCapitalAllowancesExpenseAmount = @dblCapitalAllowancesExpenseAmount,
		dblGardeningAndLawnMowingExpenseAmount = @dblGardeningAndLawnMowingExpenseAmount,
		dblInsuranceExpenseAmount = @dblInsuranceExpenseAmount,
		dblInterestOnLoansExpenseAmount = @dblInterestOnLoansExpenseAmount,
		dblLandTaxExpenseAmount = @dblLandTaxExpenseAmount,
		dblLegalFeesExpenseAmount = @dblLegalFeesExpenseAmount,
		dblPestControlExpenseAmount = @dblPestControlExpenseAmount,
		dblPropertyAgentFeesAndCommissionExpenseAmount = @dblPropertyAgentFeesAndCommissionExpenseAmount,
		dblRepairsAndMaintenanceExpenseAmount = @dblRepairsAndMaintenanceExpenseAmount,
		dblCapitalWorksSpecialBuildWriteOffExpenseAmount = @dblCapitalWorksSpecialBuildWriteOffExpenseAmount,
		dblStationaryTelephoneAndPostageExpenseAmount = @dblStationaryTelephoneAndPostageExpenseAmount,
		dblTravelExpenseAmount = @dblTravelExpenseAmount,
		dblWaterChargesExpenseAmount = @dblWaterChargesExpenseAmount,
		dblSundryRentalExpense1Amount = @dblSundryRentalExpense1Amount,
		dblSundryRentalExpense2Amount = @dblSundryRentalExpense2Amount,
		dblSundryRentalExpense3Amount = @dblSundryRentalExpense3Amount,
		dblSundryRentalExpense4Amount = @dblSundryRentalExpense4Amount,
		dblSundryRentalExpense5Amount = @dblSundryRentalExpense5Amount,
		dblSundryRentalExpense6Amount = @dblSundryRentalExpense6Amount,
		dblSundryRentalExpense7Amount = @dblSundryRentalExpense7Amount,
		dblSundryRentalExpense8Amount = @dblSundryRentalExpense8Amount,
		dblSundryRentalExpense9Amount = @dblSundryRentalExpense9Amount,
		dblSundryRentalExpense10Amount = @dblSundryRentalExpense10Amount
	WHERE iUserID = @iUserId
	AND iPayoutID = @iPayoutID;

	IF @@ROWCOUNT > 0
		EXEC dbo.uspRepostPayoutJournal @iUserId, @iPayoutID;

	COMMIT TRANSACTION;

	SELECT 'Payout Updated Successfully';
END
GO

-- =============================================
-- 9. uspDeletePayout: also deletes the payout's journal
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspDeletePayout]
(
	@iUserId Integer,
	@iPayoutID Integer
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        22-Apr-2021
-- Version:     1.001
-- Description:	Deletes a payout (Dividend, Interest etc.)
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Deletes the payout's journal too (lines cascade)
-- =============================================
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;

	BEGIN TRANSACTION;

	IF EXISTS (SELECT 1 FROM tblPayouts WHERE iUserID = @iUserId AND iPayoutID = @iPayoutID)
		DELETE FROM tblJournal
		WHERE iUserID = @iUserId AND strSourceModule = 'Payout' AND iSourceID = @iPayoutID;

	DELETE FROM [dbo].[tblPayouts]
	WHERE iUserID = @iUserId
	AND iPayoutID = @iPayoutID;

	COMMIT TRANSACTION;

	Select 'Payout Deleted Sucessfully';
END
GO

-- =============================================
-- 10. Deleting a share trade with its journals and settlement cash row
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspDeleteShareTrade]
(
    @iUserId       INT,
    @iShareTradeID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes a share trade together with its journal (ShareTrade),
--              the cash transaction recorded with it, that cash transaction's
--              settlement journal (BankTransaction) and its custom group links,
--              in one transaction, then recalculates the bank running balances.
--              Returns one row: Result = 'Share Trade Deleted Successfully'
--              or a message starting with 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblShareTrades WHERE iUserId = @iUserId AND iShareTradeId = @iShareTradeID)
    BEGIN
        SELECT 'Error: That share trade was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblPayouts WHERE iReinvestmentTradeID = @iShareTradeID)
    BEGIN
        SELECT 'Error: This trade was created by a dividend reinvestment (DRP). Delete the payout instead.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblCGTParcelMatching WHERE iBuyTradeID = @iShareTradeID OR iSellTradeID = @iShareTradeID)
    BEGIN
        SELECT 'Error: This trade is matched to CGT parcels. It can''t be deleted until those matches are recalculated without it.' AS Result;
        RETURN;
    END

    DECLARE @Cash TABLE (iBankStatementId INT NOT NULL PRIMARY KEY, strBankAccountName VARCHAR(500) NULL, strCurrency VARCHAR(50) NOT NULL);

    BEGIN TRANSACTION;

    INSERT INTO @Cash (iBankStatementId, strBankAccountName, strCurrency)
    SELECT iBankStatementId, strBankAccountName, strCurrency
    FROM tblBankStatement WITH (UPDLOCK, HOLDLOCK)
    WHERE iUserId = @iUserId AND iShareTradeId = @iShareTradeID;

    -- Journals (lines cascade): the trade's own and its cash settlement's
    DELETE FROM tblJournal
    WHERE iUserID = @iUserId
      AND ((strSourceModule = 'ShareTrade' AND iSourceID = @iShareTradeID)
        OR (strSourceModule = 'BankTransaction' AND iSourceID IN (SELECT iBankStatementId FROM @Cash)));

    DELETE FROM tblShareTradeGroups WHERE iShareTradeID = @iShareTradeID;

    DELETE FROM tblShareTrades WHERE iUserId = @iUserId AND iShareTradeId = @iShareTradeID;

    DELETE FROM tblBankStatement WHERE iUserId = @iUserId AND iBankStatementId IN (SELECT iBankStatementId FROM @Cash);

    -- Running balances of the bank accounts the cash came from
    ;WITH RunningBalances AS (
        SELECT
            B.dblClosingBalance AS CurrentStoredBalance,
            SUM(ISNULL(B.dblDepositAmount, 0) - ISNULL(B.dblWithdrawalAmount, 0)) OVER (
                PARTITION BY B.iUserId, B.strBankAccountName, B.strCurrency
                ORDER BY B.dtmDateOfTransaction ASC, B.iBankStatementId ASC
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS CalculatedRunningBalance
        FROM tblBankStatement B
        WHERE B.iUserId = @iUserId
          AND EXISTS (SELECT 1 FROM @Cash C
                      WHERE ISNULL(C.strBankAccountName, '') = ISNULL(B.strBankAccountName, '')
                        AND C.strCurrency = B.strCurrency)
    )
    UPDATE RunningBalances
    SET CurrentStoredBalance = CalculatedRunningBalance
    WHERE CurrentStoredBalance IS NULL OR CurrentStoredBalance <> CalculatedRunningBalance;

    COMMIT TRANSACTION;

    SELECT 'Share Trade Deleted Successfully' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[DeleteAShareTrade]
(
	@iUserId			Integer,
	@iShareTradeID		Integer
)
AS
-- =============================================
-- Author:		Tony Darcy
-- Date:        30-Jun-2020
-- Version:     1.001
-- Description:	Deletes A Share Trade & the corresponding cash transaction
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Now uspDeleteShareTrade, which also deletes the journals and
--              recalculates the bank balances. Returns its one-row message.
-- =============================================
BEGIN
	SET NOCOUNT ON;
	EXEC dbo.uspDeleteShareTrade @iUserId, @iShareTradeID;
END
GO

SET NOEXEC OFF
GO
