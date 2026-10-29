-- =============================================
-- Migration: Deleting everything switches a single-entry portfolio to double entry (29-Oct-2026).
-- After Delete data > Everything nothing is left without a journal, so uspDeletePortfolioData also sets the
-- portfolio to DoubleEntry and whatever is entered next posts to the ledger. Safe to run twice. Needs
-- 2026-10-29-delete-sync-records.sql first.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeletePortfolioData]
(
    @iUserId      INT,
    @iPortfolioId INT,
    @strPart      VARCHAR(20)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Permanently deletes one part of a portfolio, for an owner (fnCanDeletePortfolio). Every journal
--              is deleted whole, so the ledger still balances. Anything dated in a locked period stops the
--              whole delete (the locked period triggers). Parts:
--                Trades   - every share trade with its journal and settlement cash row, corporate actions and
--                           splits, returns of capital, parcel choices and transfers, CGT parcel matches.
--                           Payouts stay, no longer linked to a reinvestment trade.
--                Payouts  - every payout (dividends, distributions, interest, other income) with its journal,
--                           and the trades dividend reinvestment created.
--                Cash     - every cash transaction that isn't a trade's settlement, with its journal.
--                Journals - every journal (the whole ledger), bank reconciliations, FX revaluations and stock
--                           adjustments. Trades, payouts, cash, invoices and bills stay, shown as not posted.
--                Prices   - prices entered by hand for this portfolio.
--                All      - everything recorded, keeping the portfolio, its settings, chart of accounts and sharing.
--              Ticks on bank reconciliations for deleted journals are removed. Returns one row:
--              Result = 'Deleted' or a message starting with 'Error:'.
--              29-Oct-2026: Navexa sync records of deleted trades, payouts or cash are forgotten, so a re-sync adds them again.
--              29-Oct-2026: All also switches a single-entry portfolio to double entry, as there is nothing left to post.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF dbo.fnCanDeletePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: Only an owner of this portfolio can delete its data.' AS Result;
        RETURN;
    END
    IF @strPart NOT IN ('Trades', 'Payouts', 'Cash', 'Journals', 'Prices', 'All')
    BEGIN
        SELECT 'Error: Choose what to delete.' AS Result;
        RETURN;
    END

    DECLARE @Trades TABLE (id INT PRIMARY KEY);
    DECLARE @Cash TABLE (id INT PRIMARY KEY);
    DECLARE @Journals TABLE (id INT PRIMARY KEY);

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @strPart = 'Trades'
        BEGIN
            INSERT INTO @Trades SELECT iShareTradeId FROM tblShareTrades WHERE iUserId = @iPortfolioId;
            INSERT INTO @Cash SELECT iBankStatementId FROM tblBankStatement WHERE iUserId = @iPortfolioId AND iShareTradeId IN (SELECT id FROM @Trades);
            INSERT INTO @Journals SELECT iJournalID FROM tblJournal
            WHERE iUserID = @iPortfolioId
              AND ((strSourceModule = 'ShareTrade' AND iSourceID IN (SELECT id FROM @Trades))
                OR (strSourceModule = 'BankTransaction' AND iSourceID IN (SELECT id FROM @Cash))
                OR strSourceModule IN ('CorporateAction', 'ReturnOfCapital'));

            UPDATE tblPayouts SET iReinvestmentTradeID = NULL WHERE iUserID = @iPortfolioId AND iReinvestmentTradeID IS NOT NULL;
            DELETE FROM tblCGTParcelMatching WHERE iUserID = @iPortfolioId;
            DELETE FROM tblSaleParcelAllocation WHERE iUserID = @iPortfolioId;
            DELETE FROM tblSaleParcelChoice WHERE iUserID = @iPortfolioId;
            DELETE FROM tblParcelTransfers WHERE iUserID = @iPortfolioId;
            DELETE FROM tblCorporateActions WHERE iUserID = @iPortfolioId;
            DELETE FROM tblReturnsOfCapital WHERE iUserID = @iPortfolioId;
            DELETE FROM tblShareTradeGroups WHERE iShareTradeID IN (SELECT id FROM @Trades);
        END
        ELSE IF @strPart = 'Payouts'
        BEGIN
            INSERT INTO @Trades SELECT DISTINCT iReinvestmentTradeID FROM tblPayouts WHERE iUserID = @iPortfolioId AND iReinvestmentTradeID IS NOT NULL;
            INSERT INTO @Journals SELECT iJournalID FROM tblJournal
            WHERE iUserID = @iPortfolioId
              AND (strSourceModule = 'Payout' OR (strSourceModule = 'ShareTrade' AND iSourceID IN (SELECT id FROM @Trades)));

            -- The reinvestment trades' CGT matches are worked out again when the reports next run
            DELETE FROM tblCGTParcelMatching WHERE iUserID = @iPortfolioId
              AND (iBuyTradeID IN (SELECT id FROM @Trades) OR iSellTradeID IN (SELECT id FROM @Trades));
            DELETE FROM tblSaleParcelAllocation WHERE iUserID = @iPortfolioId AND iBuyTradeID IN (SELECT id FROM @Trades);
            UPDATE tblParcelTransfers SET iBuyTradeID = NULL WHERE iUserID = @iPortfolioId AND iBuyTradeID IN (SELECT id FROM @Trades);
            DELETE FROM tblShareTradeGroups WHERE iShareTradeID IN (SELECT id FROM @Trades);
        END
        ELSE IF @strPart = 'Cash'
        BEGIN
            INSERT INTO @Cash SELECT iBankStatementId FROM tblBankStatement WHERE iUserId = @iPortfolioId AND ISNULL(iShareTradeId, 0) = 0;
            INSERT INTO @Journals SELECT iJournalID FROM tblJournal
            WHERE iUserID = @iPortfolioId AND strSourceModule = 'BankTransaction' AND iSourceID IN (SELECT id FROM @Cash);
            UPDATE tblShareTrades SET iBankStatementId = NULL WHERE iUserId = @iPortfolioId AND iBankStatementId IN (SELECT id FROM @Cash);
        END
        ELSE IF @strPart = 'Journals'
        BEGIN
            INSERT INTO @Journals SELECT iJournalID FROM tblJournal WHERE iUserID = @iPortfolioId;
            DELETE L FROM tblBankReconciliationLine L JOIN tblBankReconciliation R ON R.iReconciliationID = L.iReconciliationID WHERE R.iUserID = @iPortfolioId;
            DELETE FROM tblBankReconciliation WHERE iUserID = @iPortfolioId;
            DELETE FROM tblFxRevaluations WHERE iUserID = @iPortfolioId;
            DELETE FROM tblStockAdjustments WHERE iUserID = @iPortfolioId;
        END
        ELSE IF @strPart = 'Prices'
        BEGIN
            -- and their copies among the latest prices
            DELETE FROM tblSharePriceCache WHERE iUserID = @iPortfolioId;
            DELETE FROM tblSharePrice WHERE iUserID = @iPortfolioId;
        END
        ELSE IF @strPart = 'All'
        BEGIN
            EXEC dbo.uspPurgePortfolio @iPortfolioId, 1;
            -- Nothing is left to post, so a fresh start is double entry like every new portfolio
            UPDATE tblUsers SET strAccountingMode = 'DoubleEntry'
            WHERE iUserId = @iPortfolioId AND ISNULL(strAccountingMode, 'SingleEntry') <> 'DoubleEntry';
        END

        IF EXISTS (SELECT 1 FROM @Journals)
        BEGIN
            -- Untick them on bank reconciliations, then delete them whole (their lines cascade)
            DELETE R FROM tblBankReconciliationLine R JOIN tblJournalLines L ON L.iJournalLineID = R.iJournalLineID
            WHERE L.iJournalID IN (SELECT id FROM @Journals);
            DELETE FROM tblJournal WHERE iUserID = @iPortfolioId AND iJournalID IN (SELECT id FROM @Journals);
            EXEC dbo.uspClearDeletedJournalLinks @iPortfolioId;
        END

        IF @strPart = 'Payouts'
            DELETE FROM tblPayouts WHERE iUserID = @iPortfolioId;
        IF EXISTS (SELECT 1 FROM @Trades)
            DELETE FROM tblShareTrades WHERE iUserId = @iPortfolioId AND iShareTradeId IN (SELECT id FROM @Trades);
        IF EXISTS (SELECT 1 FROM @Cash)
        BEGIN
            DELETE FROM tblBankStatement WHERE iUserId = @iPortfolioId AND iBankStatementId IN (SELECT id FROM @Cash);
            EXEC dbo.uspRecalcBankBalances @iPortfolioId;
        END

        -- The Navexa sync forgets the records it added that were just deleted, so its next run brings them back
        -- rather than skipping them as already added. ('All' clears the sync's records with everything else.)
        IF OBJECT_ID('dbo.tblNavexaSyncItem', 'U') IS NOT NULL AND @strPart IN ('Trades', 'Payouts', 'Cash')
            DELETE FROM dbo.tblNavexaSyncItem
            WHERE iUserId = @iPortfolioId AND strStatus = 'Imported'
              AND ((@strPart = 'Trades' AND strKind IN ('Trade', 'Split', 'CapitalReturn', 'Swap'))
                OR (@strPart = 'Payouts' AND strKind IN ('Payout', 'Drp', 'Reward'))
                OR (@strPart = 'Cash' AND strKind = 'Cash'));

        COMMIT TRANSACTION;
        SELECT 'Deleted' AS Result;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        SELECT 'Error: ' + ERROR_MESSAGE() AS Result;
    END CATCH
END
GO
