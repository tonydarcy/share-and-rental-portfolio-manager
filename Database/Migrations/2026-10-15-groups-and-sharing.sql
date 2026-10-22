-- =============================================
-- Migration: custom groups and portfolio sharing
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build
-- with the Groups and Sharing pages. The new site's portfolio picker reads
-- uspGetAccessiblePortfolios, so that build won't load portfolios until this
-- has been run.
-- The old site keeps working: uspCreateCustomGroup now also returns a result
-- row; everything else is new. Safe to run more than once.
--
-- What it adds:
--   * uspCreateCustomGroup (changed), uspRenameCustomGroup, uspDeleteCustomGroup.
--     Trades are added to and removed from groups with the existing
--     uspAddTradeToGroup and uspRemoveTradeFromGroup.
--   * uspGetAccessiblePortfolios: the portfolios a login may open with its
--     access level (as GetPortfoliosUserHasPermissionToAccess, plus Access).
--   * uspGetPortfolioShares, uspSharePortfolio, uspRemovePortfolioShare:
--     who can open a portfolio, and giving or taking away access by email.
--     Access is 'Admin' (view, change and share) or 'View' (look only).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- 1. Custom groups
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspCreateCustomGroup]
(
    @iUserId INT,
    @strGroupName VARCHAR(100)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Creates a named group for a portfolio. Returns one row:
--              Result ('Group Created' or 'Error: ...') and iGroupID.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strGroupName = LTRIM(RTRIM(@strGroupName));

    IF ISNULL(@strGroupName, '') = ''
    BEGIN
        SELECT 'Error: Enter a name for the group.' AS Result, CAST(NULL AS INT) AS iGroupID;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblCustomGroups WHERE iUserID = @iUserId AND strGroupName = @strGroupName)
    BEGIN
        SELECT 'Error: There is already a group with that name.' AS Result, CAST(NULL AS INT) AS iGroupID;
        RETURN;
    END

    INSERT INTO tblCustomGroups (iUserID, strGroupName) VALUES (@iUserId, @strGroupName);
    SELECT 'Group Created' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS iGroupID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRenameCustomGroup]
(
    @iUserId INT,
    @iGroupID INT,
    @strGroupName VARCHAR(100)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Renames one of a portfolio's groups. Returns Result
--              ('Group Renamed' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strGroupName = LTRIM(RTRIM(@strGroupName));

    IF NOT EXISTS (SELECT 1 FROM tblCustomGroups WHERE iGroupID = @iGroupID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That group was not found.' AS Result;
        RETURN;
    END

    IF ISNULL(@strGroupName, '') = ''
    BEGIN
        SELECT 'Error: Enter a name for the group.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblCustomGroups WHERE iUserID = @iUserId AND strGroupName = @strGroupName AND iGroupID <> @iGroupID)
    BEGIN
        SELECT 'Error: There is already a group with that name.' AS Result;
        RETURN;
    END

    UPDATE tblCustomGroups SET strGroupName = @strGroupName WHERE iGroupID = @iGroupID AND iUserID = @iUserId;
    SELECT 'Group Renamed' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteCustomGroup]
(
    @iUserId INT,
    @iGroupID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes one of a portfolio's groups and its links to trades
--              (the trades themselves are not touched). Returns Result
--              ('Group Deleted' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM tblCustomGroups WHERE iGroupID = @iGroupID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That group was not found.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE FROM tblShareTradeGroups WHERE iGroupID = @iGroupID;
    DELETE FROM tblCustomGroups WHERE iGroupID = @iGroupID AND iUserID = @iUserId;
    COMMIT TRANSACTION;

    SELECT 'Group Deleted' AS Result;
END
GO

-- =============================================
-- 2. Portfolio access and sharing
-- tblPortfolioPermissions: iUserID is the login, iUserIDForPortfolioToAccess
-- the portfolio (a tblUsers row). A row with portfolio 0 opens every portfolio.
-- A login always opens its own row. Any access text other than View/Read is
-- treated as full access, as the old site did.
-- =============================================
CREATE OR ALTER PROCEDURE [dbo].[uspGetAccessiblePortfolios]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The portfolios a login may open, one row each: PortfolioId,
--              PortfolioName, EntityType, Access ('Admin' or 'View').
--              Full access wins when more than one row applies.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @Grants TABLE (PortfolioId INT, IsView BIT);

    -- Every portfolio, for a login with the all-portfolios row
    INSERT INTO @Grants (PortfolioId, IsView)
    SELECT U.iUserId,
           CASE WHEN LTRIM(RTRIM(P.strAccessDescription)) IN ('View', 'View only', 'Read', 'Read only', 'ReadOnly', 'Read-only') THEN 1 ELSE 0 END
    FROM tblPortfolioPermissions P
    CROSS JOIN tblUsers U
    WHERE P.iUserID = @iUserId AND P.iUserIDForPortfolioToAccess = 0;

    INSERT INTO @Grants (PortfolioId, IsView)
    SELECT P.iUserIDForPortfolioToAccess,
           CASE WHEN LTRIM(RTRIM(P.strAccessDescription)) IN ('View', 'View only', 'Read', 'Read only', 'ReadOnly', 'Read-only') THEN 1 ELSE 0 END
    FROM tblPortfolioPermissions P
    WHERE P.iUserID = @iUserId AND P.iUserIDForPortfolioToAccess > 0;

    INSERT INTO @Grants (PortfolioId, IsView) VALUES (@iUserId, 0);

    SELECT U.iUserId AS PortfolioId,
           CASE WHEN EXISTS (SELECT 1 FROM tblPortfolioPermissions A WHERE A.iUserID = @iUserId AND A.iUserIDForPortfolioToAccess = 0)
                     AND U.iUserId <> @iUserId
                THEN U.strContactName + ' - ' + U.strEntityName
                ELSE U.strEntityName END AS PortfolioName,
           U.strEntityType AS EntityType,
           CASE WHEN MIN(CAST(G.IsView AS INT)) = 1 THEN 'View' ELSE 'Admin' END AS Access
    FROM @Grants G
    JOIN tblUsers U ON U.iUserId = G.PortfolioId
    GROUP BY U.iUserId, U.strContactName, U.strEntityName, U.strEntityType
    ORDER BY CASE WHEN U.iUserId = @iUserId THEN 0 ELSE 1 END, PortfolioName;
END
GO

-- Full access to a portfolio: own login row, or a non-view grant (direct or all-portfolios)
CREATE OR ALTER FUNCTION [dbo].[fnCanManagePortfolio] (@iUserId INT, @iPortfolioId INT)
RETURNS BIT
AS
BEGIN
    IF @iUserId = @iPortfolioId
        RETURN 1;
    IF EXISTS (SELECT 1 FROM tblPortfolioPermissions
               WHERE iUserID = @iUserId AND iUserIDForPortfolioToAccess IN (@iPortfolioId, 0)
                 AND LTRIM(RTRIM(ISNULL(strAccessDescription, ''))) NOT IN ('View', 'View only', 'Read', 'Read only', 'ReadOnly', 'Read-only'))
        RETURN 1;
    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetPortfolioShares]
(
    @iUserId INT,
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Who has been given access to a portfolio. Only returns rows
--              when @iUserId has full access to it. Columns:
--              iPortfolioPermissionsID, iUserID, LoginEmail, ContactName, Access.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF dbo.fnCanManagePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT CAST(NULL AS INT) AS iPortfolioPermissionsID, CAST(NULL AS INT) AS iUserID,
               CAST(NULL AS VARCHAR(500)) AS LoginEmail, CAST(NULL AS VARCHAR(500)) AS ContactName,
               CAST(NULL AS VARCHAR(500)) AS Access
        WHERE 1 = 0;
        RETURN;
    END

    SELECT P.iPortfolioPermissionsID, P.iUserID,
           U.strLoginEmail AS LoginEmail, U.strContactName AS ContactName,
           CASE WHEN LTRIM(RTRIM(ISNULL(P.strAccessDescription, ''))) IN ('View', 'View only', 'Read', 'Read only', 'ReadOnly', 'Read-only')
                THEN 'View' ELSE 'Admin' END AS Access
    FROM tblPortfolioPermissions P
    JOIN tblUsers U ON U.iUserId = P.iUserID
    WHERE P.iUserIDForPortfolioToAccess = @iPortfolioId
    ORDER BY U.strContactName, U.strLoginEmail;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSharePortfolio]
(
    @iUserId INT,
    @iPortfolioId INT,
    @strLoginEmail VARCHAR(500),
    @strAccess VARCHAR(20)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Gives the login registered with @strLoginEmail 'Admin' or
--              'View' access to a portfolio, or changes the access it has.
--              @iUserId needs full access to the portfolio. Returns Result
--              ('Portfolio Shared' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @strAccess NOT IN ('Admin', 'View')
    BEGIN
        SELECT 'Error: Choose View or Admin access.' AS Result;
        RETURN;
    END

    IF dbo.fnCanManagePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: You need full access to a portfolio to share it.' AS Result;
        RETURN;
    END

    -- The login is the oldest row with the email; later rows are that person's extra portfolios
    DECLARE @iShareWith INT = (SELECT TOP 1 iUserId FROM tblUsers
                               WHERE strLoginEmail = LTRIM(RTRIM(@strLoginEmail)) ORDER BY iUserId);

    IF @iShareWith IS NULL
    BEGIN
        SELECT 'Error: Nobody has signed up with that email address. Ask them to sign up first.' AS Result;
        RETURN;
    END

    IF @iShareWith = @iUserId
    BEGIN
        SELECT 'Error: That is your own login.' AS Result;
        RETURN;
    END

    IF @iShareWith = @iPortfolioId
    BEGIN
        SELECT 'Error: That person''s login is this portfolio, so they already have full access.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM tblPortfolioPermissions WITH (UPDLOCK, HOLDLOCK)
               WHERE iUserID = @iShareWith AND iUserIDForPortfolioToAccess = @iPortfolioId)
        UPDATE tblPortfolioPermissions
        SET strAccessDescription = @strAccess,
            strComment = 'Shared by user ' + CAST(@iUserId AS VARCHAR(20)) + ' on ' + CONVERT(VARCHAR(10), GETDATE(), 120)
        WHERE iUserID = @iShareWith AND iUserIDForPortfolioToAccess = @iPortfolioId;
    ELSE
        INSERT INTO tblPortfolioPermissions (iUserID, iUserIDForPortfolioToAccess, strAccessDescription, strComment)
        VALUES (@iShareWith, @iPortfolioId, @strAccess,
                'Shared by user ' + CAST(@iUserId AS VARCHAR(20)) + ' on ' + CONVERT(VARCHAR(10), GETDATE(), 120));

    COMMIT TRANSACTION;

    SELECT 'Portfolio Shared' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspRemovePortfolioShare]
(
    @iUserId INT,
    @iPortfolioId INT,
    @iPortfolioPermissionsID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Takes away someone's access to a portfolio. @iUserId needs
--              full access, and can't remove their own (so a portfolio is
--              never left with nobody to manage it). Returns Result
--              ('Access Removed' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF dbo.fnCanManagePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: You need full access to a portfolio to change who it is shared with.' AS Result;
        RETURN;
    END

    DECLARE @iFor INT = (SELECT iUserID FROM tblPortfolioPermissions
                         WHERE iPortfolioPermissionsID = @iPortfolioPermissionsID
                           AND iUserIDForPortfolioToAccess = @iPortfolioId);

    IF @iFor IS NULL
    BEGIN
        SELECT 'Error: That access was not found.' AS Result;
        RETURN;
    END

    IF @iFor = @iUserId
    BEGIN
        SELECT 'Error: You can''t remove your own access. Ask another person with full access to do it.' AS Result;
        RETURN;
    END

    DELETE FROM tblPortfolioPermissions
    WHERE iPortfolioPermissionsID = @iPortfolioPermissionsID AND iUserIDForPortfolioToAccess = @iPortfolioId;

    SELECT 'Access Removed' AS Result;
END
GO
