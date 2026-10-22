-- =============================================
-- Migration: manual prices for unlisted assets
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Prices > Manual prices. Safe to run more than once. It only adds procedures.
--
-- Managed funds, unlisted shares, property and other assets are recorded as trades on the markets
-- FUND, UNLISTED, PROPERTY and OTHER. Their prices are entered by hand (or from a CSV) for one portfolio:
-- rows in tblSharePrice and tblSharePriceCache with iUserID set to the portfolio, which the holdings and
-- unrealised gains procs already prefer for that portfolio, and which other portfolios don't see.
--   * uspGetManualPrices, uspSaveManualPrice, uspDeleteManualPrice.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE dbo.uspGetManualPrices
    @iUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT iSharePriceId AS PriceId, strStockExchangeCode AS ExchangeCode, strStockCode AS StockCode,
           CAST(dtmDetailsAsAtDate AS DATE) AS [Date], dblClosePrice AS Price
    FROM dbo.tblSharePrice
    WHERE iUserID = @iUserID
    ORDER BY strStockCode, dtmDetailsAsAtDate DESC;
END
GO

-- One price a day per holding: replaces this portfolio's price for that day. Returns Result 'Success' or 'Error: ...'.
CREATE OR ALTER PROCEDURE dbo.uspSaveManualPrice
    @iUserID              INT,
    @strStockExchangeCode VARCHAR(50),
    @strStockCode         VARCHAR(50),
    @dtmDate              DATE,
    @dblClosePrice        FLOAT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @iUserID IS NULL OR @iUserID <= 0 OR @dblClosePrice IS NULL OR @dblClosePrice <= 0
    BEGIN
        SELECT 'Error: Enter a price above zero.' AS Result;
        RETURN;
    END
    IF NOT EXISTS (SELECT 1 FROM dbo.tblShareTrades WHERE iUserId = @iUserID
                   AND strStockExchangeCode = @strStockExchangeCode AND strStockCode = @strStockCode)
    BEGIN
        SELECT 'Error: There are no trades for that holding in this portfolio.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE dbo.tblSharePriceCache WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode
        AND strStockCode = @strStockCode AND CAST(dtmDetailsAsAtDate AS DATE) = @dtmDate;
    DELETE dbo.tblSharePrice WHERE iUserID = @iUserID AND strStockExchangeCode = @strStockExchangeCode
        AND strStockCode = @strStockCode AND CAST(dtmDetailsAsAtDate AS DATE) = @dtmDate;
    INSERT dbo.tblSharePrice (strStockExchangeCode, strStockCode, dtmDetailsAsAtDate, dblOpenPrice, dblHighPrice, dblLowPrice,
                              dblClosePrice, iVolume, iUserID)
    VALUES (@strStockExchangeCode, @strStockCode, @dtmDate, @dblClosePrice, @dblClosePrice, @dblClosePrice, @dblClosePrice, 0, @iUserID);
    INSERT dbo.tblSharePriceCache (strStockExchangeCode, strStockCode, dtmDetailsAsAtDate, dblOpenPrice, dblHighPrice, dblLowPrice,
                                   dblClosePrice, iVolume, iUserID, iSharePriceId)
    VALUES (@strStockExchangeCode, @strStockCode, @dtmDate, @dblClosePrice, @dblClosePrice, @dblClosePrice, @dblClosePrice, 0, @iUserID, SCOPE_IDENTITY());
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteManualPrice
    @iUserID  INT,
    @iSharePriceId INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DELETE C FROM dbo.tblSharePriceCache C JOIN dbo.tblSharePrice P
        ON P.iUserID = C.iUserID AND P.strStockExchangeCode = C.strStockExchangeCode AND P.strStockCode = C.strStockCode
       AND P.dtmDetailsAsAtDate = C.dtmDetailsAsAtDate
     WHERE P.iSharePriceId = @iSharePriceId AND P.iUserID = @iUserID;
    DELETE dbo.tblSharePrice WHERE iSharePriceId = @iSharePriceId AND iUserID = @iUserID;
    DECLARE @Deleted INT = @@ROWCOUNT;
    COMMIT;
    SELECT CASE WHEN @Deleted > 0 THEN 'Success' ELSE 'Error: That price isn''t one of this portfolio''s.' END AS Result;
END
GO

PRINT 'Manual prices migration done.';
GO
