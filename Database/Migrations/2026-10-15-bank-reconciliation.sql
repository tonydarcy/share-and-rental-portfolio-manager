-- =============================================
-- Migration: bank reconciliation
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- Accounting > Bank reconciliation. It only adds tables and procedures; the
-- old site and the journals are not touched.
-- Safe to run more than once.
--
-- What it changes:
--   * tblBankReconciliation (new): one statement per bank account: the
--     statement date, opening and closing balances, and when it was finished.
--   * tblBankReconciliationLine (new): the ledger lines ticked off on it. A
--     line can only be ticked on one reconciliation.
--   * uspSaveBankReconciliation (new): saves a reconciliation in progress and
--     its ticked lines, or finishes it when the difference is nil.
--   * uspReopenBankReconciliation (new): reopens the latest finished one.
--   * uspDeleteBankReconciliation (new): deletes one that isn't finished.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblBankReconciliation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBankReconciliation (
        iReconciliationID   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblBankReconciliation PRIMARY KEY,
        iUserID             INT NOT NULL,
        iAccountID          INT NOT NULL,
        dtmStatementDate    DATE NOT NULL,
        dblOpeningBalance   DECIMAL(19,4) NOT NULL,
        dblStatementBalance DECIMAL(19,4) NOT NULL,
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblBankReconciliation_Created DEFAULT (GETDATE()),
        dtmCompletedDate    DATETIME NULL
    );
    CREATE INDEX IX_tblBankReconciliation_Account ON dbo.tblBankReconciliation (iUserID, iAccountID, dtmStatementDate);
END
GO

IF OBJECT_ID('dbo.tblBankReconciliationLine', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBankReconciliationLine (
        iJournalLineID    INT NOT NULL CONSTRAINT PK_tblBankReconciliationLine PRIMARY KEY,
        iReconciliationID INT NOT NULL CONSTRAINT FK_tblBankReconciliationLine_Rec
            REFERENCES dbo.tblBankReconciliation (iReconciliationID)
    );
    CREATE INDEX IX_tblBankReconciliationLine_Rec ON dbo.tblBankReconciliationLine (iReconciliationID);
END
GO

-- =============================================
-- Saving or finishing a reconciliation
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspSaveBankReconciliation]
(
    @iUserId             INT,
    @iReconciliationID   INT = NULL,       -- NULL starts a new one
    @iAccountID          INT,
    @dtStatementDate     DATE,
    @dblOpeningBalance   DECIMAL(19,4),
    @dblStatementBalance DECIMAL(19,4),
    @xmlLineIDs          XML,              -- <i>123</i><i>456</i>: the ticked journal lines
    @bComplete           BIT = 0
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Saves a bank reconciliation in progress, replacing its ticked
--              lines, and finishes it when @bComplete = 1 and the opening
--              balance plus the ticked lines equals the statement balance.
--              The ticked lines must be this portfolio's lines on the account,
--              dated on or before the statement date and not ticked on another
--              reconciliation. Only one reconciliation per account can be in
--              progress, and a new one must be dated after the last finished
--              one, whose closing balance is its opening balance.
--              Returns one row: Result = 'Success', ReconciliationId; or Result
--              = 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts
                   WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                     AND strAccountType IN ('Asset', 'Liability'))
    BEGIN
        SELECT 'Error: That bank account was not found in this portfolio.' AS Result, NULL AS ReconciliationId;
        RETURN;
    END

    IF @iReconciliationID IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM tblBankReconciliation
        WHERE iReconciliationID = @iReconciliationID AND iUserID = @iUserId AND iAccountID = @iAccountID AND dtmCompletedDate IS NULL)
    BEGIN
        SELECT 'Error: That reconciliation was not found, or it is already finished.' AS Result, NULL AS ReconciliationId;
        RETURN;
    END

    IF @iReconciliationID IS NULL AND EXISTS (
        SELECT 1 FROM tblBankReconciliation WHERE iUserID = @iUserId AND iAccountID = @iAccountID AND dtmCompletedDate IS NULL)
    BEGIN
        SELECT 'Error: This account already has a reconciliation in progress. Carry on with that one.' AS Result, NULL AS ReconciliationId;
        RETURN;
    END

    DECLARE @dtLast DATE, @dblLastBalance DECIMAL(19,4);
    SELECT TOP 1 @dtLast = dtmStatementDate, @dblLastBalance = dblStatementBalance
    FROM tblBankReconciliation
    WHERE iUserID = @iUserId AND iAccountID = @iAccountID AND dtmCompletedDate IS NOT NULL
    ORDER BY dtmStatementDate DESC, iReconciliationID DESC;

    IF @dtLast IS NOT NULL AND @dtStatementDate <= @dtLast
    BEGIN
        SELECT 'Error: The statement date must be after the last finished reconciliation (' + CONVERT(VARCHAR(11), @dtLast, 106) + ').' AS Result,
               NULL AS ReconciliationId;
        RETURN;
    END

    -- After the first reconciliation the opening balance is the last closing balance
    IF @dtLast IS NOT NULL
        SET @dblOpeningBalance = @dblLastBalance;

    DECLARE @Lines TABLE (iJournalLineID INT PRIMARY KEY);
    INSERT INTO @Lines (iJournalLineID)
    SELECT DISTINCT T.c.value('.', 'INT') FROM @xmlLineIDs.nodes('/i') T(c);

    IF EXISTS (
        SELECT 1 FROM @Lines X
        WHERE NOT EXISTS (
            SELECT 1 FROM tblJournalLines L
            JOIN tblJournal J ON J.iJournalID = L.iJournalID
            WHERE L.iJournalLineID = X.iJournalLineID AND J.iUserID = @iUserId
              AND L.iAccountID = @iAccountID AND J.dtmTransactionDate <= @dtStatementDate))
    BEGIN
        SELECT 'Error: Some ticked lines aren''t on this account on or before the statement date. Refresh the page and try again.' AS Result,
               NULL AS ReconciliationId;
        RETURN;
    END

    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1 FROM @Lines X
        JOIN tblBankReconciliationLine RL WITH (UPDLOCK, HOLDLOCK) ON RL.iJournalLineID = X.iJournalLineID
        WHERE RL.iReconciliationID <> ISNULL(@iReconciliationID, -1))
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT 'Error: Some ticked lines are already reconciled on another statement. Refresh the page and try again.' AS Result,
               NULL AS ReconciliationId;
        RETURN;
    END

    IF @iReconciliationID IS NULL
    BEGIN
        INSERT INTO tblBankReconciliation (iUserID, iAccountID, dtmStatementDate, dblOpeningBalance, dblStatementBalance)
        VALUES (@iUserId, @iAccountID, @dtStatementDate, @dblOpeningBalance, @dblStatementBalance);
        SET @iReconciliationID = SCOPE_IDENTITY();
    END
    ELSE
        UPDATE tblBankReconciliation
        SET dtmStatementDate = @dtStatementDate,
            dblOpeningBalance = @dblOpeningBalance,
            dblStatementBalance = @dblStatementBalance
        WHERE iReconciliationID = @iReconciliationID;

    DELETE FROM tblBankReconciliationLine WHERE iReconciliationID = @iReconciliationID;
    INSERT INTO tblBankReconciliationLine (iJournalLineID, iReconciliationID)
    SELECT iJournalLineID, @iReconciliationID FROM @Lines;

    IF @bComplete = 1
    BEGIN
        DECLARE @Ticked DECIMAL(19,4) = (
            SELECT ISNULL(SUM(ISNULL(L.dblDebit, 0) - ISNULL(L.dblCredit, 0)), 0)
            FROM @Lines X JOIN tblJournalLines L ON L.iJournalLineID = X.iJournalLineID);

        IF @dblOpeningBalance + @Ticked <> @dblStatementBalance
        BEGIN
            ROLLBACK TRANSACTION;
            SELECT 'Error: It can''t be finished while there is a difference of '
                   + FORMAT(@dblStatementBalance - @dblOpeningBalance - @Ticked, 'N2') + '.' AS Result, NULL AS ReconciliationId;
            RETURN;
        END

        UPDATE tblBankReconciliation SET dtmCompletedDate = GETDATE() WHERE iReconciliationID = @iReconciliationID;
    END

    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iReconciliationID AS ReconciliationId;
END
GO

-- =============================================
-- Reopening the latest finished reconciliation
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspReopenBankReconciliation]
(
    @iUserId           INT,
    @iReconciliationID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Reopens a finished reconciliation so it can be changed. Only
--              the account's latest one, and only while no other is in
--              progress. Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @iAccountID INT;
    SELECT @iAccountID = iAccountID FROM tblBankReconciliation
    WHERE iReconciliationID = @iReconciliationID AND iUserID = @iUserId AND dtmCompletedDate IS NOT NULL;

    IF @iAccountID IS NULL
    BEGIN
        SELECT 'Error: That finished reconciliation was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblBankReconciliation WHERE iUserID = @iUserId AND iAccountID = @iAccountID AND dtmCompletedDate IS NULL)
    BEGIN
        SELECT 'Error: Another reconciliation for this account is in progress. Finish or delete it first.' AS Result;
        RETURN;
    END

    IF EXISTS (
        SELECT 1 FROM tblBankReconciliation R
        JOIN tblBankReconciliation Me ON Me.iReconciliationID = @iReconciliationID
        WHERE R.iUserID = @iUserId AND R.iAccountID = @iAccountID AND R.iReconciliationID <> @iReconciliationID
          AND R.dtmStatementDate >= Me.dtmStatementDate)
    BEGIN
        SELECT 'Error: Only the latest reconciliation for the account can be reopened.' AS Result;
        RETURN;
    END

    UPDATE tblBankReconciliation SET dtmCompletedDate = NULL WHERE iReconciliationID = @iReconciliationID;
    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Deleting a reconciliation in progress
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspDeleteBankReconciliation]
(
    @iUserId           INT,
    @iReconciliationID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes a reconciliation that isn't finished, and its ticks.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblBankReconciliation
                   WHERE iReconciliationID = @iReconciliationID AND iUserID = @iUserId AND dtmCompletedDate IS NULL)
    BEGIN
        SELECT 'Error: That reconciliation was not found, or it is finished (reopen it first).' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE FROM tblBankReconciliationLine WHERE iReconciliationID = @iReconciliationID;
    DELETE FROM tblBankReconciliation WHERE iReconciliationID = @iReconciliationID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO
