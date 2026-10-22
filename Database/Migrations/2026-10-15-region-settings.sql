-- =============================================
-- Migration: country, financial year start, currency and format per portfolio
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with the
-- Portfolio settings page. Run it AFTER 2026-10-15-groups-and-sharing.sql
-- (it uses fnCanManagePortfolio). Safe to run more than once.
-- Until it is run, every portfolio shows with Australian settings, as before.
--
-- What it adds:
--   * tblUsers.strCountryCode, iYearStartMonth, iYearStartDay, strNumberFormat.
--     All allow NULL, which means Australia (1 July, en-AU). The currency stays
--     in the existing strPortfolioCurrency column.
--   * uspGetPortfolioRegion: reads them.
--   * uspSavePortfolioRegion: saves them, for a login with full access.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF COL_LENGTH('dbo.tblUsers', 'strCountryCode') IS NULL
    ALTER TABLE dbo.tblUsers ADD strCountryCode VARCHAR(2) NULL;
GO
IF COL_LENGTH('dbo.tblUsers', 'iYearStartMonth') IS NULL
    ALTER TABLE dbo.tblUsers ADD iYearStartMonth INT NULL;
GO
IF COL_LENGTH('dbo.tblUsers', 'iYearStartDay') IS NULL
    ALTER TABLE dbo.tblUsers ADD iYearStartDay INT NULL;
GO
IF COL_LENGTH('dbo.tblUsers', 'strNumberFormat') IS NULL
    ALTER TABLE dbo.tblUsers ADD strNumberFormat VARCHAR(20) NULL;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetPortfolioRegion]
(
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: A portfolio's country, financial year start, currency and
--              date/number format. NULLs mean the Australian defaults.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT strCountryCode, iYearStartMonth, iYearStartDay, strPortfolioCurrency, strNumberFormat
    FROM tblUsers
    WHERE iUserId = @iPortfolioId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSavePortfolioRegion]
(
    @iUserId INT,
    @iPortfolioId INT,
    @strCountryCode VARCHAR(2),
    @iYearStartMonth INT,
    @iYearStartDay INT,
    @strPortfolioCurrency VARCHAR(3),
    @strNumberFormat VARCHAR(20)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Saves a portfolio's country, financial year start, currency
--              and date/number format. @iUserId needs full access to the
--              portfolio. Returns Result ('Settings Saved' or 'Error: ...').
--              Changing the currency doesn't convert amounts already entered.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF dbo.fnCanManagePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: You need full access to a portfolio to change its settings.' AS Result;
        RETURN;
    END

    IF @iYearStartMonth NOT BETWEEN 1 AND 12 OR @iYearStartDay NOT BETWEEN 1 AND 28
    BEGIN
        SELECT 'Error: Choose a financial year start month and a day from 1 to 28.' AS Result;
        RETURN;
    END

    IF LEN(ISNULL(@strPortfolioCurrency, '')) <> 3
    BEGIN
        SELECT 'Error: Choose a currency.' AS Result;
        RETURN;
    END

    UPDATE tblUsers
    SET strCountryCode = UPPER(@strCountryCode),
        iYearStartMonth = @iYearStartMonth,
        iYearStartDay = @iYearStartDay,
        strPortfolioCurrency = UPPER(@strPortfolioCurrency),
        strNumberFormat = @strNumberFormat
    WHERE iUserId = @iPortfolioId;

    SELECT 'Settings Saved' AS Result;
END
GO
