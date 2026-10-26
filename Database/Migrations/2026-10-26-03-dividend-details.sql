-- =============================================
-- Migration: franking and DRP prices for announced dividends
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Import > Dividend details.
-- Run it after 2026-10-21-02-announced-actions.sql. Safe to run more than once. It only adds things.
--
-- The daily feed (EODHD) gives a dividend's amount and dates but not its franking or DRP price. These come from a
-- dividend details file the site admin uploads (Import > Dividend details), and later from an automatic source.
--   * tblAnnouncedDividends gets dblFrankedPercent (0 to 100), dblDrpPrice and strDetailsSource.
--   * uspSaveAnnouncedDividendDetails sets them for a stock's dividend on an ex-date, adding the dividend if the feed
--     hasn't (when the amount is given). Returns Result: Updated, Added or Error: ...
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

IF COL_LENGTH('dbo.tblAnnouncedDividends', 'dblFrankedPercent') IS NULL
    ALTER TABLE dbo.tblAnnouncedDividends ADD dblFrankedPercent FLOAT NULL;
IF COL_LENGTH('dbo.tblAnnouncedDividends', 'dblDrpPrice') IS NULL
    ALTER TABLE dbo.tblAnnouncedDividends ADD dblDrpPrice FLOAT NULL;
IF COL_LENGTH('dbo.tblAnnouncedDividends', 'strDetailsSource') IS NULL
    ALTER TABLE dbo.tblAnnouncedDividends ADD strDetailsSource VARCHAR(100) NULL;
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveAnnouncedDividendDetails
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @dtmExDate            DATE,
    @dblFrankedPercent    FLOAT = NULL,
    @dblDrpPrice          FLOAT = NULL,
    @strDetailsSource     VARCHAR(100),
    @dblAmountPerUnit     FLOAT = NULL,     -- only used to add a dividend the feed doesn't have
    @dtmPaymentDate       DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @strStockExchangeCode = UPPER(LTRIM(RTRIM(@strStockExchangeCode))), @strStockCode = UPPER(LTRIM(RTRIM(@strStockCode)));
    IF @dblFrankedPercent IS NOT NULL AND (@dblFrankedPercent < 0 OR @dblFrankedPercent > 100)
    BEGIN
        SELECT 'Error: The franked percentage must be between 0 and 100.' AS Result;
        RETURN;
    END
    IF @dblDrpPrice IS NOT NULL AND @dblDrpPrice <= 0
    BEGIN
        SELECT 'Error: The DRP price must be more than zero.' AS Result;
        RETURN;
    END

    UPDATE dbo.tblAnnouncedDividends
       SET dblFrankedPercent = ISNULL(@dblFrankedPercent, dblFrankedPercent),
           dblDrpPrice = ISNULL(@dblDrpPrice, dblDrpPrice),
           dtmPaymentDate = ISNULL(dtmPaymentDate, @dtmPaymentDate),
           strDetailsSource = @strDetailsSource
     WHERE strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode AND dtmExDate = @dtmExDate;
    IF @@ROWCOUNT > 0
    BEGIN
        SELECT 'Updated' AS Result;
        RETURN;
    END
    IF ISNULL(@dblAmountPerUnit, 0) <= 0
    BEGIN
        SELECT 'Error: No ' + @strStockCode + ' dividend with that ex-date is on file; give the amount per share to add it.' AS Result;
        RETURN;
    END
    INSERT dbo.tblAnnouncedDividends (strStockExchangeCode, strStockCode, dtmExDate, dtmPaymentDate, dblAmountPerUnit,
           strSource, dblFrankedPercent, dblDrpPrice, strDetailsSource)
    VALUES (@strStockExchangeCode, @strStockCode, @dtmExDate, @dtmPaymentDate, @dblAmountPerUnit,
           LEFT(@strDetailsSource, 50), @dblFrankedPercent, @dblDrpPrice, @strDetailsSource);
    SELECT 'Added' AS Result;
END
GO
