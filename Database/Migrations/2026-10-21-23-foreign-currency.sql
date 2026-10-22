-- =============================================
-- Migration: foreign currency accounts
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Accounting > Foreign
-- currency. Run it after 2026-10-21-22-tracking-budgets.sql. Safe to run more than once. It only adds things.
--
-- Rates are in the trades' convention: units of the foreign currency per one unit of the portfolio's currency
-- (0.65 USD per AUD), so the portfolio amount is the foreign amount divided by the rate, to the cent.
--
-- What it adds:
--   * System accounts 4950 Foreign Exchange Gains and Losses (realised, when an invoice or bill is paid at a different
--     rate) and 4960 Unrealised Foreign Exchange Gains and Losses (revaluations), both revenue.
--   * tblForeignCurrencyAccounts: a portfolio's own bank account kept in another currency.
--   * tblJournalLines gets dblForeignAmount: on a foreign currency account's lines, the amount in that currency
--     (debit positive), so the account has a balance in both currencies.
--   * tblInvoices gets strCurrency, dblExchangeRate and dblHomeTotal; tblInvoicePayments gets dblExchangeRate,
--     dblHomeAmount (what reached the bank, in the portfolio's currency) and dblReceivableHome (what it cleared from
--     Accounts Receivable). The invoice procs that use them are changed in 2026-10-21-26-invoice-books.sql.
--   * tblFxRevaluations: a revaluation's journal and the next day's reversal.
--   * uspSetAccountCurrency, uspAddForeignBankEntry, uspDeleteForeignBankEntry, uspPostFxRevaluation,
--     uspDeleteFxRevaluation.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblTrackingCategories', 'U') IS NULL OR OBJECT_ID('dbo.tblInvoices', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-18-invoicing.sql and 2026-10-21-22-tracking-budgets.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @iAccountID INT;
EXEC dbo.uspGetLedgerAccount 0, 4950, 'Foreign Exchange Gains and Losses', 'Revenue', @iAccountID OUTPUT;
EXEC dbo.uspGetLedgerAccount 0, 4960, 'Unrealised Foreign Exchange Gains and Losses', 'Revenue', @iAccountID OUTPUT;
GO

IF OBJECT_ID('dbo.tblForeignCurrencyAccounts', 'U') IS NULL
    CREATE TABLE dbo.tblForeignCurrencyAccounts (
        iAccountID   INT NOT NULL CONSTRAINT PK_tblForeignCurrencyAccounts PRIMARY KEY,
        iUserID      INT NOT NULL,
        strCurrency  CHAR(3) NOT NULL
    );
GO

IF COL_LENGTH('dbo.tblJournalLines', 'dblForeignAmount') IS NULL
    ALTER TABLE dbo.tblJournalLines ADD dblForeignAmount DECIMAL(19,4) NULL;
GO

IF COL_LENGTH('dbo.tblInvoices', 'strCurrency') IS NULL
    ALTER TABLE dbo.tblInvoices ADD strCurrency CHAR(3) NULL, dblExchangeRate DECIMAL(19,8) NULL, dblHomeTotal DECIMAL(19,4) NULL;
GO
IF COL_LENGTH('dbo.tblInvoicePayments', 'dblHomeAmount') IS NULL
    ALTER TABLE dbo.tblInvoicePayments ADD dblExchangeRate DECIMAL(19,8) NULL, dblHomeAmount DECIMAL(19,4) NULL, dblReceivableHome DECIMAL(19,4) NULL;
GO

IF OBJECT_ID('dbo.tblFxRevaluations', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFxRevaluations (
        iRevaluationID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFxRevaluations PRIMARY KEY,
        iUserID            INT NOT NULL,
        dtmDate            DATE NOT NULL,
        strRates           NVARCHAR(1000) NULL,         -- e.g. USD 0.6512, EUR 0.6021
        iJournalID         INT NULL,
        iReversalJournalID INT NULL,
        dtmCreatedDate     DATETIME NOT NULL CONSTRAINT DF_tblFxRevaluations_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblFxRevaluations_User ON dbo.tblFxRevaluations (iUserID, dtmDate);
END
GO

-- Marks one of the portfolio's own asset or liability accounts as kept in another currency (@strCurrency NULL puts it
-- back). Only while nothing has been posted to it, so its two balances always agree. Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSetAccountCurrency
    @iUserId     INT,
    @iAccountID  INT,
    @strCurrency CHAR(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strCurrency = NULLIF(UPPER(LTRIM(RTRIM(@strCurrency))), '');
    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId
                           AND strAccountType IN ('Asset', 'Liability'))
            THEN 'Choose one of this portfolio''s own bank, asset or liability accounts (add one first if need be).'
        WHEN @strCurrency IS NOT NULL AND (LEN(@strCurrency) <> 3 OR @strCurrency LIKE '%[^A-Z]%')
            THEN 'Enter a three-letter currency code, such as USD.'
        WHEN EXISTS (SELECT 1 FROM tblJournalLines WHERE iAccountID = @iAccountID)
            THEN 'Something has already been posted to that account. Add a new account for the foreign currency instead.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    DELETE FROM tblForeignCurrencyAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId;
    IF @strCurrency IS NOT NULL
        INSERT tblForeignCurrencyAccounts (iAccountID, iUserID, strCurrency) VALUES (@iAccountID, @iUserId, @strCurrency);
    SELECT 'Success' AS Result;
END
GO

-- Money in (positive) or out (negative) of a foreign currency account, against another account: a deposit, a fee, a
-- transfer from the home bank account. Posts a journal (source 'ForeignBank'): the foreign account gets the amount
-- divided by the rate and keeps the foreign amount. Returns Result and iJournalID.
CREATE OR ALTER PROCEDURE dbo.uspAddForeignBankEntry
    @iUserId          INT,
    @iAccountID       INT,
    @dtDate           DATE,
    @dblForeignAmount DECIMAL(19,4),
    @dblExchangeRate  DECIMAL(19,8),
    @iOtherAccountID  INT,
    @strDescription   VARCHAR(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @strDescription = NULLIF(LTRIM(RTRIM(@strDescription)), '');
    DECLARE @Error VARCHAR(300) = CASE
        WHEN NOT EXISTS (SELECT 1 FROM tblForeignCurrencyAccounts WHERE iAccountID = @iAccountID AND iUserID = @iUserId)
            THEN 'Choose one of the foreign currency accounts.'
        WHEN @dtDate IS NULL THEN 'Enter the date.'
        WHEN ISNULL(@dblForeignAmount, 0) = 0 THEN 'Enter the amount.'
        WHEN ISNULL(@dblExchangeRate, 0) <= 0 THEN 'Enter the exchange rate.'
        WHEN @iOtherAccountID = @iAccountID OR NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iOtherAccountID AND (iUserID = @iUserId OR iUserID = 0)
                  AND ISNULL(bIsPostable, 1) = 1)
            THEN 'Choose the other account.'
        WHEN EXISTS (SELECT 1 FROM tblForeignCurrencyAccounts WHERE iAccountID = @iOtherAccountID AND iUserID = @iUserId)
            THEN 'Moving money between two foreign currency accounts isn''t supported yet. Go through the home bank account.'
        WHEN @strDescription IS NULL THEN 'Enter a description.'
        END;
    DECLARE @dblHome DECIMAL(19,4) = ROUND(ABS(@dblForeignAmount) / NULLIF(@dblExchangeRate, 0), 2);
    IF @Error IS NULL AND @dblHome = 0
        SET @Error = 'That amount is less than a cent in the portfolio''s currency.';
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS iJournalID;
        RETURN;
    END

    BEGIN TRANSACTION;
    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'ForeignBank', NULL, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, dblForeignAmount)
    VALUES (@iJournalID, @iAccountID, CASE WHEN @dblForeignAmount > 0 THEN @dblHome ELSE 0 END,
            CASE WHEN @dblForeignAmount < 0 THEN @dblHome ELSE 0 END, @strDescription, @dblForeignAmount),
           (@iJournalID, @iOtherAccountID, CASE WHEN @dblForeignAmount < 0 THEN @dblHome ELSE 0 END,
            CASE WHEN @dblForeignAmount > 0 THEN @dblHome ELSE 0 END, @strDescription, NULL);
    UPDATE tblJournal SET iSourceID = @iJournalID WHERE iJournalID = @iJournalID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iJournalID AS iJournalID;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteForeignBankEntry
    @iUserId    INT,
    @iJournalID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId AND strSourceModule = 'ForeignBank';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That entry wasn''t found.' END AS Result;
END
GO

-- Posts a revaluation worked out by the site: <lines><l a="account" d="debit" c="credit" r="1 when it reverses"/></lines>.
-- Every line goes in a journal on @dtDate (source 'FxRevaluation'); the r="1" lines (open invoices and bills) are
-- reversed the next day (source 'FxRevaluationReversal'), so payments still clear them at their own rate. Foreign
-- currency accounts are not reversed: their portfolio balance stays at the closing rate. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspPostFxRevaluation
    @iUserId  INT,
    @dtDate   DATE,
    @strRates NVARCHAR(1000) = NULL,
    @xmlLines XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Lines TABLE (iAccountID INT, dblDebit DECIMAL(19,4), dblCredit DECIMAL(19,4), bReverse BIT, strDescription VARCHAR(500));
    INSERT @Lines (iAccountID, dblDebit, dblCredit, bReverse, strDescription)
    SELECT X.l.value('@a', 'INT'), ISNULL(X.l.value('@d', 'DECIMAL(19,4)'), 0), ISNULL(X.l.value('@c', 'DECIMAL(19,4)'), 0),
           ISNULL(X.l.value('@r', 'BIT'), 0), LEFT(X.l.value('@f', 'VARCHAR(500)'), 500)
    FROM @xmlLines.nodes('/lines/l') AS X(l);
    DELETE FROM @Lines WHERE dblDebit = 0 AND dblCredit = 0;

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @dtDate IS NULL THEN 'Enter the date.'
        WHEN NOT EXISTS (SELECT 1 FROM @Lines) THEN 'Nothing needs revaluing at those rates.'
        WHEN EXISTS (SELECT 1 FROM @Lines L WHERE NOT EXISTS (
                SELECT 1 FROM tblChartOfAccounts A WHERE A.iAccountID = L.iAccountID AND (A.iUserID = @iUserId OR A.iUserID = 0)))
            THEN 'An account wasn''t found in this portfolio.'
        WHEN (SELECT SUM(dblDebit - dblCredit) FROM @Lines) <> 0
          OR ISNULL((SELECT SUM(dblDebit - dblCredit) FROM @Lines WHERE bReverse = 1), 0) <> 0
            THEN 'The revaluation didn''t balance.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, CAST(NULL AS INT) AS Id;
        RETURN;
    END

    DECLARE @strDescription VARCHAR(500) = LEFT('Foreign currency revaluation' + ISNULL(' at ' + CAST(@strRates AS VARCHAR(400)), ''), 500);
    BEGIN TRANSACTION;
    INSERT tblFxRevaluations (iUserID, dtmDate, strRates) VALUES (@iUserId, @dtDate, @strRates);
    DECLARE @iRevaluationID INT = SCOPE_IDENTITY();

    INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
    VALUES (@iUserId, @dtDate, @strDescription, 'FxRevaluation', @iRevaluationID, GETDATE());
    DECLARE @iJournalID INT = SCOPE_IDENTITY();
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
    SELECT @iJournalID, iAccountID, dblDebit, dblCredit, ISNULL(strDescription, @strDescription) FROM @Lines;

    DECLARE @iReversalID INT = NULL;
    IF EXISTS (SELECT 1 FROM @Lines WHERE bReverse = 1)
    BEGIN
        INSERT INTO tblJournal (iUserID, dtmTransactionDate, strDescription, strSourceModule, iSourceID, dtmCreatedDate)
        VALUES (@iUserId, DATEADD(DAY, 1, @dtDate), LEFT('Reversal: ' + @strDescription, 500), 'FxRevaluationReversal', @iRevaluationID, GETDATE());
        SET @iReversalID = SCOPE_IDENTITY();
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription)
        SELECT @iReversalID, iAccountID, dblCredit, dblDebit, LEFT('Reversal: ' + ISNULL(strDescription, @strDescription), 500)
        FROM @Lines WHERE bReverse = 1;
    END

    UPDATE tblFxRevaluations SET iJournalID = @iJournalID, iReversalJournalID = @iReversalID WHERE iRevaluationID = @iRevaluationID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result, @iRevaluationID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteFxRevaluation
    @iUserId        INT,
    @iRevaluationID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @iJournalID INT, @iReversalID INT, @bFound BIT = 0;
    SELECT @iJournalID = iJournalID, @iReversalID = iReversalJournalID, @bFound = 1
    FROM tblFxRevaluations WHERE iRevaluationID = @iRevaluationID AND iUserID = @iUserId;
    IF @bFound = 0
    BEGIN
        SELECT 'Error: That revaluation wasn''t found.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    DELETE FROM tblJournal WHERE iUserID = @iUserId AND iJournalID IN (@iJournalID, @iReversalID);
    DELETE FROM tblFxRevaluations WHERE iRevaluationID = @iRevaluationID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Foreign currency migration done.';
GO
