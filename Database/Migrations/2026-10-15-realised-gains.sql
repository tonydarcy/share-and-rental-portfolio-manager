-- =============================================
-- Migration: realised gains and losses on sale posted to the ledger
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database AFTER 2026-10-15-ledger-fixes.sql
-- and BEFORE deploying the site build that posts realised gains.
-- The old site keeps working: it only adds an account and two procedures.
-- Safe to run more than once.
--
-- What it changes:
--   * Adds system account 4500 Realised Gains on Investments (Revenue).
--   * uspGetSaleJournals (new): each sale's journal with the cost and the
--     gain or loss it posts, so the site can see which ones are out of date.
--   * uspPostSaleGain (new): rewrites one sale's journal (Hybrid / DoubleEntry)
--     from the FIFO cost of the parcels sold, which the site works out:
--       Dr  Trade Clearing / Suspense       proceeds        (unchanged)
--       Cr  <CODE>.<EXCH> - Cost Base       FIFO cost
--       Cr  4500 Realised Gains             gain            (or Dr for a loss)
--     uspAddShareTrade still posts proceeds to the cost base account; the site
--     moves the gain out the next time the ledger is read.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Pre-flight
-- =============================================
IF OBJECT_ID('dbo.uspGetLedgerAccount', 'P') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-ledger-fixes.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber = 4500 AND strAccountType <> 'Revenue')
BEGIN
    SELECT iAccountID, iAccountNumber, strAccountName, strAccountType
    FROM tblChartOfAccounts WHERE iUserID = 0 AND iAccountNumber = 4500;
    RAISERROR('Stopped: account 4500 is already used for an account that isn''t Revenue (listed above). Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- =============================================
-- 2. The realised gains account
-- =============================================
DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 4500, 'Realised Gains on Investments', 'Revenue', @iAccountID OUTPUT;
SELECT iAccountID, iAccountNumber, strAccountName, strAccountType
FROM tblChartOfAccounts WHERE iAccountID = @iAccountID;
GO

-- =============================================
-- 3. Sale journals and what they post
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
-- =============================================
BEGIN
    SET NOCOUNT ON;

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
    GROUP BY T.iShareTradeId, J.iJournalID;
END
GO

-- =============================================
-- 4. Posting one sale's gain or loss
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspPostSaleGain]
(
    @iUserId      INT,
    @iJournalID   INT,
    @dblCostBase  DECIMAL(19,4) = NULL  -- FIFO cost of the units sold; NULL puts the journal back to proceeds only
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Rewrites a sale's journal so its cost base account is credited
--              with the FIFO cost of the units sold and the difference from
--              the proceeds goes to 4500 Realised Gains on Investments (a
--              credit for a gain, a debit for a loss). The proceeds lines are
--              not touched, so the journal still balances. Running it again
--              with the same cost changes nothing. Returns one row: Result =
--              'Sale Gain Posted' or a message starting with 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Marker VARCHAR(500) = 'Realised gain or loss on sale (FIFO)';

    IF NOT EXISTS (
        SELECT 1
        FROM tblJournal J
        JOIN tblShareTrades T ON T.iShareTradeId = J.iSourceID AND T.iUserId = J.iUserID
        WHERE J.iJournalID = @iJournalID AND J.iUserID = @iUserId
          AND J.strSourceModule = 'ShareTrade' AND UPPER(LTRIM(RTRIM(T.strType))) = 'SELL')
    BEGIN
        SELECT 'Error: That sale journal was not found in this portfolio.' AS Result;
        RETURN;
    END

    DECLARE @iGainAccountID INT;
    EXEC dbo.uspGetLedgerAccount @iUserId, 4500, 'Realised Gains on Investments', 'Revenue', @iGainAccountID OUTPUT;

    BEGIN TRANSACTION;

    DECLARE @Lock VARCHAR(255) = 'uspPostSaleGain:' + CAST(@iJournalID AS VARCHAR(20));
    EXEC sp_getapplock @Resource = @Lock, @LockMode = 'Exclusive', @LockOwner = 'Transaction';

    DECLARE @iStockLineID INT, @iStockLines INT;
    SELECT @iStockLines = COUNT(*), @iStockLineID = MIN(L.iJournalLineID)
    FROM tblJournalLines L
    JOIN tblChartOfAccounts A ON A.iAccountID = L.iAccountID
    WHERE L.iJournalID = @iJournalID AND A.strAccountName LIKE '% - Cost Base'
      AND ISNULL(L.strDescription, '') <> @Marker;

    IF @iStockLines <> 1
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT 'Error: The sale journal doesn''t have exactly one cost base line, so it was left as it is.' AS Result;
        RETURN;
    END

    DELETE FROM tblJournalLines WHERE iJournalID = @iJournalID AND strDescription = @Marker;

    -- The proceeds: what the other lines (the clearing account) debit
    DECLARE @Proceeds DECIMAL(19,4) = (
        SELECT ISNULL(SUM(ISNULL(dblDebit, 0) - ISNULL(dblCredit, 0)), 0)
        FROM tblJournalLines
        WHERE iJournalID = @iJournalID AND iJournalLineID <> @iStockLineID);

    DECLARE @Cost DECIMAL(19,4) = ISNULL(@dblCostBase, @Proceeds);
    DECLARE @Gain DECIMAL(19,4) = @Proceeds - @Cost;

    UPDATE tblJournalLines
    SET dblDebit = CASE WHEN @Cost < 0 THEN -@Cost ELSE 0 END,
        dblCredit = CASE WHEN @Cost >= 0 THEN @Cost ELSE 0 END
    WHERE iJournalLineID = @iStockLineID;

    IF @Gain <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        VALUES (@iJournalID, @iGainAccountID,
                CASE WHEN @Gain < 0 THEN -@Gain ELSE 0 END,
                CASE WHEN @Gain > 0 THEN @Gain ELSE 0 END,
                @Marker);

    COMMIT TRANSACTION;
    SELECT 'Sale Gain Posted' AS Result;
END
GO
