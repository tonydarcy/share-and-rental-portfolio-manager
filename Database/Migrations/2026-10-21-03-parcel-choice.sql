-- =============================================
-- Migration: choice of parcels on sale
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Share trades > Parcels (on a sale). Run it after 2026-10-15-lock-periods.sql (it uses fnLockedTo).
-- Safe to run more than once. It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblSaleParcelChoice: how a sale's parcels were chosen (Specific, MinGain or MaxGain). A sale with
--     no row is first in, first out, as before.
--   * tblSaleParcelAllocation: the units each chosen sale took from each purchase (buy trade).
--   * uspGetSaleParcelChoices, uspSaveSaleParcelChoice, uspDeleteSaleParcelChoice.
-- The site's CGT figures, parcels sold, open parcels and the realised gain journals read these rows.
-- The older reports built in SQL (GetCapitalGainsTaxSummary, GetUnrealizedCapitalGains) stay first in, first out.
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

IF OBJECT_ID('dbo.tblSaleParcelChoice', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSaleParcelChoice (
        iUserID        INT NOT NULL,                       -- the portfolio
        iSellTradeID   INT NOT NULL,
        strMethod      VARCHAR(20) NOT NULL,               -- Specific, MinGain or MaxGain
        dtmSavedDate   DATETIME NOT NULL CONSTRAINT DF_tblSaleParcelChoice_Saved DEFAULT (GETDATE()),
        CONSTRAINT PK_tblSaleParcelChoice PRIMARY KEY (iUserID, iSellTradeID)
    );
END
GO

IF OBJECT_ID('dbo.tblSaleParcelAllocation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSaleParcelAllocation (
        iUserID        INT NOT NULL,
        iSellTradeID   INT NOT NULL,
        iBuyTradeID    INT NOT NULL,
        dblUnits       FLOAT NOT NULL,
        CONSTRAINT PK_tblSaleParcelAllocation PRIMARY KEY (iUserID, iSellTradeID, iBuyTradeID)
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetSaleParcelChoices
    @iUserID INT
AS
BEGIN
    SET NOCOUNT ON;
    -- Only sales and purchases that still exist in this portfolio
    SELECT C.iSellTradeID AS SellTradeId, C.strMethod AS Method, A.iBuyTradeID AS BuyTradeId, A.dblUnits AS Units
    FROM dbo.tblSaleParcelChoice C
    JOIN dbo.tblShareTrades S ON S.iShareTradeId = C.iSellTradeID AND S.iUserId = C.iUserID
    LEFT JOIN dbo.tblSaleParcelAllocation A ON A.iUserID = C.iUserID AND A.iSellTradeID = C.iSellTradeID
         AND EXISTS (SELECT 1 FROM dbo.tblShareTrades B WHERE B.iShareTradeId = A.iBuyTradeID AND B.iUserId = C.iUserID)
    WHERE C.iUserID = @iUserID
    ORDER BY C.iSellTradeID, A.iBuyTradeID;
END
GO

-- =============================================
-- Saves a sale's parcel choice: @xmlPicks is <picks><p b="buy trade id" u="units"/>...</picks>.
-- Checks the sale and purchases are this portfolio's, of the same stock, bought no later than the sale,
-- and that the sale isn't in a locked period. Returns Result: 'Success' or 'Error: ...'.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveSaleParcelChoice
    @iUserID      INT,
    @iSellTradeID INT,
    @strMethod    VARCHAR(20),
    @xmlPicks     XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Picks TABLE (iBuyTradeID INT, dblUnits FLOAT);
    INSERT INTO @Picks (iBuyTradeID, dblUnits)
    SELECT X.p.value('@b', 'INT'), X.p.value('@u', 'FLOAT') FROM @xmlPicks.nodes('/picks/p') AS X(p);

    DECLARE @Market VARCHAR(50), @Code VARCHAR(50), @Date DATE, @Qty FLOAT;
    SELECT @Market = strStockExchangeCode, @Code = strStockCode, @Date = dtmDateOfTransaction, @Qty = ABS(iQuantity)
    FROM dbo.tblShareTrades WHERE iShareTradeId = @iSellTradeID AND iUserId = @iUserID AND UPPER(strType) = 'SELL';

    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    DECLARE @Error VARCHAR(300) = CASE
        WHEN @Market IS NULL THEN 'That sale isn''t in this portfolio.'
        WHEN @Locked IS NOT NULL AND @Date <= @Locked THEN 'The sale is in a locked period, so its parcels can''t be changed.'
        WHEN @strMethod NOT IN ('Specific', 'MinGain', 'MaxGain') THEN 'Choose how the parcels were picked.'
        WHEN NOT EXISTS (SELECT 1 FROM @Picks) THEN 'Pick at least one parcel.'
        WHEN EXISTS (SELECT 1 FROM @Picks WHERE dblUnits IS NULL OR dblUnits <= 0) THEN 'Units picked must be more than zero.'
        WHEN EXISTS (SELECT iBuyTradeID FROM @Picks GROUP BY iBuyTradeID HAVING COUNT(*) > 1) THEN 'Each parcel can be picked once.'
        WHEN EXISTS (SELECT 1 FROM @Picks P WHERE NOT EXISTS (
                         SELECT 1 FROM dbo.tblShareTrades B
                         WHERE B.iShareTradeId = P.iBuyTradeID AND B.iUserId = @iUserID AND UPPER(B.strType) = 'BUY'
                           AND B.strStockExchangeCode = @Market AND B.strStockCode = @Code AND B.dtmDateOfTransaction <= @Date))
            THEN 'A parcel picked isn''t a purchase of this stock before the sale.'
        WHEN ABS((SELECT SUM(dblUnits) FROM @Picks) - @Qty) > 0.0001 THEN 'The units picked must add up to the units sold.'
    END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE dbo.tblSaleParcelAllocation WHERE iUserID = @iUserID AND iSellTradeID = @iSellTradeID;
    DELETE dbo.tblSaleParcelChoice WHERE iUserID = @iUserID AND iSellTradeID = @iSellTradeID;
    INSERT dbo.tblSaleParcelChoice (iUserID, iSellTradeID, strMethod) VALUES (@iUserID, @iSellTradeID, @strMethod);
    INSERT dbo.tblSaleParcelAllocation (iUserID, iSellTradeID, iBuyTradeID, dblUnits)
    SELECT @iUserID, @iSellTradeID, iBuyTradeID, dblUnits FROM @Picks;
    COMMIT;

    SELECT 'Success' AS Result;
END
GO

-- Puts a sale back to first in, first out. Returns Result: 'Success' or 'Error: ...'.
CREATE OR ALTER PROCEDURE dbo.uspDeleteSaleParcelChoice
    @iUserID      INT,
    @iSellTradeID INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Date DATE = (SELECT dtmDateOfTransaction FROM dbo.tblShareTrades WHERE iShareTradeId = @iSellTradeID AND iUserId = @iUserID);
    DECLARE @Locked DATE = dbo.fnLockedTo(@iUserID);
    IF @Locked IS NOT NULL AND @Date <= @Locked
    BEGIN
        SELECT 'Error: The sale is in a locked period, so its parcels can''t be changed.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    DELETE dbo.tblSaleParcelAllocation WHERE iUserID = @iUserID AND iSellTradeID = @iSellTradeID;
    DELETE dbo.tblSaleParcelChoice WHERE iUserID = @iUserID AND iSellTradeID = @iSellTradeID;
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Parcel choice migration done.';
GO
