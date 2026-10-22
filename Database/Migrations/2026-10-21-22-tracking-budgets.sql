-- =============================================
-- Migration: tracking categories and budgets
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Accounting > Tracking
-- categories and Budgets. Run it after 2026-10-21-21-ato-prefill.sql (the last of the roadmap 27 to 39 scripts).
-- Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblTrackingCategories (up to two per portfolio, e.g. Department and Property) and tblTrackingOptions (Sales,
--     Admin; 12 Smith St, ...).
--   * tblJournalLines gets iTrackingOption1ID and iTrackingOption2ID: a journal line's option in each category.
--   * tblBudgets (a budget per financial year, optionally for one tracking option) and tblBudgetLines (an amount per
--     account per month).
--   * uspSaveTrackingCategory, uspSaveTrackingOption, uspDeleteTrackingOption, uspSetJournalLineTracking,
--     uspSaveBudget, uspSaveBudgetLines, uspDeleteBudget.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblJournalLines', 'U') IS NULL OR COL_LENGTH('dbo.tblJournalLines', 'iTaxCodeID') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-17-sales-tax.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblTrackingCategories', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTrackingCategories (
        iCategoryID   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTrackingCategories PRIMARY KEY,
        iUserID       INT NOT NULL,                     -- the portfolio
        iPosition     INT NOT NULL,                     -- 1 or 2: which journal line column it uses
        strName       NVARCHAR(100) NOT NULL,
        bActive       BIT NOT NULL CONSTRAINT DF_tblTrackingCategories_Active DEFAULT (1)
    );
    CREATE UNIQUE INDEX UX_tblTrackingCategories_Position ON dbo.tblTrackingCategories (iUserID, iPosition);
END
GO

IF OBJECT_ID('dbo.tblTrackingOptions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTrackingOptions (
        iOptionID     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTrackingOptions PRIMARY KEY,
        iCategoryID   INT NOT NULL CONSTRAINT FK_tblTrackingOptions_Category REFERENCES dbo.tblTrackingCategories (iCategoryID),
        strName       NVARCHAR(100) NOT NULL,
        bActive       BIT NOT NULL CONSTRAINT DF_tblTrackingOptions_Active DEFAULT (1)
    );
    CREATE INDEX IX_tblTrackingOptions_Category ON dbo.tblTrackingOptions (iCategoryID);
END
GO

-- Plain nullable columns only: no filtered index on tblJournalLines (older procs were created with QUOTED_IDENTIFIER OFF)
IF COL_LENGTH('dbo.tblJournalLines', 'iTrackingOption1ID') IS NULL
    ALTER TABLE dbo.tblJournalLines ADD iTrackingOption1ID INT NULL, iTrackingOption2ID INT NULL;
GO

IF OBJECT_ID('dbo.tblBudgets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblBudgets (
        iBudgetID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblBudgets PRIMARY KEY,
        iUserID            INT NOT NULL,
        strName            NVARCHAR(100) NOT NULL,
        iYear              INT NOT NULL,                -- the financial year, by the year it ends
        iTrackingOptionID  INT NULL,                    -- a budget for one department, property, ...
        dtmUpdatedDate     DATETIME NOT NULL CONSTRAINT DF_tblBudgets_Updated DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblBudgets_User ON dbo.tblBudgets (iUserID, iYear);
END
GO

IF OBJECT_ID('dbo.tblBudgetLines', 'U') IS NULL
    CREATE TABLE dbo.tblBudgetLines (
        iBudgetID   INT NOT NULL CONSTRAINT FK_tblBudgetLines_Budget REFERENCES dbo.tblBudgets (iBudgetID) ON DELETE CASCADE,
        iAccountID  INT NOT NULL,
        iMonth      INT NOT NULL,                       -- 1 is the first month of the financial year, 12 the last
        dblAmount   DECIMAL(19,4) NOT NULL,             -- income and expenses both as positive amounts
        CONSTRAINT PK_tblBudgetLines PRIMARY KEY (iBudgetID, iAccountID, iMonth)
    );
GO

-- =============================================
-- Tracking
-- =============================================
-- Adds (@iCategoryID 0) or renames a category. A portfolio has at most two. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveTrackingCategory
    @iUserId     INT,
    @iCategoryID INT,
    @strName     NVARCHAR(100),
    @bActive     BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    IF @strName = ''
    BEGIN
        SELECT 'Error: Enter the category''s name.' AS Result, NULL AS Id;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblTrackingCategories WHERE iUserID = @iUserId AND strName = @strName AND iCategoryID <> ISNULL(@iCategoryID, 0))
    BEGIN
        SELECT 'Error: There is already a category with that name.' AS Result, NULL AS Id;
        RETURN;
    END

    IF ISNULL(@iCategoryID, 0) = 0
    BEGIN
        DECLARE @iPosition INT = CASE
            WHEN NOT EXISTS (SELECT 1 FROM tblTrackingCategories WHERE iUserID = @iUserId AND iPosition = 1) THEN 1
            WHEN NOT EXISTS (SELECT 1 FROM tblTrackingCategories WHERE iUserID = @iUserId AND iPosition = 2) THEN 2 END;
        IF @iPosition IS NULL
        BEGIN
            SELECT 'Error: A portfolio can have two tracking categories. Rename one instead.' AS Result, NULL AS Id;
            RETURN;
        END
        INSERT tblTrackingCategories (iUserID, iPosition, strName, bActive) VALUES (@iUserId, @iPosition, @strName, ISNULL(@bActive, 1));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END

    UPDATE tblTrackingCategories SET strName = @strName, bActive = ISNULL(@bActive, 1)
    WHERE iCategoryID = @iCategoryID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That category isn''t in this portfolio.' END AS Result, @iCategoryID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveTrackingOption
    @iUserId     INT,
    @iOptionID   INT,
    @iCategoryID INT,
    @strName     NVARCHAR(100),
    @bActive     BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    IF NOT EXISTS (SELECT 1 FROM tblTrackingCategories WHERE iCategoryID = @iCategoryID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That category isn''t in this portfolio.' AS Result, NULL AS Id;
        RETURN;
    END
    IF @strName = ''
    BEGIN
        SELECT 'Error: Enter the option''s name.' AS Result, NULL AS Id;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblTrackingOptions WHERE iCategoryID = @iCategoryID AND strName = @strName AND iOptionID <> ISNULL(@iOptionID, 0))
    BEGIN
        SELECT 'Error: That category already has an option with that name.' AS Result, NULL AS Id;
        RETURN;
    END

    IF ISNULL(@iOptionID, 0) = 0
    BEGIN
        INSERT tblTrackingOptions (iCategoryID, strName, bActive) VALUES (@iCategoryID, @strName, ISNULL(@bActive, 1));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblTrackingOptions SET strName = @strName, bActive = ISNULL(@bActive, 1)
    WHERE iOptionID = @iOptionID AND iCategoryID = @iCategoryID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That option isn''t in this category.' END AS Result, @iOptionID AS Id;
END
GO

-- Deletes an option nothing uses yet (otherwise make it inactive). Returns Result.
CREATE OR ALTER PROCEDURE dbo.uspDeleteTrackingOption
    @iUserId   INT,
    @iOptionID INT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                   WHERE O.iOptionID = @iOptionID AND C.iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That option isn''t in this portfolio.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblJournalLines WHERE iTrackingOption1ID = @iOptionID OR iTrackingOption2ID = @iOptionID)
       OR EXISTS (SELECT 1 FROM tblBudgets WHERE iTrackingOptionID = @iOptionID)
    BEGIN
        SELECT 'Error: That option is used on journals or a budget. Make it inactive instead.' AS Result;
        RETURN;
    END
    DELETE FROM tblTrackingOptions WHERE iOptionID = @iOptionID;
    SELECT 'Success' AS Result;
END
GO

-- Sets the tracking options on a journal's lines added after @iAfterLineID (the lines just added for one entry,
-- including a tax line split out of it). Each option must be an active one in this portfolio's category 1 or 2.
-- Errors are thrown, so the caller's transaction rolls the whole journal back.
CREATE OR ALTER PROCEDURE dbo.uspSetJournalLineTracking
    @iUserId      INT,
    @iJournalID   INT,
    @iAfterLineID INT,
    @iOption1ID   INT = NULL,
    @iOption2ID   INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @iOption1ID = NULLIF(@iOption1ID, 0);
    SET @iOption2ID = NULLIF(@iOption2ID, 0);
    IF @iOption1ID IS NULL AND @iOption2ID IS NULL
        RETURN;
    IF NOT EXISTS (SELECT 1 FROM tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserId)
        THROW 50701, 'That journal was not found in this portfolio.', 1;
    IF (@iOption1ID IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
            WHERE O.iOptionID = @iOption1ID AND C.iUserID = @iUserId AND C.iPosition = 1))
       OR (@iOption2ID IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
            WHERE O.iOptionID = @iOption2ID AND C.iUserID = @iUserId AND C.iPosition = 2))
        THROW 50702, 'Choose tracking options from this portfolio''s categories.', 1;

    UPDATE tblJournalLines SET iTrackingOption1ID = @iOption1ID, iTrackingOption2ID = @iOption2ID
    WHERE iJournalID = @iJournalID AND iJournalLineID > ISNULL(@iAfterLineID, 0);
END
GO

-- =============================================
-- Budgets
-- =============================================
CREATE OR ALTER PROCEDURE dbo.uspSaveBudget
    @iUserId           INT,
    @iBudgetID         INT,             -- 0 adds one
    @strName           NVARCHAR(100),
    @iYear             INT,
    @iTrackingOptionID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    SET @iTrackingOptionID = NULLIF(@iTrackingOptionID, 0);
    DECLARE @Error VARCHAR(200) = CASE
        WHEN @strName = '' THEN 'Enter a name for the budget.'
        WHEN @iYear IS NULL OR @iYear NOT BETWEEN 1990 AND 2200 THEN 'Choose the financial year.'
        WHEN @iTrackingOptionID IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM tblTrackingOptions O JOIN tblTrackingCategories C ON C.iCategoryID = O.iCategoryID
                WHERE O.iOptionID = @iTrackingOptionID AND C.iUserID = @iUserId) THEN 'Choose a tracking option from the list.'
        END;
    IF @Error IS NOT NULL
    BEGIN
        SELECT 'Error: ' + @Error AS Result, NULL AS Id;
        RETURN;
    END

    IF ISNULL(@iBudgetID, 0) = 0
    BEGIN
        INSERT tblBudgets (iUserID, strName, iYear, iTrackingOptionID) VALUES (@iUserId, @strName, @iYear, @iTrackingOptionID);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblBudgets SET strName = @strName, iYear = @iYear, iTrackingOptionID = @iTrackingOptionID, dtmUpdatedDate = GETDATE()
    WHERE iBudgetID = @iBudgetID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That budget isn''t in this portfolio.' END AS Result, @iBudgetID AS Id;
END
GO

-- Replaces a budget's amounts with <lines><b a="account" m="month 1-12" v="amount"/></lines>. Zero amounts are dropped.
CREATE OR ALTER PROCEDURE dbo.uspSaveBudgetLines
    @iUserId   INT,
    @iBudgetID INT,
    @xmlLines  XML
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM tblBudgets WHERE iBudgetID = @iBudgetID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: That budget isn''t in this portfolio.' AS Result;
        RETURN;
    END

    DECLARE @Lines TABLE (iAccountID INT, iMonth INT, dblAmount DECIMAL(19,4));
    INSERT @Lines (iAccountID, iMonth, dblAmount)
    SELECT X.b.value('@a', 'INT'), X.b.value('@m', 'INT'), X.b.value('@v', 'DECIMAL(19,4)')
    FROM @xmlLines.nodes('/lines/b') AS X(b);
    DELETE FROM @Lines WHERE ISNULL(dblAmount, 0) = 0;

    IF EXISTS (SELECT 1 FROM @Lines WHERE iMonth NOT BETWEEN 1 AND 12)
       OR EXISTS (SELECT 1 FROM @Lines L WHERE NOT EXISTS (
            SELECT 1 FROM tblChartOfAccounts A WHERE A.iAccountID = L.iAccountID AND (A.iUserID = @iUserId OR A.iUserID = 0)))
       OR EXISTS (SELECT 1 FROM @Lines GROUP BY iAccountID, iMonth HAVING COUNT(*) > 1)
    BEGIN
        SELECT 'Error: Check the accounts and months.' AS Result;
        RETURN;
    END

    BEGIN TRANSACTION;
    DELETE FROM tblBudgetLines WHERE iBudgetID = @iBudgetID;
    INSERT tblBudgetLines (iBudgetID, iAccountID, iMonth, dblAmount) SELECT @iBudgetID, iAccountID, iMonth, dblAmount FROM @Lines;
    UPDATE tblBudgets SET dtmUpdatedDate = GETDATE() WHERE iBudgetID = @iBudgetID;
    COMMIT TRANSACTION;
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteBudget
    @iUserId   INT,
    @iBudgetID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblBudgets WHERE iBudgetID = @iBudgetID AND iUserID = @iUserId;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That budget isn''t in this portfolio.' END AS Result;
END
GO

PRINT 'Tracking categories and budgets migration done.';
GO
