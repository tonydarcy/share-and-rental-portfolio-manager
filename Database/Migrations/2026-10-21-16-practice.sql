-- =============================================
-- Migration: practice tools
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Clients > Practice dashboard,
-- Clients > Client queries, Tax work > Workpapers and year-end checklist, and Clients > Onboarding. Safe to run more
-- than once. It only adds things.
--
-- What it adds:
--   * tblYearStatus: each client's year-end status (not started, in progress, review, signed; locked comes from the
--     lock date). uspSaveYearStatus.
--   * tblYearChecklist: the year-end checklist ticks. uspSaveChecklistItem.
--   * tblWorkpapers: a workpaper per balance sheet account and year, with the preparer's note and the reviewer's
--     sign-off. uspSaveWorkpaper, uspReviewWorkpaper.
--   * tblClientQueries: "what is this transaction?" questions and the client's answers. uspAddClientQuery,
--     uspAnswerClientQuery, uspSetClientQueryStatus, uspDeleteClientQuery.
--   * tblOnboarding: engagement letters sent to new clients by a link, the details they fill in, and the portfolio
--     set up from them. uspAddOnboarding, uspAcceptOnboarding, uspCompleteOnboarding, uspCancelOnboarding.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblYearStatus', 'U') IS NULL
    CREATE TABLE dbo.tblYearStatus (
        iUserID          INT NOT NULL,
        iYear            INT NOT NULL,                  -- the financial year's end year
        strStatus        VARCHAR(20) NOT NULL,          -- NotStarted, InProgress, Review, Signed
        strNote          NVARCHAR(500) NULL,
        iChangedByUserID INT NULL,
        dtmChanged       DATETIME NOT NULL CONSTRAINT DF_tblYearStatus_Changed DEFAULT (GETDATE()),
        CONSTRAINT PK_tblYearStatus PRIMARY KEY (iUserID, iYear)
    );
GO

IF OBJECT_ID('dbo.tblYearChecklist', 'U') IS NULL
    CREATE TABLE dbo.tblYearChecklist (
        iUserID       INT NOT NULL,
        iYear         INT NOT NULL,
        strItemKey    VARCHAR(50) NOT NULL,             -- the item's key in the site's checklist for the entity type
        bDone         BIT NOT NULL,
        strNote       NVARCHAR(500) NULL,
        iDoneByUserID INT NULL,
        dtmDone       DATETIME NULL,
        CONSTRAINT PK_tblYearChecklist PRIMARY KEY (iUserID, iYear, strItemKey)
    );
GO

IF OBJECT_ID('dbo.tblWorkpapers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblWorkpapers (
        iWorkpaperID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblWorkpapers PRIMARY KEY,
        iUserID           INT NOT NULL,
        iYear             INT NOT NULL,
        iAccountID        INT NOT NULL,
        strNote           NVARCHAR(2000) NULL,          -- how the balance was checked
        dblSupported      DECIMAL(19,2) NULL,           -- the balance the bank statement, holding statement or schedule shows
        iPreparedByUserID INT NULL,
        dtmPrepared       DATETIME NULL,
        iReviewedByUserID INT NULL,
        dtmReviewed       DATETIME NULL,
        strReviewNote     NVARCHAR(1000) NULL
    );
    CREATE UNIQUE INDEX UX_tblWorkpapers ON dbo.tblWorkpapers (iUserID, iYear, iAccountID);
END
GO

IF OBJECT_ID('dbo.tblClientQueries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblClientQueries (
        iQueryID          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblClientQueries PRIMARY KEY,
        iUserID           INT NOT NULL,
        iJournalID        INT NULL,                     -- the journal asked about
        iJournalLineID    INT NULL,                     -- or an imported bank line still unallocated
        dtmTransaction    DATE NULL,
        dblAmount         DECIMAL(19,2) NULL,
        strTransaction    NVARCHAR(500) NULL,
        strQuestion       NVARCHAR(2000) NOT NULL,
        iAskedByUserID    INT NULL,
        dtmAsked          DATETIME NOT NULL CONSTRAINT DF_tblClientQueries_Asked DEFAULT (GETDATE()),
        strAnswer         NVARCHAR(2000) NULL,
        iAnsweredByUserID INT NULL,
        dtmAnswered       DATETIME NULL,
        strStatus         VARCHAR(10) NOT NULL CONSTRAINT DF_tblClientQueries_Status DEFAULT ('Open')   -- Open, Answered, Closed
    );
    CREATE INDEX IX_tblClientQueries_User ON dbo.tblClientQueries (iUserID, strStatus);
END
GO

IF OBJECT_ID('dbo.tblOnboarding', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblOnboarding (
        iOnboardingID    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblOnboarding PRIMARY KEY,
        iPracticeUserID  INT NOT NULL,                  -- the accountant's login
        strToken         VARCHAR(64) NOT NULL,          -- the secret in the link sent to the client
        strClientName    NVARCHAR(200) NOT NULL,
        strClientEmail   NVARCHAR(320) NOT NULL,
        strEntityType    VARCHAR(20) NOT NULL,
        strLetter        NVARCHAR(MAX) NOT NULL,
        strStatus        VARCHAR(10) NOT NULL CONSTRAINT DF_tblOnboarding_Status DEFAULT ('Sent'),   -- Sent, Accepted, SetUp, Cancelled
        dtmCreated       DATETIME NOT NULL CONSTRAINT DF_tblOnboarding_Created DEFAULT (GETDATE()),
        dtmAccepted      DATETIME NULL,
        strAcceptedName  NVARCHAR(200) NULL,
        strAcceptedIp    VARCHAR(64) NULL,
        strDetailsXml    NVARCHAR(MAX) NULL,            -- the details form, as the site wrote it
        iPortfolioID     INT NULL,
        dtmSetUp         DATETIME NULL
    );
    CREATE UNIQUE INDEX UX_tblOnboarding_Token ON dbo.tblOnboarding (strToken);
    CREATE INDEX IX_tblOnboarding_Practice ON dbo.tblOnboarding (iPracticeUserID);
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveYearStatus
    @iUserID          INT,
    @iYear            INT,
    @strStatus        VARCHAR(20),
    @strNote          NVARCHAR(500) = NULL,
    @iChangedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strStatus NOT IN ('NotStarted', 'InProgress', 'Review', 'Signed') OR @iYear NOT BETWEEN 1990 AND 2100
    BEGIN
        SELECT 'Error: Choose a status.' AS Result;
        RETURN;
    END
    UPDATE dbo.tblYearStatus SET strStatus = @strStatus, strNote = NULLIF(LTRIM(RTRIM(@strNote)), ''), iChangedByUserID = @iChangedByUserID, dtmChanged = GETDATE()
    WHERE iUserID = @iUserID AND iYear = @iYear;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblYearStatus (iUserID, iYear, strStatus, strNote, iChangedByUserID) VALUES (@iUserID, @iYear, @strStatus, NULLIF(LTRIM(RTRIM(@strNote)), ''), @iChangedByUserID);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveChecklistItem
    @iUserID       INT,
    @iYear         INT,
    @strItemKey    VARCHAR(50),
    @bDone         BIT,
    @strNote       NVARCHAR(500) = NULL,
    @iDoneByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strItemKey, ''))) = '' OR @iYear NOT BETWEEN 1990 AND 2100
    BEGIN
        SELECT 'Error: That checklist item wasn''t found.' AS Result;
        RETURN;
    END
    UPDATE dbo.tblYearChecklist
    SET bDone = @bDone, strNote = NULLIF(LTRIM(RTRIM(@strNote)), ''),
        iDoneByUserID = CASE WHEN @bDone = 1 THEN @iDoneByUserID END, dtmDone = CASE WHEN @bDone = 1 THEN GETDATE() END
    WHERE iUserID = @iUserID AND iYear = @iYear AND strItemKey = @strItemKey;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblYearChecklist (iUserID, iYear, strItemKey, bDone, strNote, iDoneByUserID, dtmDone)
        VALUES (@iUserID, @iYear, @strItemKey, @bDone, NULLIF(LTRIM(RTRIM(@strNote)), ''), CASE WHEN @bDone = 1 THEN @iDoneByUserID END,
            CASE WHEN @bDone = 1 THEN GETDATE() END);
    SELECT 'Success' AS Result;
END
GO

-- The preparer's note and supported balance. Changing a workpaper takes off any reviewer's sign-off.
CREATE OR ALTER PROCEDURE dbo.uspSaveWorkpaper
    @iUserID           INT,
    @iYear             INT,
    @iAccountID        INT,
    @strNote           NVARCHAR(2000) = NULL,
    @dblSupported      DECIMAL(19,2) = NULL,
    @iPreparedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.tblChartOfAccounts WHERE iAccountID = @iAccountID AND (iUserID = @iUserID OR iUserID = 0))
    BEGIN
        SELECT 'Error: That account isn''t in this portfolio.' AS Result, NULL AS Id;
        RETURN;
    END
    UPDATE dbo.tblWorkpapers
    SET strNote = NULLIF(LTRIM(RTRIM(@strNote)), ''), dblSupported = @dblSupported, iPreparedByUserID = @iPreparedByUserID, dtmPrepared = GETDATE(),
        iReviewedByUserID = NULL, dtmReviewed = NULL
    WHERE iUserID = @iUserID AND iYear = @iYear AND iAccountID = @iAccountID;
    IF @@ROWCOUNT = 0
        INSERT dbo.tblWorkpapers (iUserID, iYear, iAccountID, strNote, dblSupported, iPreparedByUserID, dtmPrepared)
        VALUES (@iUserID, @iYear, @iAccountID, NULLIF(LTRIM(RTRIM(@strNote)), ''), @dblSupported, @iPreparedByUserID, GETDATE());
    SELECT 'Success' AS Result,
        (SELECT iWorkpaperID FROM dbo.tblWorkpapers WHERE iUserID = @iUserID AND iYear = @iYear AND iAccountID = @iAccountID) AS Id;
END
GO

-- Signs off (@bSignOff 1) or takes the sign-off away. Only a prepared workpaper can be signed off.
CREATE OR ALTER PROCEDURE dbo.uspReviewWorkpaper
    @iUserID           INT,
    @iWorkpaperID      INT,
    @bSignOff          BIT,
    @strReviewNote     NVARCHAR(1000) = NULL,
    @iReviewedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblWorkpapers
    SET iReviewedByUserID = CASE WHEN @bSignOff = 1 THEN @iReviewedByUserID END, dtmReviewed = CASE WHEN @bSignOff = 1 THEN GETDATE() END,
        strReviewNote = NULLIF(LTRIM(RTRIM(@strReviewNote)), '')
    WHERE iWorkpaperID = @iWorkpaperID AND iUserID = @iUserID AND dtmPrepared IS NOT NULL;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That workpaper hasn''t been prepared yet.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAddClientQuery
    @iUserID        INT,
    @iJournalID     INT = NULL,
    @iJournalLineID INT = NULL,
    @dtmTransaction DATE = NULL,
    @dblAmount      DECIMAL(19,2) = NULL,
    @strTransaction NVARCHAR(500) = NULL,
    @strQuestion    NVARCHAR(2000),
    @iAskedByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strQuestion, ''))) = ''
    BEGIN
        SELECT 'Error: Enter the question.' AS Result, NULL AS Id;
        RETURN;
    END
    IF (@iJournalID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.tblJournal WHERE iJournalID = @iJournalID AND iUserID = @iUserID))
        OR (@iJournalLineID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.tblJournalLines L JOIN dbo.tblJournal J ON J.iJournalID = L.iJournalID
                                                       WHERE L.iJournalLineID = @iJournalLineID AND J.iUserID = @iUserID))
    BEGIN
        SELECT 'Error: That transaction isn''t in this portfolio.' AS Result, NULL AS Id;
        RETURN;
    END
    INSERT dbo.tblClientQueries (iUserID, iJournalID, iJournalLineID, dtmTransaction, dblAmount, strTransaction, strQuestion, iAskedByUserID)
    VALUES (@iUserID, @iJournalID, @iJournalLineID, @dtmTransaction, @dblAmount, NULLIF(LTRIM(RTRIM(@strTransaction)), ''), LTRIM(RTRIM(@strQuestion)), @iAskedByUserID);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAnswerClientQuery
    @iUserID           INT,
    @iQueryID          INT,
    @strAnswer         NVARCHAR(2000),
    @iAnsweredByUserID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strAnswer, ''))) = ''
    BEGIN
        SELECT 'Error: Enter your answer.' AS Result;
        RETURN;
    END
    UPDATE dbo.tblClientQueries
    SET strAnswer = LTRIM(RTRIM(@strAnswer)), iAnsweredByUserID = @iAnsweredByUserID, dtmAnswered = GETDATE(), strStatus = 'Answered'
    WHERE iQueryID = @iQueryID AND iUserID = @iUserID AND strStatus <> 'Closed';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That question isn''t open in this portfolio.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSetClientQueryStatus
    @iUserID   INT,
    @iQueryID  INT,
    @strStatus VARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;
    IF @strStatus NOT IN ('Open', 'Closed')
    BEGIN
        SELECT 'Error: Choose open or closed.' AS Result;
        RETURN;
    END
    UPDATE dbo.tblClientQueries
    SET strStatus = CASE WHEN @strStatus = 'Open' AND strAnswer IS NOT NULL THEN 'Answered' ELSE @strStatus END
    WHERE iQueryID = @iQueryID AND iUserID = @iUserID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That question isn''t in this portfolio.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteClientQuery
    @iUserID  INT,
    @iQueryID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblClientQueries WHERE iQueryID = @iQueryID AND iUserID = @iUserID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That question isn''t in this portfolio.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspAddOnboarding
    @iPracticeUserID INT,
    @strToken        VARCHAR(64),
    @strClientName   NVARCHAR(200),
    @strClientEmail  NVARCHAR(320),
    @strEntityType   VARCHAR(20),
    @strLetter       NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    IF LEN(ISNULL(@strToken, '')) < 32 OR LTRIM(RTRIM(ISNULL(@strClientName, ''))) = '' OR ISNULL(@strClientEmail, '') NOT LIKE '%_@_%'
        OR @strEntityType NOT IN ('Individual', 'Partnership', 'Company', 'Trust', 'SMSF') OR LTRIM(RTRIM(ISNULL(@strLetter, ''))) = ''
    BEGIN
        SELECT 'Error: Enter the client''s name, email, entity type and the engagement letter.' AS Result, NULL AS Id;
        RETURN;
    END
    INSERT dbo.tblOnboarding (iPracticeUserID, strToken, strClientName, strClientEmail, strEntityType, strLetter)
    VALUES (@iPracticeUserID, @strToken, LTRIM(RTRIM(@strClientName)), LTRIM(RTRIM(@strClientEmail)), @strEntityType, @strLetter);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

-- The client accepts the letter by the link and sends their details. Only once, and not after it's cancelled.
CREATE OR ALTER PROCEDURE dbo.uspAcceptOnboarding
    @strToken        VARCHAR(64),
    @strAcceptedName NVARCHAR(200),
    @strAcceptedIp   VARCHAR(64) = NULL,
    @strDetailsXml   NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strAcceptedName, ''))) = ''
    BEGIN
        SELECT 'Error: Type your name to accept the letter.' AS Result;
        RETURN;
    END
    UPDATE dbo.tblOnboarding
    SET strStatus = 'Accepted', dtmAccepted = GETDATE(), strAcceptedName = LTRIM(RTRIM(@strAcceptedName)), strAcceptedIp = @strAcceptedIp,
        strDetailsXml = @strDetailsXml
    WHERE strToken = @strToken AND strStatus = 'Sent';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: This link has already been used or has been cancelled.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspCompleteOnboarding
    @iPracticeUserID INT,
    @iOnboardingID   INT,
    @iPortfolioID    INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblOnboarding SET strStatus = 'SetUp', iPortfolioID = @iPortfolioID, dtmSetUp = GETDATE()
    WHERE iOnboardingID = @iOnboardingID AND iPracticeUserID = @iPracticeUserID AND strStatus = 'Accepted';
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That client hasn''t accepted yet, or is already set up.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspCancelOnboarding
    @iPracticeUserID INT,
    @iOnboardingID   INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblOnboarding SET strStatus = 'Cancelled'
    WHERE iOnboardingID = @iOnboardingID AND iPracticeUserID = @iPracticeUserID AND strStatus IN ('Sent', 'Accepted');
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That onboarding can''t be cancelled.' END AS Result;
END
GO
