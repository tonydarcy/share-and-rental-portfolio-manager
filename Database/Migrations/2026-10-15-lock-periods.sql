-- =============================================
-- Migration: lock periods
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database AFTER 2026-10-15-realised-gains.sql
-- (and after 2026-10-15-bank-reconciliation.sql if you use it), BEFORE
-- deploying the site build with Accounting > Lock periods.
-- Safe to run more than once.
--
-- A portfolio locked "up to" a date can't have anything dated on or before it
-- added, changed or deleted, by the new site, the old site or SSMS. The
-- database itself refuses, with error 50300, through triggers on:
--   * tblJournal and tblJournalLines: every journal (trades, payouts, cash,
--     bank imports and allocations, realised gains, revaluations, manual)
--   * tblShareTrades: adding or deleting a trade, or changing its date, type,
--     code, total or brokerage (a share split in a later year still adjusts
--     the units and price of earlier trades, as it must)
--   * tblPayouts and tblCorporateActions: adding, changing or deleting
--   * tblBankStatement: adding or deleting, or changing the date or amount
--   * tblBankReconciliation: statements dated in the locked period
-- Until a portfolio is locked nothing changes for it.
--
-- What it adds:
--   * tblLockedPeriod (one row per locked portfolio) and tblLockedPeriodHistory
--   * fnLockedTo(@iUserID): the lock date, or NULL
--   * uspGetLockedPeriod, uspSetLockedPeriod, uspGetLockedPeriodHistory
--   * uspGetSaleJournals: leaves out sales in the locked period, so the site
--     stops reposting their realised gains
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.uspGetSaleJournals', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-realised-gains.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblLockedPeriod', 'U') IS NULL
    CREATE TABLE dbo.tblLockedPeriod (
        iUserID          INT NOT NULL CONSTRAINT PK_tblLockedPeriod PRIMARY KEY,
        dtmLockedTo      DATE NOT NULL,
        dtmChangedDate   DATETIME NOT NULL CONSTRAINT DF_tblLockedPeriod_Changed DEFAULT (GETDATE()),
        iChangedByUserID INT NULL
    );
GO

IF OBJECT_ID('dbo.tblLockedPeriodHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblLockedPeriodHistory (
        iHistoryID       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblLockedPeriodHistory PRIMARY KEY,
        iUserID          INT NOT NULL,
        dtmLockedTo      DATE NULL,      -- NULL: the lock was removed
        dtmChangedDate   DATETIME NOT NULL CONSTRAINT DF_tblLockedPeriodHistory_Changed DEFAULT (GETDATE()),
        iChangedByUserID INT NULL
    );
    CREATE INDEX IX_tblLockedPeriodHistory_User ON dbo.tblLockedPeriodHistory (iUserID, dtmChangedDate);
END
GO

CREATE OR ALTER FUNCTION dbo.fnLockedTo (@iUserID INT)
RETURNS DATE
AS
BEGIN
    RETURN (SELECT dtmLockedTo FROM dbo.tblLockedPeriod WHERE iUserID = @iUserID);
END
GO

-- =============================================
-- The triggers. Each finds the first row dated in its portfolio's locked
-- period, among the rows added (inserted) and the rows removed or replaced
-- (deleted), and refuses the whole statement.
-- =============================================
CREATE OR ALTER TRIGGER dbo.trgJournalLock ON dbo.tblJournal
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (SELECT iUserID, dtmTransactionDate FROM inserted UNION ALL SELECT iUserID, dtmTransactionDate FROM deleted) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserID
        WHERE X.dtmTransactionDate <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That change is in a locked period (the ledger is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

CREATE OR ALTER TRIGGER dbo.trgJournalLinesLock ON dbo.tblJournalLines
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (SELECT iJournalID FROM inserted UNION ALL SELECT iJournalID FROM deleted) X
        JOIN dbo.tblJournal J ON J.iJournalID = X.iJournalID
        JOIN dbo.tblLockedPeriod P ON P.iUserID = J.iUserID
        WHERE J.dtmTransactionDate <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That change is in a locked period (the ledger is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

CREATE OR ALTER TRIGGER dbo.trgShareTradesLock ON dbo.tblShareTrades
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (
            -- Added or deleted rows, and updates that change what the trade was
            SELECT ISNULL(I.iUserId, D.iUserId) AS iUserId, ISNULL(I.dtmDateOfTransaction, D.dtmDateOfTransaction) AS dtDate, D.dtmDateOfTransaction AS dtOld
            FROM inserted I
            FULL JOIN deleted D ON D.iShareTradeId = I.iShareTradeId
            WHERE I.iShareTradeId IS NULL OR D.iShareTradeId IS NULL
               OR I.dtmDateOfTransaction <> D.dtmDateOfTransaction
               OR I.iUserId <> D.iUserId
               OR UPPER(I.strType) <> UPPER(D.strType)
               OR I.strStockCode <> D.strStockCode
               OR I.strStockExchangeCode <> D.strStockExchangeCode
               OR ISNULL(I.dblOrderTotal, 0) <> ISNULL(D.dblOrderTotal, 0)
               OR ISNULL(I.dblBrokerage, 0) <> ISNULL(D.dblBrokerage, 0)
        ) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserId
        WHERE X.dtDate <= P.dtmLockedTo OR X.dtOld <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That trade is in a locked period (the portfolio is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

CREATE OR ALTER TRIGGER dbo.trgPayoutsLock ON dbo.tblPayouts
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (SELECT iUserID, dtmTransactionDate FROM inserted UNION ALL SELECT iUserID, dtmTransactionDate FROM deleted) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserID
        WHERE CAST(X.dtmTransactionDate AS DATE) <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That payout is in a locked period (the portfolio is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

CREATE OR ALTER TRIGGER dbo.trgCorporateActionsLock ON dbo.tblCorporateActions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (SELECT iUserID, dtmActionDate FROM inserted UNION ALL SELECT iUserID, dtmActionDate FROM deleted) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserID
        WHERE X.dtmActionDate <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That share split is in a locked period (the portfolio is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

CREATE OR ALTER TRIGGER dbo.trgBankStatementLock ON dbo.tblBankStatement
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (
            -- Running closing balances are recalculated by the procs, so only these changes count
            SELECT ISNULL(I.iUserId, D.iUserId) AS iUserId, ISNULL(I.dtmDateOfTransaction, D.dtmDateOfTransaction) AS dtDate, D.dtmDateOfTransaction AS dtOld
            FROM inserted I
            FULL JOIN deleted D ON D.iBankStatementId = I.iBankStatementId
            WHERE I.iBankStatementId IS NULL OR D.iBankStatementId IS NULL
               OR ISNULL(I.dtmDateOfTransaction, '19000101') <> ISNULL(D.dtmDateOfTransaction, '19000101')
               OR ISNULL(I.iUserId, 0) <> ISNULL(D.iUserId, 0)
               OR ISNULL(I.dblWithdrawalAmount, 0) <> ISNULL(D.dblWithdrawalAmount, 0)
               OR ISNULL(I.dblDepositAmount, 0) <> ISNULL(D.dblDepositAmount, 0)
        ) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserId
        WHERE X.dtDate <= P.dtmLockedTo OR X.dtOld <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N'That cash transaction is in a locked period (the portfolio is locked up to ' + CONVERT(NVARCHAR(11), @dt, 106)
            + N'), so nothing was saved. Unlock the year on Accounting > Lock periods first.';
        THROW 50300, @msg, 1;
    END
END
GO

IF OBJECT_ID('dbo.tblBankReconciliation', 'U') IS NOT NULL
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.trgBankReconciliationLock ON dbo.tblBankReconciliation
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblLockedPeriod) RETURN;

    DECLARE @dt DATE = (
        SELECT TOP 1 P.dtmLockedTo
        FROM (SELECT iUserID, dtmStatementDate FROM inserted UNION ALL SELECT iUserID, dtmStatementDate FROM deleted) X
        JOIN dbo.tblLockedPeriod P ON P.iUserID = X.iUserID
        WHERE X.dtmStatementDate <= P.dtmLockedTo);

    IF @dt IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(400) = N''That bank statement is in a locked period (the ledger is locked up to '' + CONVERT(NVARCHAR(11), @dt, 106)
            + N''), so nothing was saved. Unlock the year on Accounting > Lock periods first.'';
        THROW 50300, @msg, 1;
    END
END');
GO

-- =============================================
-- Reading and setting the lock
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetLockedPeriod]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The portfolio's lock: dtmLockedTo, dtmChangedDate and who set
--              it (strChangedBy, their login email). No row when unlocked.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT P.dtmLockedTo, P.dtmChangedDate, U.strLoginEmail AS strChangedBy
    FROM tblLockedPeriod P
    LEFT JOIN tblUsers U ON U.iUserId = P.iChangedByUserID
    WHERE P.iUserID = @iUserId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSetLockedPeriod]
(
    @iUserId          INT,
    @dtLockedTo       DATE = NULL,   -- NULL removes the lock
    @iChangedByUserID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Locks the portfolio up to and including @dtLockedTo, moves the
--              lock, or removes it, and records the change in the history.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblUsers WHERE iUserId = @iUserId)
    BEGIN
        SELECT 'Error: That portfolio was not found.' AS Result;
        RETURN;
    END

    IF @dtLockedTo IS NOT NULL AND @dtLockedTo >= CAST(GETDATE() AS DATE)
    BEGIN
        SELECT 'Error: Only a period that has ended can be locked.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    IF @dtLockedTo IS NULL
        DELETE FROM tblLockedPeriod WHERE iUserID = @iUserId;
    ELSE IF EXISTS (SELECT 1 FROM tblLockedPeriod WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserId)
        UPDATE tblLockedPeriod
        SET dtmLockedTo = @dtLockedTo, dtmChangedDate = GETDATE(), iChangedByUserID = @iChangedByUserID
        WHERE iUserID = @iUserId;
    ELSE
        INSERT INTO tblLockedPeriod (iUserID, dtmLockedTo, iChangedByUserID)
        VALUES (@iUserId, @dtLockedTo, @iChangedByUserID);

    INSERT INTO tblLockedPeriodHistory (iUserID, dtmLockedTo, iChangedByUserID)
    VALUES (@iUserId, @dtLockedTo, @iChangedByUserID);

    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetLockedPeriodHistory]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Every lock change for the portfolio, newest first.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT H.dtmLockedTo, H.dtmChangedDate, U.strLoginEmail AS strChangedBy
    FROM tblLockedPeriodHistory H
    LEFT JOIN tblUsers U ON U.iUserId = H.iChangedByUserID
    WHERE H.iUserID = @iUserId
    ORDER BY H.dtmChangedDate DESC, H.iHistoryID DESC;
END
GO

-- =============================================
-- uspGetSaleJournals: same columns, without sales in the locked period
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetSaleJournals]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: One row per sale journal (strSourceModule 'ShareTrade' on a
--              SELL trade): iShareTradeId, iJournalID, CostBaseLines (how many
--              lines post to a "- Cost Base" account), CostPosted (net credit
--              to that account) and GainPosted (net credit of the lines
--              uspPostSaleGain added). Nothing is written.
-- Version 2:   Leaves out sales dated in the locked period (fnLockedTo), so
--              their realised gains are never reposted.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @dtLockedTo DATE = dbo.fnLockedTo(@iUserId);

    SELECT
        T.iShareTradeId,
        J.iJournalID,
        SUM(CASE WHEN A.strAccountName LIKE '% - Cost Base' AND ISNULL(L.strDescription, '') <> 'Realised gain or loss on sale (FIFO)' THEN 1 ELSE 0 END) AS CostBaseLines,
        SUM(CASE WHEN A.strAccountName LIKE '% - Cost Base' AND ISNULL(L.strDescription, '') <> 'Realised gain or loss on sale (FIFO)'
                 THEN ISNULL(L.dblCredit, 0) - ISNULL(L.dblDebit, 0) ELSE 0 END) AS CostPosted,
        SUM(CASE WHEN L.strDescription = 'Realised gain or loss on sale (FIFO)'
                 THEN ISNULL(L.dblCredit, 0) - ISNULL(L.dblDebit, 0) ELSE 0 END) AS GainPosted
    FROM tblShareTrades T
    JOIN tblJournal J ON J.iUserID = T.iUserId AND J.strSourceModule = 'ShareTrade' AND J.iSourceID = T.iShareTradeId
    JOIN tblJournalLines L ON L.iJournalID = J.iJournalID
    JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
    WHERE T.iUserId = @iUserId AND UPPER(LTRIM(RTRIM(T.strType))) = 'SELL'
      AND (@dtLockedTo IS NULL OR J.dtmTransactionDate > @dtLockedTo)
    GROUP BY T.iShareTradeId, J.iJournalID;
END
GO
