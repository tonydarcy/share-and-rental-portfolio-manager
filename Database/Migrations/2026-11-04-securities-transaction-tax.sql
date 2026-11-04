-- India: securities transaction tax (STT) recorded on a trade. The STT is part of the trade's brokerage (so the cash
-- and journals are unchanged); this table only says how much of it to leave out of the capital gains cost.
-- Safe to run more than once.
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblTradeSecuritiesTax', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTradeSecuritiesTax
    (
        iShareTradeId INT NOT NULL CONSTRAINT PK_tblTradeSecuritiesTax PRIMARY KEY,
        iUserId INT NOT NULL,
        dblAmount DECIMAL(18, 2) NOT NULL
    );
    CREATE INDEX IX_tblTradeSecuritiesTax_iUserId ON dbo.tblTradeSecuritiesTax (iUserId);
END
GO

-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        04-Nov-2026
-- Description: Records the STT on the portfolio's newest trade. Called straight after uspAddShareTrade (or
--              AddShareTradeWithCashTransaction) in the same transaction.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSetLatestTradeSecuritiesTax
(
    @iPortfolioId INT,
    @dblAmount DECIMAL(18, 2)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @iTradeId INT = (SELECT MAX(iShareTradeId) FROM dbo.tblShareTrades WHERE iUserId = @iPortfolioId);
    IF @iTradeId IS NULL
        RETURN;

    DELETE FROM dbo.tblTradeSecuritiesTax WHERE iShareTradeId = @iTradeId;
    IF @dblAmount > 0
        INSERT INTO dbo.tblTradeSecuritiesTax (iShareTradeId, iUserId, dblAmount) VALUES (@iTradeId, @iPortfolioId, @dblAmount);
END
GO

-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        04-Nov-2026
-- Description: The STT on the portfolio's trades that still exist (TradeId, Amount).
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspGetTradeSecuritiesTax
(
    @iPortfolioId INT
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT s.iShareTradeId AS TradeId, s.dblAmount AS Amount
    FROM dbo.tblTradeSecuritiesTax s
    JOIN dbo.tblShareTrades t ON t.iShareTradeId = s.iShareTradeId
    WHERE s.iUserId = @iPortfolioId;
END
GO
