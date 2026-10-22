-- =============================================
-- Migration: GST / VAT / sales tax codes and the sales tax report
-- Date:      17-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Tax > Sales tax. Run it AFTER 2026-10-15-accounting.sql and 2026-10-15-allocation-rules.sql
-- (it changes uspAllocateSuspenseTransaction and the allocation rule procs). Safe to run more
-- than once. Nothing changes for a portfolio until it sets up its tax codes.
--
-- What it adds:
--   * tblSalesTaxSetup (new): per portfolio, what the tax is called (GST, VAT, sales tax),
--     how often it is reported, and the ledger account the tax goes to.
--   * tblTaxCodes (new): per portfolio, each tax code: sales or purchases, its rate, and the
--     boxes of the tax return (BAS, VAT return, ...) its amounts and tax are reported in.
--   * tblJournalLines.iTaxCodeID, dblTaxAmount, bIsTaxLine: a line with a tax code carries
--     the amount before tax, and the tax goes on a second line (bIsTaxLine = 1) to the tax
--     control account. dblTaxAmount is the tax, on the same side as the line.
--   * tblAllocationRule.iTaxCodeID: a rule can also set the tax code.
--   * uspGetSalesTaxSetup, uspSaveSalesTaxSetup, uspGetTaxCodes, uspSaveTaxCode,
--     uspDeleteTaxCode, uspAddJournalLineWithTax, uspGetSalesTaxLines (new).
--     uspGetJournalDetail returns the new columns already (it selects JL.*).
--   * uspAllocateSuspenseTransaction, uspGetAllocationRules, uspSaveAllocationRule:
--     take an optional tax code (callers that don't pass one work as before).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblSalesTaxSetup', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSalesTaxSetup (
        iUserID            INT NOT NULL CONSTRAINT PK_tblSalesTaxSetup PRIMARY KEY,
        strTaxName         VARCHAR(30) NOT NULL,          -- GST, VAT, Sales tax
        iReportingMonths   INT NOT NULL CONSTRAINT DF_tblSalesTaxSetup_Months DEFAULT (3),  -- 1, 2, 3, 6 or 12
        iControlAccountID  INT NOT NULL,
        dtmUpdatedDate     DATETIME NOT NULL CONSTRAINT DF_tblSalesTaxSetup_Updated DEFAULT (GETDATE())
    );
END
GO

IF OBJECT_ID('dbo.tblTaxCodes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTaxCodes (
        iTaxCodeID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTaxCodes PRIMARY KEY,
        iUserID             INT NOT NULL,
        strCode             VARCHAR(10) NOT NULL,
        strName             VARCHAR(100) NOT NULL,
        strKind             VARCHAR(10) NOT NULL,          -- Sales or Purchases
        dblRate             DECIMAL(9,4) NOT NULL,         -- percent, e.g. 10.0000
        strAmountBoxes      VARCHAR(100) NULL,             -- e.g. 'G1,G2'
        strTaxBox           VARCHAR(20) NULL,              -- e.g. '1A'
        bAmountIncludesTax  BIT NOT NULL CONSTRAINT DF_tblTaxCodes_Incl DEFAULT (1),
        bIsActive           BIT NOT NULL CONSTRAINT DF_tblTaxCodes_Active DEFAULT (1),
        iSortOrder          INT NOT NULL CONSTRAINT DF_tblTaxCodes_Sort DEFAULT (0),
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblTaxCodes_Created DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblTaxCodes_UserCode ON dbo.tblTaxCodes (iUserID, strCode);
END
GO

IF COL_LENGTH('dbo.tblJournalLines', 'iTaxCodeID') IS NULL
    ALTER TABLE dbo.tblJournalLines ADD iTaxCodeID INT NULL;
GO
IF COL_LENGTH('dbo.tblJournalLines', 'dblTaxAmount') IS NULL
    ALTER TABLE dbo.tblJournalLines ADD dblTaxAmount DECIMAL(19,4) NULL;
GO
IF COL_LENGTH('dbo.tblJournalLines', 'bIsTaxLine') IS NULL
    ALTER TABLE dbo.tblJournalLines ADD bIsTaxLine BIT NOT NULL CONSTRAINT DF_tblJournalLines_IsTaxLine DEFAULT (0);
GO
-- A plain index, not a filtered one: a filtered index makes every older proc that writes
-- journal lines fail if it was created with QUOTED_IDENTIFIER OFF
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblJournalLines_TaxCode' AND object_id = OBJECT_ID('dbo.tblJournalLines') AND has_filter = 1)
    DROP INDEX IX_tblJournalLines_TaxCode ON dbo.tblJournalLines;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblJournalLines_TaxCode' AND object_id = OBJECT_ID('dbo.tblJournalLines'))
    CREATE INDEX IX_tblJournalLines_TaxCode ON dbo.tblJournalLines (iTaxCodeID);
GO
IF COL_LENGTH('dbo.tblAllocationRule', 'iTaxCodeID') IS NULL
    ALTER TABLE dbo.tblAllocationRule ADD iTaxCodeID INT NULL;
GO

-- =============================================
-- Setup
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetSalesTaxSetup]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: The portfolio's sales tax setup, if it has one, with the
--              control account's number and name. No row: not set up.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT S.strTaxName, S.iReportingMonths, S.iControlAccountID, A.iAccountNumber, A.strAccountName
    FROM tblSalesTaxSetup S
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = S.iControlAccountID
    WHERE S.iUserID = @iUserId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveSalesTaxSetup]
(
    @iUserId           INT,
    @strTaxName        VARCHAR(30),
    @iReportingMonths  INT,
    @iControlAccountID INT = NULL    -- NULL: use (or add) a 'GST / Sales Tax Control' account
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Saves what the tax is called, how often it is reported and the
--              ledger account tax goes to. Without an account, adds a
--              liability account named after the tax ('GST Control' etc.)
--              numbered from 2300, unless the portfolio already has one.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @strTaxName = LTRIM(RTRIM(ISNULL(@strTaxName, '')));
    IF @strTaxName = ''
    BEGIN
        SELECT 'Error: Enter what the tax is called, e.g. GST or VAT.' AS Result;
        RETURN;
    END

    IF ISNULL(@iReportingMonths, 0) NOT IN (1, 2, 3, 6, 12)
    BEGIN
        SELECT 'Error: Choose how often the tax is reported.' AS Result;
        RETURN;
    END

    IF @iControlAccountID IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM tblChartOfAccounts
        WHERE iAccountID = @iControlAccountID AND (iUserID = @iUserId OR iUserID = 0)
          AND strAccountType IN ('Asset', 'Liability') AND ISNULL(bIsPostable, 1) = 1
          AND strAccountName NOT IN ('Bank Import Suspense', 'Trade Clearing / Suspense'))
    BEGIN
        SELECT 'Error: Choose an asset or liability account for the tax.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    IF @iControlAccountID IS NULL
    BEGIN
        DECLARE @strAccountName VARCHAR(255) = LEFT(@strTaxName + ' Control', 255);
        SELECT TOP 1 @iControlAccountID = iAccountID
        FROM tblChartOfAccounts WITH (UPDLOCK, HOLDLOCK)
        WHERE iUserID = @iUserId AND strAccountName = @strAccountName;

        IF @iControlAccountID IS NULL
        BEGIN
            DECLARE @iNumber INT = 2300;
            WHILE EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountNumber = @iNumber AND (iUserID = @iUserId OR iUserID = 0))
                SET @iNumber += 1;
            INSERT INTO tblChartOfAccounts (iUserID, iAccountNumber, strAccountName, strAccountType, bIsPostable)
            VALUES (@iUserId, @iNumber, @strAccountName, 'Liability', 1);
            SET @iControlAccountID = SCOPE_IDENTITY();
        END
    END

    IF EXISTS (SELECT 1 FROM tblSalesTaxSetup WITH (UPDLOCK, HOLDLOCK) WHERE iUserID = @iUserId)
        UPDATE tblSalesTaxSetup
        SET strTaxName = @strTaxName, iReportingMonths = @iReportingMonths,
            iControlAccountID = @iControlAccountID, dtmUpdatedDate = GETDATE()
        WHERE iUserID = @iUserId;
    ELSE
        INSERT INTO tblSalesTaxSetup (iUserID, strTaxName, iReportingMonths, iControlAccountID)
        VALUES (@iUserId, @strTaxName, @iReportingMonths, @iControlAccountID);

    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Tax codes
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetTaxCodes]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: The portfolio's tax codes (active and inactive) in order, and
--              whether any journal line uses each one.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT T.iTaxCodeID, T.strCode, T.strName, T.strKind, T.dblRate, T.strAmountBoxes, T.strTaxBox,
           T.bAmountIncludesTax, T.bIsActive, T.iSortOrder,
           CAST(CASE WHEN EXISTS (SELECT 1 FROM tblJournalLines JL WHERE JL.iTaxCodeID = T.iTaxCodeID)
                     THEN 1 ELSE 0 END AS BIT) AS bInUse
    FROM tblTaxCodes T
    WHERE T.iUserID = @iUserId
    ORDER BY T.iSortOrder, T.strCode;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveTaxCode]
(
    @iUserId            INT,
    @iTaxCodeID         INT = NULL,     -- NULL adds a code
    @strCode            VARCHAR(10),
    @strName            VARCHAR(100),
    @strKind            VARCHAR(10),
    @dblRate            DECIMAL(9,4),
    @strAmountBoxes     VARCHAR(100) = NULL,
    @strTaxBox          VARCHAR(20) = NULL,
    @bAmountIncludesTax BIT = 1,
    @bIsActive          BIT = 1,
    @iSortOrder         INT = NULL      -- NULL: last (new) or unchanged (existing)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or changes one of the portfolio's tax codes. A new rate
--              only applies to entries made from now on; tax already
--              recorded keeps its amount.
--              Returns one row: Result = 'Success', TaxCodeId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SET @strCode = UPPER(LTRIM(RTRIM(ISNULL(@strCode, ''))));
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    SET @strAmountBoxes = NULLIF(LTRIM(RTRIM(ISNULL(@strAmountBoxes, ''))), '');
    SET @strTaxBox = NULLIF(LTRIM(RTRIM(ISNULL(@strTaxBox, ''))), '');

    IF @strCode = '' OR @strName = ''
    BEGIN
        SELECT 'Error: Enter the code and its name.' AS Result, NULL AS TaxCodeId;
        RETURN;
    END

    IF ISNULL(@strKind, '') NOT IN ('Sales', 'Purchases')
    BEGIN
        SELECT 'Error: Choose sales or purchases.' AS Result, NULL AS TaxCodeId;
        RETURN;
    END

    IF @dblRate IS NULL OR @dblRate < 0 OR @dblRate > 100
    BEGIN
        SELECT 'Error: The rate is a percentage from 0 to 100.' AS Result, NULL AS TaxCodeId;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblTaxCodes WHERE iUserID = @iUserId AND strCode = @strCode
               AND (@iTaxCodeID IS NULL OR iTaxCodeID <> @iTaxCodeID))
    BEGIN
        SELECT 'Error: There is already a tax code ' + @strCode + '.' AS Result, NULL AS TaxCodeId;
        RETURN;
    END

    IF @iTaxCodeID IS NULL
    BEGIN
        INSERT INTO tblTaxCodes (iUserID, strCode, strName, strKind, dblRate, strAmountBoxes, strTaxBox,
                                 bAmountIncludesTax, bIsActive, iSortOrder)
        VALUES (@iUserId, @strCode, @strName, @strKind, @dblRate, @strAmountBoxes, @strTaxBox,
                ISNULL(@bAmountIncludesTax, 1), ISNULL(@bIsActive, 1),
                ISNULL(@iSortOrder, (SELECT ISNULL(MAX(iSortOrder), 0) + 10 FROM tblTaxCodes WHERE iUserID = @iUserId)));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS TaxCodeId;
        RETURN;
    END

    -- A code in use can't swap between sales and purchases: its past lines would change sign on the report
    IF EXISTS (SELECT 1 FROM tblTaxCodes T WHERE T.iTaxCodeID = @iTaxCodeID AND T.iUserID = @iUserId AND T.strKind <> @strKind
               AND EXISTS (SELECT 1 FROM tblJournalLines JL WHERE JL.iTaxCodeID = T.iTaxCodeID))
    BEGIN
        SELECT 'Error: This code is already used, so it can''t change between sales and purchases. Add a new code instead.' AS Result, NULL AS TaxCodeId;
        RETURN;
    END

    UPDATE tblTaxCodes
    SET strCode = @strCode, strName = @strName, strKind = @strKind, dblRate = @dblRate,
        strAmountBoxes = @strAmountBoxes, strTaxBox = @strTaxBox,
        bAmountIncludesTax = ISNULL(@bAmountIncludesTax, 1), bIsActive = ISNULL(@bIsActive, 1),
        iSortOrder = ISNULL(@iSortOrder, iSortOrder)
    WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That tax code was not found in this portfolio.' AS Result, NULL AS TaxCodeId;
    ELSE
        SELECT 'Success' AS Result, @iTaxCodeID AS TaxCodeId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteTaxCode]
(
    @iUserId    INT,
    @iTaxCodeID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Deletes a tax code no journal line uses (a used one can be made
--              inactive instead). Rules using it lose their tax code.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That tax code was not found in this portfolio.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblJournalLines WHERE iTaxCodeID = @iTaxCodeID)
    BEGIN
        SELECT 'Error: This code is used by journals, so it can''t be deleted. Make it inactive instead.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    UPDATE tblAllocationRule SET iTaxCodeID = NULL WHERE iUserID = @iUserId AND iTaxCodeID = @iTaxCodeID;
    DELETE FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Journal lines with tax
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAddJournalLineWithTax]
(
    @iUserId        INT,
    @iJournalID     INT,
    @iAccountID     INT,
    @dblDebit       DECIMAL(19,4),   -- the amount including tax
    @dblCredit      DECIMAL(19,4),   -- the amount including tax
    @strDescription VARCHAR(500) = NULL,
    @iTaxCodeID     INT,
    @dblTaxAmount   DECIMAL(19,4)    -- the tax within that amount, 0 or more
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds a journal line with a tax code, split in two: the amount
--              less tax to @iAccountID (carrying the code and the tax), and
--              the tax to the portfolio's tax control account on the same
--              side (bIsTaxLine = 1). With no tax (a zero-rated or exempt
--              code) only the first line is added. Errors are thrown, so the
--              caller's transaction rolls the whole journal back.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @dblDebit = ISNULL(@dblDebit, 0);
    SET @dblCredit = ISNULL(@dblCredit, 0);
    SET @dblTaxAmount = ISNULL(@dblTaxAmount, 0);

    IF NOT EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId)
        THROW 50401, 'That journal was not found in this portfolio.', 1;

    IF NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId)
        THROW 50402, 'That tax code was not found in this portfolio.', 1;

    DECLARE @iControlAccountID INT = (SELECT iControlAccountID FROM tblSalesTaxSetup WHERE iUserID = @iUserId);
    IF @iControlAccountID IS NULL
        THROW 50403, 'Set up sales tax for this portfolio before using tax codes.', 1;

    IF (@dblDebit <> 0 AND @dblCredit <> 0) OR (@dblDebit = 0 AND @dblCredit = 0) OR @dblDebit < 0 OR @dblCredit < 0
        THROW 50404, 'A journal line is either a debit or a credit.', 1;

    DECLARE @dblGross DECIMAL(19,4) = @dblDebit + @dblCredit;
    IF @dblTaxAmount < 0 OR @dblTaxAmount > @dblGross
        THROW 50405, 'The tax can''t be more than the amount, or negative.', 1;

    IF @dblTaxAmount <> 0 AND @iAccountID = @iControlAccountID
        THROW 50406, 'A line with tax can''t post to the tax control account itself.', 1;

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, iTaxCodeID, dblTaxAmount, bIsTaxLine)
    VALUES (@iJournalID, @iAccountID,
            CASE WHEN @dblDebit > 0 THEN @dblDebit - @dblTaxAmount ELSE 0 END,
            CASE WHEN @dblCredit > 0 THEN @dblCredit - @dblTaxAmount ELSE 0 END,
            @strDescription, @iTaxCodeID, @dblTaxAmount, 0);

    IF @dblTaxAmount <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, strDescription, iTaxCodeID, dblTaxAmount, bIsTaxLine)
        VALUES (@iJournalID, @iControlAccountID,
                CASE WHEN @dblDebit > 0 THEN @dblTaxAmount ELSE 0 END,
                CASE WHEN @dblCredit > 0 THEN @dblTaxAmount ELSE 0 END,
                @strDescription, @iTaxCodeID, NULL, 1);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetSalesTaxLines]
(
    @iUserId INT,
    @dtFrom  DATE,
    @dtTo    DATE
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Every journal line with a tax code dated in the period (not the
--              tax lines themselves), for the sales tax report. Net and Tax
--              are debit-positive: a sale (credit) comes back negative.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT J.iJournalID, J.dtmTransactionDate, J.strDescription AS strJournalDescription, J.strSourceModule,
           JL.iJournalLineID, JL.strDescription, JL.iTaxCodeID,
           A.iAccountNumber, A.strAccountName,
           ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0) AS Net,
           CASE WHEN ISNULL(JL.dblCredit, 0) > 0 THEN -ISNULL(JL.dblTaxAmount, 0) ELSE ISNULL(JL.dblTaxAmount, 0) END AS Tax
    FROM tblJournalLines JL
    INNER JOIN tblJournal J ON J.iJournalID = JL.iJournalID
    INNER JOIN tblTaxCodes T ON T.iTaxCodeID = JL.iTaxCodeID AND T.iUserID = J.iUserID
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = JL.iAccountID
    WHERE J.iUserID = @iUserId
      AND J.dtmTransactionDate >= @dtFrom AND J.dtmTransactionDate <= @dtTo
      AND JL.bIsTaxLine = 0
    ORDER BY J.dtmTransactionDate, J.iJournalID, JL.iJournalLineID;
END
GO

-- =============================================
-- Bank allocation with a tax code
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspAllocateSuspenseTransaction]
(
    @iUserId INT,
    @iSuspenseLineID INT,
    @iTargetAccountID INT,
    @strDescription VARCHAR(500),
    @iTaxCodeID INT = NULL,
    @dblTaxAmount DECIMAL(19,4) = NULL
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
-- Author:		Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Version:     1.003
-- Description:	Optional tax code: the target account gets the amount less
--              @dblTaxAmount (carrying the code), and the tax goes to the
--              portfolio's tax control account on the same side
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @SuspenseAmt DECIMAL(19,4);
    DECLARE @SuspenseAccID INT;
    DECLARE @dtDate DATE;
    DECLARE @iMatchedJournalID INT;
    DECLARE @iControlAccountID INT;

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

    SET @dblTaxAmount = CASE WHEN @iTaxCodeID IS NULL THEN NULL ELSE ISNULL(@dblTaxAmount, 0) END;
    IF @iTaxCodeID IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId)
        BEGIN
            SELECT 'Error: That tax code was not found in this portfolio.' AS Result;
            RETURN;
        END
        SELECT @iControlAccountID = iControlAccountID FROM tblSalesTaxSetup WHERE iUserID = @iUserId;
        IF @iControlAccountID IS NULL
        BEGIN
            SELECT 'Error: Set up sales tax for this portfolio before using tax codes.' AS Result;
            RETURN;
        END
        IF @dblTaxAmount < 0 OR @dblTaxAmount > ABS(@SuspenseAmt)
        BEGIN
            SELECT 'Error: The tax can''t be more than the amount, or negative.' AS Result;
            RETURN;
        END
        IF @dblTaxAmount <> 0 AND @iTargetAccountID = @iControlAccountID
        BEGIN
            SELECT 'Error: A line with tax can''t go to the tax control account itself.' AS Result;
            RETURN;
        END
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
    --    line B posts the same direction to the target account (less any tax),
    --    line C posts the tax to the control account
    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit)
    VALUES (
        @NewJournalID,
        @SuspenseAccID,
        CASE WHEN @SuspenseAmt < 0 THEN ABS(@SuspenseAmt) ELSE 0 END,
        CASE WHEN @SuspenseAmt > 0 THEN @SuspenseAmt ELSE 0 END
    );

    DECLARE @dblTax DECIMAL(19,4) = ISNULL(@dblTaxAmount, 0);

    INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, iTaxCodeID, dblTaxAmount)
    VALUES (
        @NewJournalID,
        @iTargetAccountID,
        CASE WHEN @SuspenseAmt > 0 THEN @SuspenseAmt - @dblTax ELSE 0 END,
        CASE WHEN @SuspenseAmt < 0 THEN ABS(@SuspenseAmt) - @dblTax ELSE 0 END,
        @iTaxCodeID,
        @dblTaxAmount
    );

    IF @dblTax <> 0
        INSERT INTO tblJournalLines (iJournalID, iAccountID, dblDebit, dblCredit, iTaxCodeID, bIsTaxLine)
        VALUES (
            @NewJournalID,
            @iControlAccountID,
            CASE WHEN @SuspenseAmt > 0 THEN @dblTax ELSE 0 END,
            CASE WHEN @SuspenseAmt < 0 THEN @dblTax ELSE 0 END,
            @iTaxCodeID,
            1
        );

    -- 4. Mark the original as matched
    UPDATE tblJournalLines
    SET iMatchedJournalID = @NewJournalID
    WHERE iJournalLineID = @iSuspenseLineID;

    COMMIT TRANSACTION;

    SELECT 'Success' AS Result;
END
GO

-- =============================================
-- Allocation rules with a tax code
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetAllocationRules]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The portfolio's allocation rules in the order they are tried,
--              with the target account's number and name.
--              16-Oct-2026: and the tax code, if the rule sets one.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT R.iRuleID, R.strContains, R.strDirection, R.iAccountID, R.strDescription, R.iSortOrder,
           A.iAccountNumber, A.strAccountName, R.iTaxCodeID, T.strCode AS strTaxCode
    FROM tblAllocationRule R
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = R.iAccountID
    LEFT JOIN tblTaxCodes T ON T.iTaxCodeID = R.iTaxCodeID AND T.iUserID = R.iUserID
    WHERE R.iUserID = @iUserId
    ORDER BY R.iSortOrder, R.iRuleID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveAllocationRule]
(
    @iUserId        INT,
    @iRuleID        INT = NULL,          -- NULL adds a rule
    @strContains    VARCHAR(200),
    @strDirection   VARCHAR(10),
    @iAccountID     INT,
    @strDescription VARCHAR(500) = NULL,
    @iSortOrder     INT = NULL,          -- NULL: last (new) or unchanged (existing)
    @iTaxCodeID     INT = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Adds or changes one of the portfolio's allocation rules. The
--              account must be a postable account of the portfolio (or a
--              system one) and not Bank Import Suspense.
--              16-Oct-2026: optional tax code, one of the portfolio's.
--              Returns one row: Result = 'Success', RuleId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SET @strContains = LTRIM(RTRIM(ISNULL(@strContains, '')));
    SET @strDescription = NULLIF(LTRIM(RTRIM(ISNULL(@strDescription, ''))), '');

    IF @strContains = ''
    BEGIN
        SELECT 'Error: Enter the text to look for.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF ISNULL(@strDirection, '') NOT IN ('Any', 'Deposit', 'Withdrawal')
    BEGIN
        SELECT 'Error: Choose deposits, withdrawals or both.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts
                   WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                     AND ISNULL(bIsPostable, 1) = 1 AND strAccountName <> 'Bank Import Suspense')
    BEGIN
        SELECT 'Error: Choose an account from the list.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF @iTaxCodeID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tblTaxCodes WHERE iTaxCodeID = @iTaxCodeID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: Choose a tax code from the list.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF @iRuleID IS NULL
    BEGIN
        INSERT INTO tblAllocationRule (iUserID, strContains, strDirection, iAccountID, strDescription, iSortOrder, iTaxCodeID)
        VALUES (@iUserId, @strContains, @strDirection, @iAccountID, @strDescription,
                ISNULL(@iSortOrder, (SELECT ISNULL(MAX(iSortOrder), 0) + 10 FROM tblAllocationRule WHERE iUserID = @iUserId)),
                @iTaxCodeID);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS RuleId;
        RETURN;
    END

    UPDATE tblAllocationRule
    SET strContains = @strContains,
        strDirection = @strDirection,
        iAccountID = @iAccountID,
        strDescription = @strDescription,
        iSortOrder = ISNULL(@iSortOrder, iSortOrder),
        iTaxCodeID = @iTaxCodeID
    WHERE iRuleID = @iRuleID AND iUserID = @iUserId;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That rule was not found in this portfolio.' AS Result, NULL AS RuleId;
    ELSE
        SELECT 'Success' AS Result, @iRuleID AS RuleId;
END
GO
