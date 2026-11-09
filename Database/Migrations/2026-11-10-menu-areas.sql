-- =============================================
-- Migration: Menu shows only what a portfolio uses (10-Nov-2026). Safe to run more than once.
--   * tblUsers.strMenuAreas: the menu choices made on Portfolio settings, as text such as
--     'Business=on;Property=off'. NULL or a missing area means automatic. A login's own row also keeps its
--     'Practice' choice (the practice view belongs to the login, like its practice details).
--   * uspGetMenuUsage: what the menu needs to decide what to show (does the portfolio have business records or
--     rental properties, its entity type and choices, and the login's practice details and choice).
--   * uspSaveMenuAreas: saves a portfolio's choices (full access needed) or the login's own practice choice.
-- The site shows every menu until this has been run.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('dbo.tblUsers', 'strMenuAreas') IS NULL
    ALTER TABLE dbo.tblUsers ADD strMenuAreas VARCHAR(200) NULL;
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetMenuUsage]
(
    @iUserId INT,
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        10-Nov-2026
-- Description: What the top menu needs for a login looking at a portfolio. Read only.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT
        CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.tblInvoices WHERE iUserID = @iPortfolioId)
                    OR EXISTS (SELECT 1 FROM dbo.tblBills WHERE iUserID = @iPortfolioId)
                    OR EXISTS (SELECT 1 FROM dbo.tblQuotesOrders WHERE iUserID = @iPortfolioId)
                    OR EXISTS (SELECT 1 FROM dbo.tblInventoryItems WHERE iUserID = @iPortfolioId)
                    OR EXISTS (SELECT 1 FROM dbo.tblFixedAssets WHERE iUserID = @iPortfolioId)
                    OR EXISTS (SELECT 1 FROM dbo.tblExpenseClaims WHERE iUserID = @iPortfolioId)
                  THEN 1 ELSE 0 END AS BIT) AS HasBusiness,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.tblRentalProperties WHERE iUserID = @iPortfolioId)
                  THEN 1 ELSE 0 END AS BIT) AS HasProperty,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.tblPracticeDetails WHERE iUserID = @iUserId)
                  THEN 1 ELSE 0 END AS BIT) AS HasPracticeDetails,
        (SELECT strEntityType FROM dbo.tblUsers WHERE iUserID = @iPortfolioId) AS EntityType,
        (SELECT strMenuAreas FROM dbo.tblUsers WHERE iUserID = @iPortfolioId) AS PortfolioChoices,
        (SELECT strMenuAreas FROM dbo.tblUsers WHERE iUserID = @iUserId) AS LoginChoices;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveMenuAreas]
(
    @iUserId INT,
    @iPortfolioId INT,
    @strMenuAreas VARCHAR(200)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        10-Nov-2026
-- Description: Saves the menu choices kept on a tblUsers row. A login may always save its own row;
--              anyone else's portfolio needs full access. Returns Result ('Settings Saved' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF @iPortfolioId <> @iUserId AND dbo.fnCanManagePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: You need full access to a portfolio to change its settings.' AS Result;
        RETURN;
    END

    UPDATE dbo.tblUsers
    SET strMenuAreas = NULLIF(@strMenuAreas, '')
    WHERE iUserID = @iPortfolioId;

    SELECT 'Settings Saved' AS Result;
END
GO
