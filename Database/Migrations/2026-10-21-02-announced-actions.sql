-- =============================================
-- Migration: announced dividends and splits
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Corporate actions > Announced dividends and splits. Run it after 2026-10-15-corporate-actions.sql.
-- Safe to run more than once. It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblAnnouncedDividends: dividends announced for a stock (one row per stock and ex-date), filled by
--     the daily price updater from the price provider (EODHD). Shared by every portfolio.
--   * tblAnnouncedSplits: splits and consolidations announced for a stock, the same way.
--   * tblAnnouncedActionDismissals: an announcement a portfolio chose to ignore, so it isn't offered again.
-- Confirming an announcement adds the payout or split through the existing procs (uspAddPayout,
-- uspApplyStockSplit), so the journals and FIFO cost base are posted as usual.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblAnnouncedDividends', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAnnouncedDividends (
        iAnnouncedDividendID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAnnouncedDividends PRIMARY KEY,
        strStockExchangeCode VARCHAR(50) NOT NULL,
        strStockCode         VARCHAR(50) NOT NULL,
        dtmExDate            DATE NOT NULL,
        dtmRecordDate        DATE NULL,
        dtmPaymentDate       DATE NULL,
        dtmDeclarationDate   DATE NULL,
        dblAmountPerUnit     FLOAT NOT NULL,               -- not adjusted for later splits
        strCurrency          VARCHAR(10) NULL,
        strPeriod            VARCHAR(50) NULL,             -- Interim, Final, Quarterly ... as the provider says
        strSource            VARCHAR(50) NOT NULL,         -- e.g. EODHD
        dtmFetchedDate       DATETIME NOT NULL CONSTRAINT DF_tblAnnouncedDividends_Fetched DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblAnnouncedDividends_Stock ON dbo.tblAnnouncedDividends (strStockExchangeCode, strStockCode, dtmExDate);
END
GO

IF OBJECT_ID('dbo.tblAnnouncedSplits', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAnnouncedSplits (
        iAnnouncedSplitID    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAnnouncedSplits PRIMARY KEY,
        strStockExchangeCode VARCHAR(50) NOT NULL,
        strStockCode         VARCHAR(50) NOT NULL,
        dtmEffectiveDate     DATE NOT NULL,                -- first day on the new basis
        iNewUnits            INT NOT NULL,
        iOldUnits            INT NOT NULL,
        strSource            VARCHAR(50) NOT NULL,
        dtmFetchedDate       DATETIME NOT NULL CONSTRAINT DF_tblAnnouncedSplits_Fetched DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblAnnouncedSplits_Stock ON dbo.tblAnnouncedSplits (strStockExchangeCode, strStockCode, dtmEffectiveDate);
END
GO

IF OBJECT_ID('dbo.tblAnnouncedActionDismissals', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAnnouncedActionDismissals (
        iUserID         INT NOT NULL,                      -- the portfolio
        strKind         VARCHAR(20) NOT NULL,              -- Dividend or Split
        iAnnouncedID    INT NOT NULL,
        dtmDismissedDate DATETIME NOT NULL CONSTRAINT DF_tblAnnouncedActionDismissals_Date DEFAULT (GETDATE()),
        CONSTRAINT PK_tblAnnouncedActionDismissals PRIMARY KEY (iUserID, strKind, iAnnouncedID)
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveAnnouncedDividend
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @dtmExDate            DATE,
    @dtmRecordDate        DATE = NULL,
    @dtmPaymentDate       DATE = NULL,
    @dtmDeclarationDate   DATE = NULL,
    @dblAmountPerUnit     FLOAT,
    @strCurrency          VARCHAR(10) = NULL,
    @strPeriod            VARCHAR(50) = NULL,
    @strSource            VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblAnnouncedDividends
       SET dtmRecordDate = @dtmRecordDate, dtmPaymentDate = @dtmPaymentDate, dtmDeclarationDate = @dtmDeclarationDate,
           dblAmountPerUnit = @dblAmountPerUnit, strCurrency = @strCurrency, strPeriod = @strPeriod,
           strSource = @strSource, dtmFetchedDate = GETDATE()
     WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode AND dtmExDate = @dtmExDate;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblAnnouncedDividends (strStockExchangeCode, strStockCode, dtmExDate, dtmRecordDate, dtmPaymentDate,
               dtmDeclarationDate, dblAmountPerUnit, strCurrency, strPeriod, strSource)
        VALUES (@strStockExchangeCode, @strStockCode, @dtmExDate, @dtmRecordDate, @dtmPaymentDate,
               @dtmDeclarationDate, @dblAmountPerUnit, @strCurrency, @strPeriod, @strSource);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveAnnouncedSplit
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @dtmEffectiveDate     DATE,
    @iNewUnits            INT,
    @iOldUnits            INT,
    @strSource            VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblAnnouncedSplits
       SET iNewUnits = @iNewUnits, iOldUnits = @iOldUnits, strSource = @strSource, dtmFetchedDate = GETDATE()
     WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode AND dtmEffectiveDate = @dtmEffectiveDate;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblAnnouncedSplits (strStockExchangeCode, strStockCode, dtmEffectiveDate, iNewUnits, iOldUnits, strSource)
        VALUES (@strStockExchangeCode, @strStockCode, @dtmEffectiveDate, @iNewUnits, @iOldUnits, @strSource);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDismissAnnouncedAction
    @iUserID      INT,
    @strKind      VARCHAR(20),
    @iAnnouncedID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblAnnouncedActionDismissals
                   WHERE iUserID = @iUserID AND strKind = @strKind AND iAnnouncedID = @iAnnouncedID)
        INSERT dbo.tblAnnouncedActionDismissals (iUserID, strKind, iAnnouncedID) VALUES (@iUserID, @strKind, @iAnnouncedID);
END
GO

PRINT 'Announced dividends and splits migration done.';
GO
