-- =============================================
-- Migration: family groups (consolidated view)
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with
-- Reports > Consolidated. Safe to run more than once. It only adds things; nothing existing changes.
--
-- What it adds:
--   * tblFamilyGroups: a named group of portfolios (for example an individual, their family trust, company
--     and SMSF), kept by the login that made it.
--   * tblFamilyGroupMembers: the portfolios in each group.
--   * uspGetFamilyGroups, uspSaveFamilyGroup, uspDeleteFamilyGroup.
-- The site only shows a group's portfolios the login can still open.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblFamilyGroups', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFamilyGroups (
        iFamilyGroupID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFamilyGroups PRIMARY KEY,
        iLoginID       INT NOT NULL,
        strName        NVARCHAR(100) NOT NULL,
        dtmCreated     DATETIME NOT NULL CONSTRAINT DF_tblFamilyGroups_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblFamilyGroups_Login ON dbo.tblFamilyGroups (iLoginID);
END
GO

IF OBJECT_ID('dbo.tblFamilyGroupMembers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFamilyGroupMembers (
        iFamilyGroupID INT NOT NULL,
        iPortfolioID   INT NOT NULL,
        CONSTRAINT PK_tblFamilyGroupMembers PRIMARY KEY (iFamilyGroupID, iPortfolioID)
    );
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetFamilyGroups
    @iLoginID INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT G.iFamilyGroupID AS GroupId, G.strName AS Name, M.iPortfolioID AS PortfolioId
    FROM dbo.tblFamilyGroups G
    LEFT JOIN dbo.tblFamilyGroupMembers M ON M.iFamilyGroupID = G.iFamilyGroupID
    WHERE G.iLoginID = @iLoginID
    ORDER BY G.strName, G.iFamilyGroupID;
END
GO

-- =============================================
-- Adds a group (@iFamilyGroupID 0) or renames one and replaces its portfolios.
-- @xmlPortfolios is <ps><p id="portfolio id"/>...</ps>. The site checks the login can open each one.
-- Returns Result ('Success' or 'Error: ...') and the group's Id.
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveFamilyGroup
    @iLoginID       INT,
    @iFamilyGroupID INT,
    @strName        NVARCHAR(100),
    @xmlPortfolios  XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @strName = LTRIM(RTRIM(@strName));
    IF ISNULL(@strName, '') = ''
    BEGIN
        SELECT 'Error: Give the group a name.' AS Result, NULL AS Id;
        RETURN;
    END
    IF @iFamilyGroupID > 0 AND NOT EXISTS (SELECT 1 FROM dbo.tblFamilyGroups WHERE iFamilyGroupID = @iFamilyGroupID AND iLoginID = @iLoginID)
    BEGIN
        SELECT 'Error: That group isn''t yours.' AS Result, NULL AS Id;
        RETURN;
    END

    BEGIN TRANSACTION;
    IF @iFamilyGroupID > 0
        UPDATE dbo.tblFamilyGroups SET strName = @strName WHERE iFamilyGroupID = @iFamilyGroupID;
    ELSE
    BEGIN
        INSERT dbo.tblFamilyGroups (iLoginID, strName) VALUES (@iLoginID, @strName);
        SET @iFamilyGroupID = SCOPE_IDENTITY();
    END
    DELETE dbo.tblFamilyGroupMembers WHERE iFamilyGroupID = @iFamilyGroupID;
    INSERT dbo.tblFamilyGroupMembers (iFamilyGroupID, iPortfolioID)
    SELECT DISTINCT @iFamilyGroupID, X.p.value('@id', 'INT') FROM @xmlPortfolios.nodes('/ps/p') AS X(p)
    WHERE X.p.value('@id', 'INT') > 0;
    COMMIT;

    SELECT 'Success' AS Result, @iFamilyGroupID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteFamilyGroup
    @iLoginID       INT,
    @iFamilyGroupID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblFamilyGroups WHERE iFamilyGroupID = @iFamilyGroupID AND iLoginID = @iLoginID)
    BEGIN
        SELECT 'Error: That group isn''t yours.' AS Result;
        RETURN;
    END
    BEGIN TRANSACTION;
    DELETE dbo.tblFamilyGroupMembers WHERE iFamilyGroupID = @iFamilyGroupID;
    DELETE dbo.tblFamilyGroups WHERE iFamilyGroupID = @iFamilyGroupID;
    COMMIT;
    SELECT 'Success' AS Result;
END
GO

PRINT 'Family groups migration done.';
GO
