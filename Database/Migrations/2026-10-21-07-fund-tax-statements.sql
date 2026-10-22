-- =============================================
-- Migration: ETF and managed fund annual tax statements (AMIT / AMMA)
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Income > Fund tax statements. Run it after 2026-10-15-lock-periods.sql (it uses fnLockedTo).
-- Safe to run more than once. It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblFundTaxStatements: one row per fund per financial year, keyed in from the fund's annual tax statement
--     (AMMA statement for an attribution MIT, or the standard distribution statement): income components, capital
--     gains, foreign income and credits, NCMI, and the cost base increase or decrease.
--   * uspSaveFundTaxStatement, uspDeleteFundTaxStatement (both refuse a year in a locked period).
-- The capital gains report, cost bases and tax return figures read these rows. The ledger doesn't change.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnLockedTo', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-lock-periods.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblFundTaxStatements', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFundTaxStatements (
        iFundTaxStatementID    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFundTaxStatements PRIMARY KEY,
        iUserID                INT NOT NULL,             -- the portfolio
        strStockExchangeCode   VARCHAR(50) NOT NULL,
        strStockCode           VARCHAR(50) NOT NULL,
        iFinancialYear         INT NOT NULL,             -- the year it ends, e.g. 2026 for 2025-26
        dtmYearEnd             DATE NOT NULL,            -- the cost base change happens on this day
        dblCashDistribution    FLOAT NOT NULL DEFAULT (0),
        dblFranked             FLOAT NOT NULL DEFAULT (0), -- franked distributions (without the credits)
        dblFrankingCredits     FLOAT NOT NULL DEFAULT (0),
        dblUnfranked           FLOAT NOT NULL DEFAULT (0),
        dblInterest            FLOAT NOT NULL DEFAULT (0),
        dblOtherIncome         FLOAT NOT NULL DEFAULT (0), -- other Australian income, not NCMI
        dblNcmi                FLOAT NOT NULL DEFAULT (0), -- non-concessional MIT income
        dblDiscountedGain      FLOAT NOT NULL DEFAULT (0), -- capital gains, discount method, after the fund's 50% discount
        dblOtherGain           FLOAT NOT NULL DEFAULT (0), -- capital gains, indexation and other methods
        dblForeignIncome       FLOAT NOT NULL DEFAULT (0), -- assessable foreign income (before foreign tax)
        dblForeignTaxCredits   FLOAT NOT NULL DEFAULT (0),
        dblTfnWithheld         FLOAT NOT NULL DEFAULT (0),
        dblCostBaseIncrease    FLOAT NOT NULL DEFAULT (0), -- AMIT cost base net amount: increase
        dblCostBaseDecrease    FLOAT NOT NULL DEFAULT (0), -- AMIT net decrease, or tax-deferred amounts (non-AMIT)
        strComment             NVARCHAR(500) NULL,
        dtmSaved               DATETIME NOT NULL CONSTRAINT DF_tblFundTaxStatements_Saved DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblFundTaxStatements ON dbo.tblFundTaxStatements (iUserID, strStockExchangeCode, strStockCode, iFinancialYear);
END
GO

-- =============================================
-- Adds (@iFundTaxStatementID 0) or changes a statement. Returns Result ('Success' or 'Error: ...') and Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveFundTaxStatement
    @iUserID              INT,
    @iFundTaxStatementID  INT = 0,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @iFinancialYear       INT,
    @dtmYearEnd           DATE,
    @dblCashDistribution  FLOAT = 0,
    @dblFranked           FLOAT = 0,
    @dblFrankingCredits   FLOAT = 0,
    @dblUnfranked         FLOAT = 0,
    @dblInterest          FLOAT = 0,
    @dblOtherIncome       FLOAT = 0,
    @dblNcmi              FLOAT = 0,
    @dblDiscountedGain    FLOAT = 0,
    @dblOtherGain         FLOAT = 0,
    @dblForeignIncome     FLOAT = 0,
    @dblForeignTaxCredits FLOAT = 0,
    @dblTfnWithheld       FLOAT = 0,
    @dblCostBaseIncrease  FLOAT = 0,
    @dblCostBaseDecrease  FLOAT = 0,
    @strComment           NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strStockExchangeCode = UPPER(LTRIM(RTRIM(@strStockExchangeCode)));
    SET @strStockCode = UPPER(LTRIM(RTRIM(@strStockCode)));
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @OldEnd DATE = (SELECT dtmYearEnd FROM dbo.tblFundTaxStatements WHERE iFundTaxStatementID = @iFundTaxStatementID AND iUserID = @iUserID);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN @iFundTaxStatementID > 0 AND @OldEnd IS NULL THEN 'That statement isn''t in this portfolio.'
        WHEN NOT EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iUserId = @iUserID AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode)
            THEN 'That fund isn''t in this portfolio.'
        WHEN @Locked IS NOT NULL AND (@dtmYearEnd <= @Locked OR @OldEnd <= @Locked) THEN 'That year is locked, so its statements can''t be changed.'
        WHEN @dblCashDistribution < 0 OR @dblFranked < 0 OR @dblFrankingCredits < 0 OR @dblUnfranked < 0 OR @dblInterest < 0 OR @dblOtherIncome < 0
          OR @dblNcmi < 0 OR @dblDiscountedGain < 0 OR @dblOtherGain < 0 OR @dblForeignIncome < 0 OR @dblForeignTaxCredits < 0
          OR @dblTfnWithheld < 0 OR @dblCostBaseIncrease < 0 OR @dblCostBaseDecrease < 0 THEN 'Amounts can''t be negative.'
        WHEN EXISTS (SELECT 1 FROM dbo.tblFundTaxStatements WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode
                     AND strStockCode = @strStockCode AND iFinancialYear = @iFinancialYear AND iFundTaxStatementID <> @iFundTaxStatementID)
            THEN 'There is already a statement for that fund and year.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    IF @iFundTaxStatementID > 0
        UPDATE dbo.tblFundTaxStatements
           SET strStockExchangeCode = @strStockExchangeCode, strStockCode = @strStockCode, iFinancialYear = @iFinancialYear, dtmYearEnd = @dtmYearEnd,
               dblCashDistribution = @dblCashDistribution, dblFranked = @dblFranked, dblFrankingCredits = @dblFrankingCredits,
               dblUnfranked = @dblUnfranked, dblInterest = @dblInterest, dblOtherIncome = @dblOtherIncome, dblNcmi = @dblNcmi,
               dblDiscountedGain = @dblDiscountedGain, dblOtherGain = @dblOtherGain, dblForeignIncome = @dblForeignIncome,
               dblForeignTaxCredits = @dblForeignTaxCredits, dblTfnWithheld = @dblTfnWithheld, dblCostBaseIncrease = @dblCostBaseIncrease,
               dblCostBaseDecrease = @dblCostBaseDecrease, strComment = @strComment, dtmSaved = GETDATE()
         WHERE iFundTaxStatementID = @iFundTaxStatementID AND iUserID = @iUserID;
    ELSE
    BEGIN
        INSERT dbo.tblFundTaxStatements (iUserID, strStockExchangeCode, strStockCode, iFinancialYear, dtmYearEnd, dblCashDistribution, dblFranked,
            dblFrankingCredits, dblUnfranked, dblInterest, dblOtherIncome, dblNcmi, dblDiscountedGain, dblOtherGain, dblForeignIncome,
            dblForeignTaxCredits, dblTfnWithheld, dblCostBaseIncrease, dblCostBaseDecrease, strComment)
        VALUES (@iUserID, @strStockExchangeCode, @strStockCode, @iFinancialYear, @dtmYearEnd, @dblCashDistribution, @dblFranked,
            @dblFrankingCredits, @dblUnfranked, @dblInterest, @dblOtherIncome, @dblNcmi, @dblDiscountedGain, @dblOtherGain, @dblForeignIncome,
            @dblForeignTaxCredits, @dblTfnWithheld, @dblCostBaseIncrease, @dblCostBaseDecrease, @strComment);
        SET @iFundTaxStatementID = SCOPE_IDENTITY();
    END
    SELECT 'Success' AS Result, @iFundTaxStatementID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteFundTaxStatement
    @iUserID             INT,
    @iFundTaxStatementID INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @End DATE = (SELECT dtmYearEnd FROM dbo.tblFundTaxStatements WHERE iFundTaxStatementID = @iFundTaxStatementID AND iUserID = @iUserID);
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @End IS NULL
        SELECT 'Error: That statement isn''t in this portfolio.' AS Result;
    ELSE IF @Locked IS NOT NULL AND @End <= @Locked
        SELECT 'Error: That year is locked, so its statements can''t be changed.' AS Result;
    ELSE
    BEGIN
        DELETE dbo.tblFundTaxStatements WHERE iFundTaxStatementID = @iFundTaxStatementID AND iUserID = @iUserID;
        SELECT 'Success' AS Result;
    END
END
GO

PRINT 'Fund tax statements migration done.';
GO
