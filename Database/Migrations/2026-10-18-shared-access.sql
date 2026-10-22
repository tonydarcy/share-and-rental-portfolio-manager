-- =============================================
-- Migration: shared access roles and invitations
-- Date:      18-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Owner / Editor / Read only sharing. Run it AFTER 2026-10-15-groups-and-sharing.sql.
-- Safe to run more than once. Existing shares keep what they could do: 'Admin' rows are
-- owners and 'View' rows are read only.
--
-- What it adds:
--   * A third access level, 'Edit' (an editor): view and change everything, but not
--     change who the portfolio is shared with. fnCanManagePortfolio already treats it
--     as full access for changes, so every proc that checks it lets editors save.
--   * fnCanSharePortfolio: only the portfolio's own login and owners ('Admin') may share.
--   * tblPortfolioInvites (new): sharing with an email nobody has signed up with yet
--     saves an invitation; it becomes access when that person signs up and logs in.
--   * uspGetAccessiblePortfolios, uspGetPortfolioShares, uspSharePortfolio,
--     uspRemovePortfolioShare (changed); uspGetPortfolioInvites, uspCancelPortfolioInvite,
--     uspAcceptPortfolioInvites (new).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.fnCanManagePortfolio', 'FN') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-15-groups-and-sharing.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblPortfolioInvites', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblPortfolioInvites (
        iInviteID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblPortfolioInvites PRIMARY KEY,
        iPortfolioID       INT NOT NULL,                  -- the tblUsers row being shared
        strEmail           VARCHAR(500) NOT NULL,
        strAccess          VARCHAR(20) NOT NULL,          -- Admin, Edit or View
        iInvitedByUserID   INT NOT NULL,
        dtmCreatedDate     DATETIME NOT NULL CONSTRAINT DF_tblPortfolioInvites_Created DEFAULT (GETDATE())
    );
    CREATE UNIQUE INDEX UX_tblPortfolioInvites_PortfolioEmail ON dbo.tblPortfolioInvites (iPortfolioID, strEmail);
    CREATE INDEX IX_tblPortfolioInvites_Email ON dbo.tblPortfolioInvites (strEmail);
END
GO

-- 2 = owner (Admin, or any text the old site wrote), 1 = editor, 0 = read only
CREATE OR ALTER FUNCTION [dbo].[fnAccessLevel] (@strAccess VARCHAR(500))
RETURNS INT
AS
BEGIN
    DECLARE @s VARCHAR(500) = LTRIM(RTRIM(ISNULL(@strAccess, '')));
    IF @s IN ('View', 'View only', 'Read', 'Read only', 'ReadOnly', 'Read-only')
        RETURN 0;
    IF @s IN ('Edit', 'Editor')
        RETURN 1;
    RETURN 2;
END
GO

-- Changing who can open a portfolio: its own login row, or an owner grant (direct or all-portfolios)
CREATE OR ALTER FUNCTION [dbo].[fnCanSharePortfolio] (@iUserId INT, @iPortfolioId INT)
RETURNS BIT
AS
BEGIN
    IF @iUserId = @iPortfolioId
        RETURN 1;
    IF EXISTS (SELECT 1 FROM tblPortfolioPermissions
               WHERE iUserID = @iUserId AND iUserIDForPortfolioToAccess IN (@iPortfolioId, 0)
                 AND dbo.fnAccessLevel(strAccessDescription) = 2)
        RETURN 1;
    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetAccessiblePortfolios]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: The portfolios a login may open, one row each: PortfolioId,
--              PortfolioName, EntityType, Access ('Admin', 'Edit' or 'View').
--              The highest access wins when more than one row applies.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    DECLARE @Grants TABLE (PortfolioId INT, Level INT);

    -- Every portfolio, for a login with the all-portfolios row
    INSERT INTO @Grants (PortfolioId, Level)
    SELECT U.iUserId, dbo.fnAccessLevel(P.strAccessDescription)
    FROM tblPortfolioPermissions P
    CROSS JOIN tblUsers U
    WHERE P.iUserID = @iUserId AND P.iUserIDForPortfolioToAccess = 0;

    INSERT INTO @Grants (PortfolioId, Level)
    SELECT P.iUserIDForPortfolioToAccess, dbo.fnAccessLevel(P.strAccessDescription)
    FROM tblPortfolioPermissions P
    WHERE P.iUserID = @iUserId AND P.iUserIDForPortfolioToAccess > 0;

    INSERT INTO @Grants (PortfolioId, Level) VALUES (@iUserId, 2);

    SELECT U.iUserId AS PortfolioId,
           CASE WHEN EXISTS (SELECT 1 FROM tblPortfolioPermissions A WHERE A.iUserID = @iUserId AND A.iUserIDForPortfolioToAccess = 0)
                     AND U.iUserId <> @iUserId
                THEN U.strContactName + ' - ' + U.strEntityName
                ELSE U.strEntityName END AS PortfolioName,
           U.strEntityType AS EntityType,
           CASE MAX(G.Level) WHEN 0 THEN 'View' WHEN 1 THEN 'Edit' ELSE 'Admin' END AS Access
    FROM @Grants G
    JOIN tblUsers U ON U.iUserId = G.PortfolioId
    GROUP BY U.iUserId, U.strContactName, U.strEntityName, U.strEntityType
    ORDER BY CASE WHEN U.iUserId = @iUserId THEN 0 ELSE 1 END, PortfolioName;
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
-- Date:        18-Oct-2026
-- Description: Who has been given access to a portfolio. Only returns rows
--              when @iUserId may share it (its login or an owner). Columns:
--              iPortfolioPermissionsID, iUserID, LoginEmail, ContactName,
--              Access ('Admin', 'Edit' or 'View').
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT P.iPortfolioPermissionsID, P.iUserID,
           U.strLoginEmail AS LoginEmail, U.strContactName AS ContactName,
           CASE dbo.fnAccessLevel(P.strAccessDescription) WHEN 0 THEN 'View' WHEN 1 THEN 'Edit' ELSE 'Admin' END AS Access
    FROM tblPortfolioPermissions P
    JOIN tblUsers U ON U.iUserId = P.iUserID
    WHERE P.iUserIDForPortfolioToAccess = @iPortfolioId
      AND dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 1
    ORDER BY U.strContactName, U.strLoginEmail;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetPortfolioInvites]
(
    @iUserId INT,
    @iPortfolioId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Invitations to a portfolio not yet taken up (the person hasn't
--              signed up). Only for its login or an owner. Columns: iInviteID,
--              strEmail, strAccess, dtmCreatedDate.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SELECT iInviteID, strEmail, strAccess, dtmCreatedDate
    FROM tblPortfolioInvites
    WHERE iPortfolioID = @iPortfolioId
      AND dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 1
    ORDER BY strEmail;
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
-- Date:        18-Oct-2026
-- Description: Gives the login registered with @strLoginEmail 'Admin' (owner),
--              'Edit' (editor) or 'View' (read only) access to a portfolio, or
--              changes the access it has. When nobody has signed up with the
--              email, saves an invitation instead. @iUserId must be the
--              portfolio's login or an owner. Returns Result ('Portfolio Shared',
--              'Invitation Saved' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @strLoginEmail = LTRIM(RTRIM(ISNULL(@strLoginEmail, '')));

    IF @strAccess NOT IN ('Admin', 'Edit', 'View')
    BEGIN
        SELECT 'Error: Choose owner, editor or read only access.' AS Result;
        RETURN;
    END

    IF dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: Only an owner of this portfolio can share it.' AS Result;
        RETURN;
    END

    IF @strLoginEmail = '' OR CHARINDEX('@', @strLoginEmail) = 0
    BEGIN
        SELECT 'Error: Check the email address.' AS Result;
        RETURN;
    END

    -- The login is the oldest row with the email; later rows are that person's extra portfolios
    DECLARE @iShareWith INT = (SELECT TOP 1 iUserId FROM tblUsers
                               WHERE strLoginEmail = @strLoginEmail ORDER BY iUserId);
    DECLARE @strComment VARCHAR(200) = 'Shared by user ' + CAST(@iUserId AS VARCHAR(20)) + ' on ' + CONVERT(VARCHAR(10), GETDATE(), 120);

    BEGIN TRANSACTION;

    IF @iShareWith IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM tblPortfolioInvites WITH (UPDLOCK, HOLDLOCK)
                   WHERE iPortfolioID = @iPortfolioId AND strEmail = @strLoginEmail)
            UPDATE tblPortfolioInvites
            SET strAccess = @strAccess, iInvitedByUserID = @iUserId, dtmCreatedDate = GETDATE()
            WHERE iPortfolioID = @iPortfolioId AND strEmail = @strLoginEmail;
        ELSE
            INSERT INTO tblPortfolioInvites (iPortfolioID, strEmail, strAccess, iInvitedByUserID)
            VALUES (@iPortfolioId, @strLoginEmail, @strAccess, @iUserId);

        COMMIT TRANSACTION;
        SELECT 'Invitation Saved' AS Result;
        RETURN;
    END

    IF @iShareWith = @iUserId
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT 'Error: That is your own login.' AS Result;
        RETURN;
    END

    IF @iShareWith = @iPortfolioId
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT 'Error: That person''s login is this portfolio, so they already have full access.' AS Result;
        RETURN;
    END

    IF EXISTS (SELECT 1 FROM tblPortfolioPermissions WITH (UPDLOCK, HOLDLOCK)
               WHERE iUserID = @iShareWith AND iUserIDForPortfolioToAccess = @iPortfolioId)
        UPDATE tblPortfolioPermissions
        SET strAccessDescription = @strAccess, strComment = @strComment
        WHERE iUserID = @iShareWith AND iUserIDForPortfolioToAccess = @iPortfolioId;
    ELSE
        INSERT INTO tblPortfolioPermissions (iUserID, iUserIDForPortfolioToAccess, strAccessDescription, strComment)
        VALUES (@iShareWith, @iPortfolioId, @strAccess, @strComment);

    DELETE FROM tblPortfolioInvites WHERE iPortfolioID = @iPortfolioId AND strEmail = @strLoginEmail;

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
-- Date:        18-Oct-2026
-- Description: Takes away someone's access to a portfolio. @iUserId must be
--              its login or an owner, and can't remove their own (so a
--              portfolio is never left with nobody to manage it). Returns
--              Result ('Access Removed' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: Only an owner of this portfolio can change who it is shared with.' AS Result;
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
        SELECT 'Error: You can''t remove your own access. Ask another owner to do it.' AS Result;
        RETURN;
    END

    DELETE FROM tblPortfolioPermissions
    WHERE iPortfolioPermissionsID = @iPortfolioPermissionsID AND iUserIDForPortfolioToAccess = @iPortfolioId;

    SELECT 'Access Removed' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspCancelPortfolioInvite]
(
    @iUserId INT,
    @iPortfolioId INT,
    @iInviteID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Withdraws an invitation to a portfolio. Returns Result
--              ('Invitation Cancelled' or 'Error: ...').
-- =============================================
BEGIN
    SET NOCOUNT ON;

    IF dbo.fnCanSharePortfolio(@iUserId, @iPortfolioId) = 0
    BEGIN
        SELECT 'Error: Only an owner of this portfolio can change who it is shared with.' AS Result;
        RETURN;
    END

    DELETE FROM tblPortfolioInvites WHERE iInviteID = @iInviteID AND iPortfolioID = @iPortfolioId;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That invitation was not found.' AS Result;
    ELSE
        SELECT 'Invitation Cancelled' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAcceptPortfolioInvites]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        18-Oct-2026
-- Description: Turns the invitations for this login's email into access, then
--              deletes them. Called when someone logs in. Returns one row:
--              Accepted (how many portfolios were added or changed).
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @strEmail VARCHAR(500) = (SELECT LTRIM(RTRIM(strLoginEmail)) FROM tblUsers WHERE iUserId = @iUserId);

    -- Only the login (the oldest row with the email), not that person's extra portfolios
    IF @strEmail IS NULL OR @strEmail = ''
       OR NOT EXISTS (SELECT 1 FROM tblPortfolioInvites WHERE strEmail = @strEmail)
       OR @iUserId <> (SELECT MIN(iUserId) FROM tblUsers WHERE strLoginEmail = @strEmail)
    BEGIN
        SELECT 0 AS Accepted;
        RETURN;
    END

    DECLARE @Accepted INT = 0;

    BEGIN TRANSACTION;

    UPDATE P
    SET strAccessDescription = I.strAccess,
        strComment = 'Invited by user ' + CAST(I.iInvitedByUserID AS VARCHAR(20)) + ' on ' + CONVERT(VARCHAR(10), I.dtmCreatedDate, 120)
    FROM tblPortfolioPermissions P
    JOIN tblPortfolioInvites I ON I.iPortfolioID = P.iUserIDForPortfolioToAccess
    WHERE P.iUserID = @iUserId AND I.strEmail = @strEmail;
    SET @Accepted += @@ROWCOUNT;

    INSERT INTO tblPortfolioPermissions (iUserID, iUserIDForPortfolioToAccess, strAccessDescription, strComment)
    SELECT @iUserId, I.iPortfolioID, I.strAccess,
           'Invited by user ' + CAST(I.iInvitedByUserID AS VARCHAR(20)) + ' on ' + CONVERT(VARCHAR(10), I.dtmCreatedDate, 120)
    FROM tblPortfolioInvites I
    WHERE I.strEmail = @strEmail AND I.iPortfolioID <> @iUserId
      AND EXISTS (SELECT 1 FROM tblUsers U WHERE U.iUserId = I.iPortfolioID)
      AND NOT EXISTS (SELECT 1 FROM tblPortfolioPermissions P
                      WHERE P.iUserID = @iUserId AND P.iUserIDForPortfolioToAccess = I.iPortfolioID);
    SET @Accepted += @@ROWCOUNT;

    DELETE FROM tblPortfolioInvites WHERE strEmail = @strEmail;

    COMMIT TRANSACTION;

    SELECT @Accepted AS Accepted;
END
GO
