-- =============================================
-- Migration: email alerts
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Alerts (account menu). Run it after 2026-10-21-02-announced-actions.sql. Safe to run more than once.
-- It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblAlertSettings: per login and portfolio, which emails to send (price targets, dividends announced,
--     Navexa sync failures, bank lines waiting to be allocated) and when it last looked.
--   * tblPriceAlerts: a price target for a holding (above or below), sent once when reached.
--   * uspSaveAlertSettings, uspSavePriceAlert, uspDeletePriceAlert, uspMarkPriceAlertSent, uspMarkAlertsChecked.
-- The site checks every 30 minutes and emails only when email (SMTP) is set up.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblAnnouncedDividends', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-21-02-announced-actions.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblAlertSettings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAlertSettings (
        iLoginID           INT NOT NULL,                   -- who gets the email (tblUsers row of the login)
        iPortfolioID       INT NOT NULL,
        bPriceTargets      BIT NOT NULL CONSTRAINT DF_tblAlertSettings_Price DEFAULT (1),
        bDividends         BIT NOT NULL CONSTRAINT DF_tblAlertSettings_Div DEFAULT (1),
        bSyncFailures      BIT NOT NULL CONSTRAINT DF_tblAlertSettings_Sync DEFAULT (1),
        bUnallocated       BIT NOT NULL CONSTRAINT DF_tblAlertSettings_Unalloc DEFAULT (0),
        dtmLastChecked     DATETIME NOT NULL CONSTRAINT DF_tblAlertSettings_Checked DEFAULT (GETUTCDATE()),  -- UTC
        dtmUnallocatedSent DATE NULL,                      -- at most one reminder a day
        CONSTRAINT PK_tblAlertSettings PRIMARY KEY (iLoginID, iPortfolioID)
    );
END
GO

IF OBJECT_ID('dbo.tblPriceAlerts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblPriceAlerts (
        iPriceAlertID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblPriceAlerts PRIMARY KEY,
        iLoginID             INT NOT NULL,
        iPortfolioID         INT NOT NULL,
        strStockExchangeCode VARCHAR(50) NOT NULL,
        strStockCode         VARCHAR(50) NOT NULL,
        strDirection         VARCHAR(10) NOT NULL,         -- Above or Below
        dblTarget            FLOAT NOT NULL,
        strNote              NVARCHAR(200) NULL,
        dtmCreated           DATETIME NOT NULL CONSTRAINT DF_tblPriceAlerts_Created DEFAULT (GETUTCDATE()),
        dtmSent              DATETIME NULL,
        dblPriceSent         FLOAT NULL
    );
    CREATE INDEX IX_tblPriceAlerts_Portfolio ON dbo.tblPriceAlerts (iPortfolioID, iLoginID);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveAlertSettings
    @iLoginID      INT,
    @iPortfolioID  INT,
    @bPriceTargets BIT,
    @bDividends    BIT,
    @bSyncFailures BIT,
    @bUnallocated  BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblAlertSettings
       SET bPriceTargets = @bPriceTargets, bDividends = @bDividends, bSyncFailures = @bSyncFailures, bUnallocated = @bUnallocated
     WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblAlertSettings (iLoginID, iPortfolioID, bPriceTargets, bDividends, bSyncFailures, bUnallocated)
        VALUES (@iLoginID, @iPortfolioID, @bPriceTargets, @bDividends, @bSyncFailures, @bUnallocated);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSavePriceAlert
    @iLoginID             INT,
    @iPortfolioID         INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @strDirection         VARCHAR(10),
    @dblTarget            FLOAT,
    @strNote              NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strDirection NOT IN ('Above', 'Below') OR @dblTarget IS NULL OR @dblTarget <= 0
    BEGIN
        SELECT 'Error: Choose above or below and a target price above zero.' AS Result;
        RETURN;
    END
    INSERT dbo.tblPriceAlerts (iLoginID, iPortfolioID, strStockExchangeCode, strStockCode, strDirection, dblTarget, strNote)
    VALUES (@iLoginID, @iPortfolioID, UPPER(LTRIM(RTRIM(@strStockExchangeCode))), UPPER(LTRIM(RTRIM(@strStockCode))), @strDirection, @dblTarget, @strNote);
    -- Make sure the login has settings, so the alert is checked
    IF NOT EXISTS (SELECT 1 FROM dbo.tblAlertSettings WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID)
        INSERT dbo.tblAlertSettings (iLoginID, iPortfolioID) VALUES (@iLoginID, @iPortfolioID);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeletePriceAlert
    @iLoginID      INT,
    @iPriceAlertID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblPriceAlerts WHERE iPriceAlertID = @iPriceAlertID AND iLoginID = @iLoginID;
    SELECT CASE WHEN @@ROWCOUNT > 0 THEN 'Success' ELSE 'Error: That alert isn''t yours.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspMarkPriceAlertSent
    @iPriceAlertID INT,
    @dblPrice      FLOAT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblPriceAlerts SET dtmSent = GETUTCDATE(), dblPriceSent = @dblPrice WHERE iPriceAlertID = @iPriceAlertID AND dtmSent IS NULL;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspMarkAlertsChecked
    @iLoginID          INT,
    @iPortfolioID      INT,
    @dtmChecked        DATETIME,
    @bUnallocatedSent  BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblAlertSettings
       SET dtmLastChecked = @dtmChecked,
           dtmUnallocatedSent = CASE WHEN @bUnallocatedSent = 1 THEN CAST(@dtmChecked AS DATE) ELSE dtmUnallocatedSent END
     WHERE iLoginID = @iLoginID AND iPortfolioID = @iPortfolioID;
END
GO

PRINT 'Alerts migration done.';
GO
