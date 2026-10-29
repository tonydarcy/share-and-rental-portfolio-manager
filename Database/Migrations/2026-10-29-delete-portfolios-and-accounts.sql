-- =============================================
-- Migration: permanently deleting data, a portfolio or an account
-- Date:      29-Oct-2026
-- Adds the procs behind Portfolio > Delete data and Account > Delete account. Nothing is deleted by
-- running this script. Safe to run twice.
--
--   * fnCanDeletePortfolio: only the portfolio's own login or someone given owner access to that portfolio
--     itself. The "every portfolio" row a site administrator has is not enough to delete someone's data.
--   * fnIsLoginRow: whether a tblUsers row is the one an account signs in with (the oldest row with its email).
--   * fnLoginDeletePlan: the portfolios an account created, who else uses each, and so whether deleting the
--     account deletes the portfolio or leaves it with the others.
--   * uspPurgePortfolio (internal): deletes everything recorded for one portfolio, or with @bKeepSettings = 1
--     everything except its settings, chart of accounts, tax codes, sharing, connections and audit trail.
--   * uspRecalcBankBalances (internal): the running balances of every bank account of a portfolio.
--   * uspClearDeletedJournalLinks (internal): clears links from invoices, bills, imported bank lines and so on
--     to journals that no longer exist, so they show as not posted or not matched.
--   * uspDeletePortfolioData: one part of a portfolio (all share trades, all payouts, all cash transactions,
--     every journal, prices entered by hand) or everything in it. Locked periods are respected.
--   * uspDeletePortfolio: a whole portfolio that isn't an account's own sign-in portfolio.
--   * uspDeleteLogin: an account. Portfolios nobody else uses are deleted; portfolios others still use are
--     kept for them with the account's sign-in details removed.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnAccessLevel', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-18-shared-access.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

-- Deleting needs the portfolio's own login row or a direct owner grant to this portfolio (not the all-portfolios row)
CREATE OR ALTER FUNCTION [dbo].[fnCanDeletePortfolio] (@iUserId INT, @iPortfolioId INT)
RETURNS BIT
AS
BEGIN
    IF @iUserId = @iPortfolioId
        RETURN 1;
    IF EXISTS (SELECT 1 FROM tblPortfolioPermissions
               WHERE iUserID = @iUserId AND iUserIDForPortfolioToAccess = @iPortfolioId
                 AND dbo.fnAccessLevel(strAccessDescription) = 2)
        RETURN 1;
    RETURN 0;
END
GO

-- The row an account signs in with: uspGetLoginDetails picks the oldest row with the email
CREATE OR ALTER FUNCTION [dbo].[fnIsLoginRow] (@iUserId INT)
RETURNS BIT
AS
BEGIN
    IF EXISTS (SELECT 1 FROM tblUsers U
               WHERE U.iUserId = @iUserId AND LTRIM(RTRIM(U.strLoginEmail)) <> ''
                 AND NOT EXISTS (SELECT 1 FROM tblUsers O WHERE O.strLoginEmail = U.strLoginEmail AND O.iUserId < U.iUserId))
        RETURN 1;
    RETURN 0;
END
GO

-- One row per portfolio the account created (every row with its email, its own first). OthersWithAccess counts the
-- other logins with access to it; OtherOwners those of them who are owners. No others means it is deleted with the account.
CREATE OR ALTER FUNCTION [dbo].[fnLoginDeletePlan] (@iUserId INT)
RETURNS TABLE
AS
RETURN
    SELECT U.iUserId AS PortfolioId, U.strEntityName AS PortfolioName,
           (SELECT COUNT(DISTINCT P.iUserID) FROM tblPortfolioPermissions P JOIN tblUsers G ON G.iUserId = P.iUserID
            WHERE P.iUserIDForPortfolioToAccess = U.iUserId AND G.strLoginEmail <> L.strLoginEmail) AS OthersWithAccess,
           (SELECT COUNT(DISTINCT P.iUserID) FROM tblPortfolioPermissions P JOIN tblUsers G ON G.iUserId = P.iUserID
            WHERE P.iUserIDForPortfolioToAccess = U.iUserId AND G.strLoginEmail <> L.strLoginEmail
              AND dbo.fnAccessLevel(P.strAccessDescription) = 2) AS OtherOwners
    FROM tblUsers L
    JOIN tblUsers U ON U.strLoginEmail = L.strLoginEmail
    WHERE L.iUserId = @iUserId AND LTRIM(RTRIM(L.strLoginEmail)) <> '';
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRecalcBankBalances]
(
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Recalculates the running balance (dblClosingBalance) of every bank account in a portfolio,
--              after cash transactions have been deleted in bulk.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    ;WITH RunningBalances AS (
        SELECT
            dblClosingBalance AS CurrentStoredBalance,
            SUM(ISNULL(dblDepositAmount, 0) - ISNULL(dblWithdrawalAmount, 0)) OVER (
                PARTITION BY iUserId, strBankAccountName, strCurrency
                ORDER BY dtmDateOfTransaction ASC, iBankStatementId ASC
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS CalculatedRunningBalance
        FROM tblBankStatement
        WHERE iUserId = @iPortfolioId
    )
    UPDATE RunningBalances
    SET CurrentStoredBalance = CalculatedRunningBalance
    WHERE CurrentStoredBalance IS NULL OR CurrentStoredBalance <> CalculatedRunningBalance;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspClearDeletedJournalLinks]
(
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: After journals are deleted in bulk, clears the links that pointed at them: imported bank
--              lines matched to them become unmatched, and invoices, bills, payments, claims, depreciation
--              runs, asset disposals and client queries show as not posted.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    UPDATE L SET iMatchedJournalID = NULL
    FROM tblJournalLines L JOIN tblJournal J ON J.iJournalID = L.iJournalID
    WHERE J.iUserID = @iPortfolioId AND L.iMatchedJournalID IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM tblJournal M WHERE M.iJournalID = L.iMatchedJournalID);

    UPDATE tblInvoices SET iJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblInvoices.iJournalID);
    UPDATE P SET iJournalID = NULL
    FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
    WHERE I.iUserID = @iPortfolioId AND P.iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = P.iJournalID);
    UPDATE tblBills SET iJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblBills.iJournalID);
    UPDATE P SET iJournalID = NULL
    FROM tblBillPayments P JOIN tblBills B ON B.iBillID = P.iBillID
    WHERE B.iUserID = @iPortfolioId AND P.iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = P.iJournalID);
    UPDATE tblExpenseClaims SET iJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblExpenseClaims.iJournalID);
    UPDATE tblExpenseClaims SET iPaymentJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iPaymentJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblExpenseClaims.iPaymentJournalID);
    UPDATE tblDepreciationRuns SET iJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblDepreciationRuns.iJournalID);
    UPDATE tblFixedAssets SET iDisposalJournalID = NULL
    WHERE iUserID = @iPortfolioId AND iDisposalJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblFixedAssets.iDisposalJournalID);
    UPDATE tblClientQueries SET iJournalID = NULL, iJournalLineID = NULL
    WHERE iUserID = @iPortfolioId AND iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblJournal J WHERE J.iJournalID = tblClientQueries.iJournalID);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspPurgePortfolio]
(
    @iPortfolioId  INT,
    @bKeepSettings BIT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Internal, called only by uspDeletePortfolioData, uspDeletePortfolio and uspDeleteLogin, which
--              check access and hold the transaction. Deletes every row recorded for a portfolio: first the
--              rows that only point at it through a parent (journal lines, payments, rental tenancies...),
--              then every table with an iUserID or iPortfolioID column, so tables added later are included.
--              Tables other tables point at go last, and the chart of accounts after the journals.
--              @bKeepSettings = 1 keeps the portfolio itself, its settings, chart of accounts, tax codes,
--              tracking categories, custom groups, sharing, connections, locked periods and audit trail (and
--              then the locked period triggers refuse anything dated in a locked period). @bKeepSettings = 0
--              deletes all of it too, except the tblUsers row, which the caller deletes or keeps.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @Keep TABLE (name SYSNAME COLLATE DATABASE_DEFAULT PRIMARY KEY);
    -- Handled below or by the caller, whatever @bKeepSettings is
    INSERT INTO @Keep (name) VALUES ('tblUsers'), ('tblPortfolioPermissions');
    IF @bKeepSettings = 1
        INSERT INTO @Keep (name) VALUES
            ('tblPortfolioInvites'), ('tblChartOfAccounts'), ('tblTaxCodes'), ('tblTrackingCategories'), ('tblCustomGroups'),
            ('tblLockedPeriod'), ('tblLockedPeriodHistory'), ('tblAuditLog'), ('tblApiAuditLog'), ('tblLog'),
            ('tblApiKeys'), ('tblSecurityEvents'), ('tblTermsAcceptances'), ('tblAlertSettings'), ('tblInvoiceSettings'),
            ('tblBasSettings'), ('tblBasLabels'), ('tblSalesTaxSetup'), ('tblPracticeDetails'), ('tblClientDetails'),
            ('tblClientOfficeholders'), ('tblFamilyGroupMembers'), ('tblNavexaConnection'), ('tblNavexaMapping'),
            ('tblAccountingConnections'), ('tblLedgerExportMap'), ('tblAllocationRule'), ('tblOnboarding');
    ELSE
    BEGIN
        -- Unlock first, or the locked period triggers refuse the deletes below
        DELETE FROM tblLockedPeriod WHERE iUserID = @iPortfolioId;
        DELETE FROM tblLockedPeriodHistory WHERE iUserID = @iPortfolioId;
        DELETE O FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID WHERE C.iUserID = @iPortfolioId;
        UPDATE tblChartOfAccounts SET iParentAccountID = NULL WHERE iUserID = @iPortfolioId AND iParentAccountID IS NOT NULL;
        DELETE FROM tblPortfolioPermissions WHERE iUserIDForPortfolioToAccess = @iPortfolioId;
    END

    -- Rows that point at the portfolio only through a parent row
    DELETE L FROM tblBankReconciliationLine L JOIN tblBankReconciliation R ON R.iReconciliationID = L.iReconciliationID WHERE R.iUserID = @iPortfolioId;
    DELETE P FROM tblBillPayments P JOIN tblBills B ON B.iBillID = P.iBillID WHERE B.iUserID = @iPortfolioId;
    DELETE P FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID WHERE I.iUserID = @iPortfolioId;
    DELETE E FROM tblLoanEntries E JOIN tblLoans L ON L.iLoanID = E.iLoanID WHERE L.iUserID = @iPortfolioId;
    DELETE E FROM tblDepreciationEntries E
    WHERE E.iAssetID IN (SELECT iAssetID FROM tblFixedAssets WHERE iUserID = @iPortfolioId)
       OR E.iRunID IN (SELECT iRunID FROM tblDepreciationRuns WHERE iUserID = @iPortfolioId);
    DELETE R FROM tblRentalRentPayments R JOIN tblRentalTenancies T ON T.iTenancyID = R.iTenancyID
        JOIN tblRentalProperties P ON P.iPropertyID = T.iPropertyID WHERE P.iUserID = @iPortfolioId;
    DECLARE @Properties TABLE (iPropertyID INT PRIMARY KEY);
    INSERT INTO @Properties SELECT iPropertyID FROM tblRentalProperties WHERE iUserID = @iPortfolioId;
    DELETE FROM tblRentalTenantBills WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalInspections WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalTenancies WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalAmounts WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalAssets WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalCompliance WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalJobs WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalLoanLinks WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalOwners WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);
    DELETE FROM tblRentalSales WHERE iPropertyID IN (SELECT iPropertyID FROM @Properties);

    -- Every table with a portfolio column. Tables others point at by foreign key go after the ones pointing
    -- at them; the chart of accounts goes last (journal lines point at it).
    DECLARE @Tables TABLE (iOrder INT IDENTITY(1,1) PRIMARY KEY, strSql NVARCHAR(1000));
    INSERT INTO @Tables (strSql)
    SELECT N'DELETE FROM dbo.' + QUOTENAME(T.name) + N' WHERE '
           + STUFF((SELECT N' OR ' + QUOTENAME(C.name) + N' = @id'
                    FROM sys.columns C
                    WHERE C.object_id = T.object_id AND LOWER(C.name) IN ('iuserid', 'iportfolioid')
                    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 4, N'')
    FROM sys.tables T
    WHERE T.is_ms_shipped = 0
      AND T.name NOT IN (SELECT name FROM @Keep)
      AND EXISTS (SELECT 1 FROM sys.columns C WHERE C.object_id = T.object_id AND LOWER(C.name) IN ('iuserid', 'iportfolioid'))
    ORDER BY CASE WHEN T.name = 'tblChartOfAccounts' THEN 2
                  WHEN EXISTS (SELECT 1 FROM sys.foreign_keys F WHERE F.referenced_object_id = T.object_id AND F.parent_object_id <> T.object_id) THEN 1
                  ELSE 0 END,
             T.name;

    DECLARE @i INT = 1, @n INT = (SELECT COUNT(*) FROM @Tables), @sql NVARCHAR(1000);
    WHILE @i <= @n
    BEGIN
        SELECT @sql = strSql FROM @Tables WHERE iOrder = @i;
        EXEC sp_executesql @sql, N'@id INT', @id = @iPortfolioId;
        SET @i += 1;
    END

    IF @bKeepSettings = 1
        EXEC dbo.uspClearDeletedJournalLinks @iPortfolioId;
    ELSE
        -- The deletes above wrote audit rows of their own
        DELETE FROM tblAuditLog WHERE iUserID = @iPortfolioId;
END
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
            EXEC dbo.uspPurgePortfolio @iPortfolioId, 1;

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

CREATE OR ALTER PROCEDURE [dbo].[uspDeletePortfolio]
(
    @iUserId      INT,
    @iPortfolioId INT,
    @strConfirm   VARCHAR(1010)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Permanently deletes a portfolio and everything in it, for an owner (fnCanDeletePortfolio), when
--              @strConfirm is its name (as the portfolio list shows it). Refused for the portfolio an account signs in with (delete the account
--              instead, or delete everything in it). Everyone it was shared with loses it. Returns one row:
--              Result = 'Portfolio Deleted' or a message starting with 'Error:'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strName VARCHAR(500), @strLongName VARCHAR(1010);
    -- The portfolio list shows "contact - name" to a login that can open every portfolio, so either confirms
    SELECT @strName = strEntityName, @strLongName = strContactName + ' - ' + strEntityName FROM tblUsers WHERE iUserId = @iPortfolioId;

    IF @strName IS NULL OR dbo.fnCanDeletePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: Only an owner of this portfolio can delete it.' AS Result;
        RETURN;
    END
    IF dbo.fnIsLoginRow(@iPortfolioId) = 1
    BEGIN
        SELECT 'Error: This is the portfolio an account signs in with, so it can''t be deleted on its own. Delete everything in it instead, or delete the account.' AS Result;
        RETURN;
    END
    IF LTRIM(RTRIM(ISNULL(@strConfirm, ''))) NOT IN (LTRIM(RTRIM(@strName)), LTRIM(RTRIM(@strLongName)))
    BEGIN
        SELECT 'Error: Type the portfolio''s name exactly to confirm.' AS Result;
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;
        EXEC dbo.uspPurgePortfolio @iPortfolioId, 0;
        DELETE FROM tblUsers WHERE iUserId = @iPortfolioId;
        COMMIT TRANSACTION;
        SELECT 'Portfolio Deleted' AS Result;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        SELECT 'Error: ' + ERROR_MESSAGE() AS Result;
    END CATCH
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteLogin]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        29-Oct-2026
-- Description: Permanently deletes an account (the app checks the password and the typed email first).
--              Its API keys, sign-in history, assistant settings, family groups and access to other
--              people's portfolios go. Of the portfolios it created (fnLoginDeletePlan):
--                * nobody else has access: deleted with everything in them;
--                * someone else has access: kept for them, with this account's email, password, two-factor
--                  settings and contact details removed so nobody can sign in with them. When none of the
--                  others is an owner, those with the highest access left become owners, so someone can
--                  still manage it.
--              Returns one row: Result = 'Account Deleted' (with Deleted and Kept counts) or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF dbo.fnIsLoginRow(@iUserId) = 0
    BEGIN
        SELECT 'Error: That account was not found.' AS Result;
        RETURN;
    END

    DECLARE @Plan TABLE (PortfolioId INT PRIMARY KEY, OthersWithAccess INT, OtherOwners INT);
    INSERT INTO @Plan SELECT PortfolioId, OthersWithAccess, OtherOwners FROM dbo.fnLoginDeletePlan(@iUserId);
    DECLARE @iDeleted INT = (SELECT COUNT(*) FROM @Plan WHERE OthersWithAccess = 0);
    DECLARE @iKept INT = (SELECT COUNT(*) FROM @Plan WHERE OthersWithAccess > 0);

    BEGIN TRY
        BEGIN TRANSACTION;

        -- What belongs to the person rather than a portfolio
        DELETE FROM tblPortfolioPermissions WHERE iUserID IN (SELECT PortfolioId FROM @Plan);
        DELETE FROM tblApiKeys WHERE iUserID = @iUserId;
        DELETE FROM tblSecurityEvents WHERE iUserId = @iUserId;
        DELETE FROM tblTermsAcceptances WHERE iUserId = @iUserId;
        DELETE FROM tblAssistantSettings WHERE iLoginID = @iUserId;
        DELETE FROM tblAssistantChats WHERE iLoginID = @iUserId;
        DELETE M FROM tblFamilyGroupMembers M JOIN tblFamilyGroups G ON G.iFamilyGroupID = M.iFamilyGroupID WHERE G.iLoginID = @iUserId;
        DELETE FROM tblFamilyGroups WHERE iLoginID = @iUserId;

        -- Portfolios nobody else uses
        DECLARE @iPortfolioId INT = (SELECT MIN(PortfolioId) FROM @Plan WHERE OthersWithAccess = 0);
        WHILE @iPortfolioId IS NOT NULL
        BEGIN
            EXEC dbo.uspPurgePortfolio @iPortfolioId, 0;
            DELETE FROM tblUsers WHERE iUserId = @iPortfolioId;
            SET @iPortfolioId = (SELECT MIN(PortfolioId) FROM @Plan WHERE OthersWithAccess = 0 AND PortfolioId > @iPortfolioId);
        END

        -- Portfolios others use: make sure one of them can manage it
        UPDATE P SET strAccessDescription = 'Admin'
        FROM tblPortfolioPermissions P
        JOIN @Plan K ON K.PortfolioId = P.iUserIDForPortfolioToAccess
        WHERE K.OthersWithAccess > 0 AND K.OtherOwners = 0
          AND dbo.fnAccessLevel(P.strAccessDescription) =
              (SELECT MAX(dbo.fnAccessLevel(X.strAccessDescription)) FROM tblPortfolioPermissions X
               WHERE X.iUserIDForPortfolioToAccess = K.PortfolioId);

        -- and nobody can sign in with this account's details any more
        UPDATE U SET strLoginEmail = '', strLoginPassword = '', strTwoFactorSecret = NULL, strTwoFactorRecoveryCodes = NULL,
                     dtmDateAccountConfirmed = NULL, strContactName = '(account deleted)', strContactEmail = '', strContactPhone = ''
        FROM tblUsers U JOIN @Plan K ON K.PortfolioId = U.iUserId
        WHERE K.OthersWithAccess > 0;

        COMMIT TRANSACTION;
        SELECT 'Account Deleted' AS Result, @iDeleted AS Deleted, @iKept AS Kept;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
        SELECT 'Error: ' + ERROR_MESSAGE() AS Result, 0 AS Deleted, 0 AS Kept;
    END CATCH
END
GO
