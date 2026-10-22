-- =============================================
-- Migration: share trader treatment
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Tax > Shares as trading
-- stock. Run it after 2026-10-15-lock-periods.sql. Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblShareTrader: a portfolio whose shares are trading stock (someone carrying on a business of share trading)
--     from a financial year on, and how they're valued at the year end (cost, market value, or the lower of the two
--     for each holding). From that year the tax figures count the trading profit as income instead of capital gains.
--   * uspGetShareTrader, uspSaveShareTrader (refuses a change that would alter a locked year).
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

IF OBJECT_ID('dbo.tblShareTrader', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblShareTrader (
        iUserID        INT NOT NULL CONSTRAINT PK_tblShareTrader PRIMARY KEY,
        iFromYear      INT NULL,                 -- first financial year (the year it ends) as trading stock; NULL = not a trader
        strValuation   VARCHAR(10) NOT NULL CONSTRAINT DF_tblShareTrader_Valuation DEFAULT ('Cost'),   -- Cost, Market or Lower
        dtmSaved       DATETIME NOT NULL CONSTRAINT DF_tblShareTrader_Saved DEFAULT (GETDATE())
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetShareTrader
    @iUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT iFromYear AS FromYear, strValuation AS Valuation FROM dbo.tblShareTrader WHERE iUserID = @iUserID;
END
GO

-- @iFromYear NULL stops treating the shares as trading stock. @dtmFirstYearEnd is the end of the earlier of the old and
-- new first years (the first year whose figures change). Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspSaveShareTrader
    @iUserID         INT,
    @iFromYear       INT = NULL,
    @strValuation    VARCHAR(10) = 'Cost',
    @dtmFirstYearEnd DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF ISNULL(@strValuation, '') NOT IN ('Cost', 'Market', 'Lower')
    BEGIN
        SELECT 'Error: Choose how the shares are valued at the year end.' AS Result;
        RETURN;
    END
    IF @Locked IS NOT NULL AND @dtmFirstYearEnd IS NOT NULL AND @dtmFirstYearEnd <= @Locked
    BEGIN
        SELECT 'Error: That would change a locked year. Unlock it first, or start from a later year.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM dbo.tblShareTrader WHERE iUserID = @iUserID)
        UPDATE dbo.tblShareTrader SET iFromYear = @iFromYear, strValuation = @strValuation, dtmSaved = GETDATE() WHERE iUserID = @iUserID;
    ELSE
        INSERT dbo.tblShareTrader (iUserID, iFromYear, strValuation) VALUES (@iUserID, @iFromYear, @strValuation);
    SELECT 'Success' AS Result;
END
GO

PRINT 'Share trader migration done.';
GO
