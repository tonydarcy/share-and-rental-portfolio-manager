-- =============================================
-- Migration: every portfolio table carries its portfolio (11-Nov-2026). Safe to run more than once.
-- Groundwork for row-level security: 24 "child" tables (journal lines, invoice and bill lines and payments, the
-- rental tables and so on) only knew their portfolio through a parent row. Each now gets its own iUserID column:
--   * existing rows are filled in from the parent row;
--   * a trigger (trg<table>Portfolio) copies the parent's portfolio onto every row added or moved to another parent,
--     and corrects a wrong value, so no stored procedure or page had to change (and backups restored from older
--     copies of the site fill it in too);
--   * an index on the new column.
-- The column stays NULL-able because the trigger fills it straight after the insert. A row whose parent is gone
-- (none expected; the tables have foreign keys) keeps NULL; the last step lists any.
-- Tables that already had a portfolio column under another name (iPortfolioID), login-level tables and shared
-- reference tables (prices, announced dividends, share details) are left as they are.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

DECLARE @Map TABLE (iOrder INT IDENTITY, strChild SYSNAME, strParentColumn SYSNAME, strParent SYSNAME, strParentKey SYSNAME);
INSERT INTO @Map (strChild, strParentColumn, strParent, strParentKey) VALUES
    ('tblJournalLines',           'iJournalID',        'tblJournal',             'iJournalID'),
    ('tblBankReconciliationLine', 'iReconciliationID', 'tblBankReconciliation',  'iReconciliationID'),
    ('tblInvoiceLines',           'iInvoiceID',        'tblInvoices',            'iInvoiceID'),
    ('tblInvoicePayments',        'iInvoiceID',        'tblInvoices',            'iInvoiceID'),
    ('tblBillLines',              'iBillID',           'tblBills',               'iBillID'),
    ('tblBillPayments',           'iBillID',           'tblBills',               'iBillID'),
    ('tblQuoteOrderLines',        'iDocumentID',       'tblQuotesOrders',        'iDocumentID'),
    ('tblBudgetLines',            'iBudgetID',         'tblBudgets',             'iBudgetID'),
    ('tblTrackingOptions',        'iCategoryID',       'tblTrackingCategories',  'iCategoryID'),
    ('tblLoanEntries',            'iLoanID',           'tblLoans',               'iLoanID'),
    ('tblDepreciationEntries',    'iRunID',            'tblDepreciationRuns',    'iRunID'),
    ('tblShareTradeGroups',       'iShareTradeID',     'tblShareTrades',         'iShareTradeID'),
    ('tblCorporateActionTrades',  'iActionID',         'tblCorporateActions',    'iActionID'),
    ('tblRentalTenancies',        'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalRentPayments',     'iTenancyID',        'tblRentalTenancies',     'iTenancyID'),  -- after tenancies
    ('tblRentalAmounts',          'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalAssets',           'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalCompliance',       'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalInspections',      'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalJobs',             'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalLoanLinks',        'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalOwners',           'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalSales',            'iPropertyID',       'tblRentalProperties',    'iPropertyID'),
    ('tblRentalTenantBills',      'iPropertyID',       'tblRentalProperties',    'iPropertyID');

DECLARE @i INT = 1, @n INT = (SELECT COUNT(*) FROM @Map), @child SYSNAME, @col SYSNAME, @parent SYSNAME, @key SYSNAME,
        @sql NVARCHAR(MAX), @missing NVARCHAR(MAX) = N'';

WHILE @i <= @n
BEGIN
    SELECT @child = strChild, @col = strParentColumn, @parent = strParent, @key = strParentKey FROM @Map WHERE iOrder = @i;
    SET @i += 1;

    -- A table this database doesn't have yet (an older optional migration not run): skip it
    IF OBJECT_ID(N'dbo.' + @child, N'U') IS NULL OR OBJECT_ID(N'dbo.' + @parent, N'U') IS NULL
    BEGIN
        SET @missing += N' ' + @child;
        CONTINUE;
    END

    BEGIN TRANSACTION;

    -- 1. The column
    IF COL_LENGTH(N'dbo.' + @child, N'iUserID') IS NULL
        EXEC (N'ALTER TABLE dbo.' + @child + N' ADD iUserID INT NULL;');

    -- 2. Fill existing rows. The locked-period trigger on journal lines would refuse an update to a locked year,
    --    so it is switched off for this one statement (only the new column changes).
    IF @child = 'tblJournalLines' AND OBJECT_ID(N'dbo.trgJournalLinesLock', N'TR') IS NOT NULL
        DISABLE TRIGGER dbo.trgJournalLinesLock ON dbo.tblJournalLines;
    SET @sql = N'UPDATE C SET iUserID = P.iUserID FROM dbo.' + @child + N' C JOIN dbo.' + @parent + N' P ON P.' + @key
             + N' = C.' + @col + N' WHERE C.iUserID IS NULL OR C.iUserID <> P.iUserID;';
    EXEC (@sql);
    IF @child = 'tblJournalLines' AND OBJECT_ID(N'dbo.trgJournalLinesLock', N'TR') IS NOT NULL
        ENABLE TRIGGER dbo.trgJournalLinesLock ON dbo.tblJournalLines;

    -- 3. Keep it right from now on
    SET @sql = N'CREATE OR ALTER TRIGGER dbo.trg' + SUBSTRING(@child, 4, 200) + N'Portfolio ON dbo.' + @child + N'
AFTER INSERT, UPDATE
AS
-- Copies the portfolio (iUserID) of the parent row in ' + @parent + N' onto rows added or moved, so every
-- portfolio table carries its own portfolio. Added by 2026-11-11-portfolio-id-on-child-tables.sql.
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(' + @col + N') OR UPDATE(iUserID)) RETURN;
    UPDATE C SET iUserID = P.iUserID
    FROM dbo.' + @child + N' C
    JOIN dbo.' + @parent + N' P ON P.' + @key + N' = C.' + @col + N'
    WHERE C.' + @col + N' IN (SELECT ' + @col + N' FROM inserted)
      AND (C.iUserID IS NULL OR C.iUserID <> P.iUserID);
END';
    EXEC (@sql);

    -- 4. The index (journal lines: with the account and amounts, for per-portfolio balances)
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @child) AND name = N'IX_' + @child + N'_User')
    BEGIN
        SET @sql = N'CREATE NONCLUSTERED INDEX IX_' + @child + N'_User ON dbo.' + @child
                 + CASE WHEN @child = 'tblJournalLines' THEN N' (iUserID, iAccountID) INCLUDE (iJournalID, dblDebit, dblCredit);'
                        ELSE N' (iUserID);' END;
        EXEC (@sql);
    END

    COMMIT TRANSACTION;
END

IF @missing <> N''
    PRINT N'Skipped (table not in this database yet; run this script again after adding it):' + @missing;
GO

-- Any row left without a portfolio (its parent row is gone). Expect none.
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql += CASE WHEN @sql = N'' THEN N'' ELSE N' UNION ALL ' END
             + N'SELECT ''' + t.name + N''' AS strTable, COUNT(*) AS iRows FROM dbo.' + t.name + N' WHERE iUserID IS NULL'
FROM sys.triggers tr JOIN sys.tables t ON t.object_id = tr.parent_id
WHERE tr.name = N'trg' + SUBSTRING(t.name, 4, 200) + N'Portfolio';
IF @sql <> N''
BEGIN
    SET @sql = N'SELECT strTable, iRows FROM (' + @sql + N') X WHERE iRows > 0;';
    EXEC (@sql);
END
GO
