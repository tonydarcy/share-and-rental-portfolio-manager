-- =============================================
-- Migration: ETF and managed fund look-through
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with the
-- look-through section of Reports > Diversity. Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblFundExposure: what is inside an ETF or fund, as weights (0 to 1) by sector, country or region, and its
--     largest holdings. iUserID NULL rows come from the price updater (EODHD fundamentals) and are shared;
--     rows with a portfolio's iUserID are that portfolio's own entry and are used instead of the shared ones.
--     A shared row of kind 'None' notes a stock the provider has no fund data for, so it isn't asked every day.
--   * uspSaveFundExposure, uspDeleteFundExposure.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblFundExposure', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFundExposure (
        iFundExposureID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFundExposure PRIMARY KEY,
        iUserID              INT NULL,                     -- NULL: shared, from the provider
        strStockExchangeCode VARCHAR(50) NOT NULL,
        strStockCode         VARCHAR(50) NOT NULL,
        strKind              VARCHAR(20) NOT NULL,         -- Sector, Country, Holding or None
        strName              NVARCHAR(200) NOT NULL,       -- sector or country name, or a holding's code
        strLabel             NVARCHAR(200) NULL,           -- a holding's company name
        dblWeight            FLOAT NOT NULL,               -- 0.25 is 25%
        dtmAsAt              DATE NULL,
        strSource            VARCHAR(50) NULL,
        dtmSaved             DATETIME NOT NULL CONSTRAINT DF_tblFundExposure_Saved DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblFundExposure_Stock ON dbo.tblFundExposure (strStockExchangeCode, strStockCode, iUserID);
END
GO

-- =============================================
-- Replaces a fund's look-through (shared when @iUserID is NULL, else that portfolio's own).
-- @xmlWeights is <w><e k="Sector" n="Financials" l="" v="0.31"/>...</w>, weights as fractions.
-- Returns Result: 'Success' or 'Error: ...'.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveFundExposure
    @iUserID              INT = NULL,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @dtmAsAt              DATE = NULL,
    @strSource            VARCHAR(50) = NULL,
    @xmlWeights           XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @strStockExchangeCode = UPPER(LTRIM(RTRIM(@strStockExchangeCode)));
    SET @strStockCode = UPPER(LTRIM(RTRIM(@strStockCode)));

    DECLARE @W TABLE (strKind VARCHAR(20), strName NVARCHAR(200), strLabel NVARCHAR(200), dblWeight FLOAT);
    INSERT INTO @W (strKind, strName, strLabel, dblWeight)
    SELECT X.e.value('@k', 'VARCHAR(20)'), LTRIM(RTRIM(X.e.value('@n', 'NVARCHAR(200)'))),
           NULLIF(LTRIM(RTRIM(X.e.value('@l', 'NVARCHAR(200)'))), ''), X.e.value('@v', 'FLOAT')
    FROM @xmlWeights.nodes('/w/e') AS X(e);

    DECLARE @Error VARCHAR(300) = CASE
        WHEN ISNULL(@strStockCode, '') = '' THEN 'Enter the fund''s code.'
        WHEN EXISTS (SELECT 1 FROM @W WHERE strKind NOT IN ('Sector', 'Country', 'Holding', 'None')) THEN 'Each line must be a sector, country or holding.'
        WHEN EXISTS (SELECT 1 FROM @W WHERE ISNULL(strName, '') = '' OR dblWeight IS NULL OR dblWeight < 0 OR dblWeight > 1) THEN 'Each line needs a name and a weight from 0% to 100%.'
        WHEN EXISTS (SELECT strKind FROM @W WHERE strKind <> 'None' GROUP BY strKind HAVING SUM(dblWeight) > 1.02) THEN 'The weights of one kind add up to more than 100%.'
        WHEN EXISTS (SELECT strKind, strName FROM @W GROUP BY strKind, strName HAVING COUNT(*) > 1) THEN 'A name appears twice.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE dbo.tblFundExposure
    WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode
      AND ((@iUserID IS NULL AND iUserID IS NULL) OR iUserID = @iUserID);
    INSERT dbo.tblFundExposure (iUserID, strStockExchangeCode, strStockCode, strKind, strName, strLabel, dblWeight, dtmAsAt, strSource)
    SELECT @iUserID, @strStockExchangeCode, @strStockCode, strKind, strName, strLabel, dblWeight, @dtmAsAt, @strSource FROM @W;
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

-- Removes a portfolio's own look-through for a fund, so the shared one (if any) is used again
CREATE OR ALTER PROCEDURE dbo.uspDeleteFundExposure
    @iUserID              INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblFundExposure
    WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Fund look-through migration done.';
GO
