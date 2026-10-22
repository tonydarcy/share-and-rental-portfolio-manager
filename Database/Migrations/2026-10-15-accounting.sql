-- =============================================
-- Migration: accounting section on the new site
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the new site build
-- with the Accounting menu (chart of accounts, journals, bank statement import).
-- Run 2026-10-15-ledger-fixes.sql first.
-- The old site keeps working: no procedure is dropped or renamed, and the two
-- changed procedures keep their parameters and result columns (one gains two
-- columns at the end).
-- Safe to run more than once.
--
-- What it changes:
--   * uspGetChartOfAccounts also returns iParentAccountID and bIsPostable.
--   * uspUpdateChartOfAccount (new) edits a portfolio's own account.
--   * uspSetChartOfAccountActive (new) deactivates or reactivates a portfolio's
--     own account (bIsPostable), which hides it from the journal and import lists.
--   * uspAddBankImportJournal (new) posts one imported bank statement line
--     (bank account against Bank Import Suspense), as ImportBankStatement.aspx
--     did with inline SQL.
--   * uspAllocateSuspenseTransaction: same parameters and result. It now checks
--     the line and the target account belong to the portfolio, refuses a line
--     that is already allocated, saves in one transaction, and dates the
--     allocation journal on the bank transaction's date (it used today's date,
--     which put the income or expense in the wrong period).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. uspGetChartOfAccounts: two more columns at the end
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetChartOfAccounts]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:		Tony Darcy & Gemini
-- Date:        5-Dec-2025
-- Version:     1.001
-- Description:	Initial Version
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Also returns iParentAccountID and bIsPostable (at the end)
-- =============================================
BEGIN
    SELECT
        CA.iAccountID,
        CA.iAccountNumber,
        CA.strAccountName,
        CA.strAccountType,
        CA.strCurrency,
        CA.iUserID,
        ISNULL(Parent.strAccountName, '-') AS ParentAccountName,
        CASE WHEN CA.iUserID = 0 THEN 'System' ELSE 'Custom' END AS Source,
        CA.iParentAccountID,
        CA.bIsPostable
    FROM tblChartOfAccounts CA
    LEFT JOIN tblChartOfAccounts Parent ON CA.iParentAccountID = Parent.iAccountID
    WHERE CA.iUserID = @iUserId OR CA.iUserID = 0
    ORDER BY CA.iAccountNumber;
END
GO

-- =============================================
-- 2. Editing a portfolio's own account
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspUpdateChartOfAccount]
(
    @iUserId          INT,
    @iAccountID       INT,
    @iAccountNumber   INT,
    @strAccountName   VARCHAR(255),
    @strAccountType   VARCHAR(50),
    @iParentAccountID INT = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Edits one of the portfolio's own ledger accounts (never a
--              system account, iUserID = 0). Same rules as uspAddChartOfAccount
--              (unique number and name), plus: the type can't change once the
--              account has transactions, the parent must be the portfolio's or
--              a system account and not the account itself or one under it, and
--              an account the procedures find by its name (bank accounts, the
--              suspense and clearing accounts, share cost base accounts) can't
--              be renamed.
--              Returns one row: Result = 'Success' or a message starting 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @strOldName VARCHAR(255), @strOldType VARCHAR(50);
    SET @strAccountName = LTRIM(RTRIM(@strAccountName));

    IF EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND iUserID = 0)
    BEGIN
        SELECT 'Error: System accounts can''t be changed.' AS Result;
        RETURN;
    END

    SELECT @strOldName = strAccountName, @strOldType = strAccountType
    FROM tblChartOfAccounts
    WHERE iAccountID = @iAccountID AND iUserID = @iUserId;

    IF @strOldName IS NULL
    BEGIN
        SELECT 'Error: That account was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF ISNULL(@strAccountName, '') = ''
    BEGIN
        SELECT 'Error: Enter the account name.' AS Result;
        RETURN;
    END

    IF @strAccountType NOT IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')
    BEGIN
        SELECT 'Error: Choose the account type.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblChartOfAccounts
               WHERE iAccountNumber = @iAccountNumber AND (iUserID = @iUserId OR iUserID = 0) AND iAccountID <> @iAccountID)
    BEGIN
        SELECT 'Error: Account Number already exists.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblChartOfAccounts
               WHERE strAccountName = @strAccountName AND (iUserID = @iUserId OR iUserID = 0) AND iAccountID <> @iAccountID)
    BEGIN
        SELECT 'Error: Account Name already exists.' AS Result;
        RETURN;
    END

    IF @strAccountName <> @strOldName
       AND (@strOldName IN ('Bank Import Suspense', 'Trade Clearing / Suspense')
            OR @strOldName LIKE '% - Cost Base'
            OR EXISTS (SELECT 1 FROM tblBankStatement WHERE iUserId = @iUserId AND strBankAccountName = @strOldName))
    BEGIN
        SELECT 'Error: Trades, cash transactions or imports find this account by its name, so it can''t be renamed.' AS Result;
        RETURN;
    END

    IF @strAccountType <> @strOldType AND EXISTS (SELECT 1 FROM tblJournalLines WHERE iAccountID = @iAccountID)
    BEGIN
        SELECT 'Error: This account has transactions, so its type can''t be changed.' AS Result;
        RETURN;
    END

    IF @iParentAccountID IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iParentAccountID AND (iUserID = @iUserId OR iUserID = 0))
        BEGIN
            SELECT 'Error: The parent account was not found in this portfolio.' AS Result;
            RETURN;
        END

        -- The parent can't be the account itself or anything under it
        DECLARE @bLoop BIT = 0;
        ;WITH Ancestors AS (
            SELECT iAccountID, iParentAccountID, 0 AS Depth
            FROM tblChartOfAccounts WHERE iAccountID = @iParentAccountID
            UNION ALL
            SELECT CA.iAccountID, CA.iParentAccountID, A.Depth + 1
            FROM tblChartOfAccounts CA
            JOIN Ancestors A ON CA.iAccountID = A.iParentAccountID
            WHERE A.Depth < 50
        )
        SELECT TOP 1 @bLoop = 1 FROM Ancestors WHERE iAccountID = @iAccountID;

        IF @bLoop = 1
        BEGIN
            SELECT 'Error: An account can''t sit under itself or one of its own sub-accounts.' AS Result;
            RETURN;
        END
    END

    UPDATE tblChartOfAccounts
    SET iAccountNumber = @iAccountNumber,
        strAccountName = @strAccountName,
        strAccountType = @strAccountType,
        iParentAccountID = @iParentAccountID
    WHERE iAccountID = @iAccountID AND iUserID = @iUserId;

    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- 3. Deactivating / reactivating a portfolio's own account
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspSetChartOfAccountActive]
(
    @iUserId    INT,
    @iAccountID INT,
    @bActive    BIT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Sets bIsPostable on one of the portfolio's own accounts.
--              An inactive account keeps its history and still shows in the
--              reports, but is left out of the journal entry and bank import
--              account lists (uspGetAccountsForJournal / uspGetAccountsForImport).
--              Returns one row: Result = 'Success' or a message starting 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND iUserID = 0)
    BEGIN
        SELECT 'Error: System accounts can''t be changed.' AS Result;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That account was not found in this portfolio.' AS Result;
        RETURN;
    END

    UPDATE tblChartOfAccounts
    SET bIsPostable = @bActive
    WHERE iAccountID = @iAccountID AND iUserID = @iUserId;

    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- 4. One imported bank statement line
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddBankImportJournal]
(
    @iUserId        INT,
    @iBankAccountID INT,
    @dtDate         DATE,
    @strDescription VARCHAR(500),
    @dblAmount      DECIMAL(19,4), -- positive = deposit, negative = withdrawal
    @strBatchID     VARCHAR(50)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Posts one bank statement line the way ImportBankStatement.aspx
--              did, but checks the bank account belongs to the portfolio:
--                Deposit:     Dr bank account, Cr Bank Import Suspense
--                Withdrawal:  Dr Bank Import Suspense, Cr bank account
--              The journal is strSourceModule 'BankImport' with the batch id,
--              so it shows on Import History and the suspense line on Unallocated.
--              Adds the portfolio's 'Bank Import Suspense' account (9999,
--              Liability) if there is none. Errors are thrown, so a caller's
--              transaction rolls the whole statement back.
--              Returns one row: JournalId.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF ISNULL(@dblAmount, 0) = 0
        THROW 50101, 'A bank statement line with no amount can''t be imported.', 1;

    IF NULLIF(LTRIM(RTRIM(ISNULL(@strBatchID, ''))), '') IS NULL
        THROW 50102, 'An import batch id is required.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM tblChartOfAccounts
        WHERE iAccountID = @iBankAccountID
          AND (iUserID = @iUserId OR iUserID = 0)
          AND strAccountType IN ('Asset', 'Liability', 'Equity')
          AND bIsPostable = 1)
        THROW 50103, 'The bank account for this import was not found in the portfolio.', 1;

    BEGIN TRANSACTION;

    DECLARE @iSuspenseAccountID INT;
    SELECT TOP 1 @iSuspenseAccountID = iAccountID
    FROM tblChartOfAccounts WITH (UPDLOCK, HOLDLOCK)
    WHERE strAccountName = 'Bank Import Suspense' AND (iUserID = @iUserId OR iUserID = 0)
    ORDER BY iUserID DESC, iAccountID;

    IF @iSuspenseAccountID IS NULL
    BEGIN
        INSERT INTO tblChartOfAccounts (iUserID, iAccountNumber, strAccountName, strAccountType, bIsPostable)
        VALUES (@iUserId, 9999, 'Bank Import Suspense', 'Liability', 1);
        SET @iSuspenseAccountID = SCOPE_IDENTITY();
    END

    IF @iSuspenseAccountID = @iBankAccountID
        THROW 50104, 'Choose the bank account the statement belongs to, not the suspense account.', 1;

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, dtmCreatedDate, strBatchID)
    VALUES (@iUserId, @dtDate, @strDescription, 'BankImport', GETDATE(), @strBatchID);
    DECLARE @iJournalID INT = SCOPE_IDENTITY();

    DECLARE @dblAbs DECIMAL(19,4) = ABS(@dblAmount);

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
    VALUES (@iJournalID, CASE WHEN @dblAmount > 0 THEN @iBankAccountID ELSE @iSuspenseAccountID END, @dblAbs, 0);

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
    VALUES (@iJournalID, CASE WHEN @dblAmount > 0 THEN @iSuspenseAccountID ELSE @iBankAccountID END, 0, @dblAbs);

    COMMIT TRANSACTION;

    SELECT @iJournalID AS JournalId;
END
GO

-- =============================================
-- 5. uspAllocateSuspenseTransaction: same parameters, checked and dated
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAllocateSuspenseTransaction]
(
    @iUserId INT,
    @iSuspenseLineID INT,
    @iTargetAccountID INT,
    @strDescription VARCHAR(500)
)
AS
-- =============================================
-- Author:		Tony Darcy & Gemini
-- Date:        5-Dec-2025
-- Version:     1.001
-- Description:	Initial Version
-- =============================================
-- Author:		Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Version:     1.002
-- Description:	Checks the line and target account belong to the portfolio and
--              the line isn't already allocated; dates the allocation journal
--              on the bank transaction's date (was today); one transaction
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @SuspenseAmt DECIMAL(19,4);
    DECLARE @SuspenseAccID INT;
    DECLARE @dtDate DATE;
    DECLARE @iMatchedJournalID INT;

    -- 1. The suspense line, only if it is in one of this portfolio's journals.
    --    Checks come before the transaction so a caller's own transaction is
    --    never rolled back by a refusal.
    SELECT @SuspenseAccID = JL.iAccountID,
           @SuspenseAmt = ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0), -- Positive = Debit (Withdrawal), Negative = Credit (Deposit)
           @dtDate = J.dtmTransactionDate,
           @iMatchedJournalID = JL.iMatchedJournalID
    FROM tblJournalLines JL
    INNER JOIN tblJournal J ON J.iJournalID = JL.iJournalID
    WHERE JL.iJournalLineID = @iSuspenseLineID AND J.iUserID = @iUserId;

    IF @SuspenseAccID IS NULL
    BEGIN
        SELECT 'Error: Transaction not found.' AS Result;
        RETURN;
    END

    IF @iMatchedJournalID IS NOT NULL
    BEGIN
        SELECT 'Error: This transaction has already been allocated.' AS Result;
        RETURN;
    END

    IF @SuspenseAmt = 0
    BEGIN
        SELECT 'Error: This transaction has no amount.' AS Result;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts
                   WHERE iAccountID = @iTargetAccountID AND (iUserID = @iUserId OR iUserID = 0))
       OR @iTargetAccountID = @SuspenseAccID
    BEGIN
        SELECT 'Error: The target account was not found in this portfolio.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    -- Someone else may have allocated it since the check
    IF EXISTS (SELECT 1 FROM tblJournalLines WITH (UPDLOCK, HOLDLOCK)
               WHERE iJournalLineID = @iSuspenseLineID AND iMatchedJournalID IS NOT NULL)
        THROW 50111, 'This transaction has already been allocated.', 1;

    -- 2. Allocation journal on the bank transaction's date
    DECLARE @NewJournalID INT;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, dtmCreatedDate)
    VALUES (@iUserId, ISNULL(@dtDate, CAST(GETDATE() AS DATE)), @strDescription, 'ManualAllocation', GETDATE());
    SET @NewJournalID = SCOPE_IDENTITY();

    -- 3. Line A clears the suspense account (opposite of the original),
    --    line B posts the same direction to the target account
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
    VALUES (
        @NewJournalID,
        @SuspenseAccID,
        CASE WHEN @SuspenseAmt < 0 THEN ABS(@SuspenseAmt) ELSE 0 END,
        CASE WHEN @SuspenseAmt > 0 THEN @SuspenseAmt ELSE 0 END
    );

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
    VALUES (
        @NewJournalID,
        @iTargetAccountID,
        CASE WHEN @SuspenseAmt > 0 THEN @SuspenseAmt ELSE 0 END,
        CASE WHEN @SuspenseAmt < 0 THEN ABS(@SuspenseAmt) ELSE 0 END
    );

    -- 4. Mark the original as matched
    UPDATE tblJournalLines
    SET iMatchedJournalID = @NewJournalID
    WHERE iJournalLineID = @iSuspenseLineID;

    COMMIT TRANSACTION;

    SELECT 'Success' AS Result;
END
GO
