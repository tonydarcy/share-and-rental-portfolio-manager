-- =============================================
-- Migration: repeating and reversing journals
-- Date:      21-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with Accounting > Repeating
-- journals. Safe to run more than once. It only adds things.
--
-- What it adds:
--   * tblRecurringJournals: a manual journal's lines kept with a schedule (weekly to yearly, from a start date to an
--     optional end date) and how many times it has been posted. The journals themselves are posted by the site through
--     the usual journal procs, so the lock and tax rules are the same as for any manual journal.
--   * uspSaveRecurringJournal, uspMarkRecurringJournalPosted, uspSetRecurringJournalActive, uspDeleteRecurringJournal.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblRecurringJournals', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblRecurringJournals (
        iRecurringJournalID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblRecurringJournals PRIMARY KEY,
        iUserID             INT NOT NULL,
        strName             NVARCHAR(200) NOT NULL,
        strDescription      NVARCHAR(500) NOT NULL,
        strFrequency        VARCHAR(20) NOT NULL,      -- Weekly, Fortnightly, Monthly, Quarterly, HalfYearly, Yearly
        dtmStart            DATE NOT NULL,
        dtmEnd              DATE NULL,
        iPostedCount        INT NOT NULL CONSTRAINT DF_tblRecurringJournals_Posted DEFAULT (0),
        bReverse            BIT NOT NULL CONSTRAINT DF_tblRecurringJournals_Reverse DEFAULT (0),   -- reversed the next day
        bActive             BIT NOT NULL CONSTRAINT DF_tblRecurringJournals_Active DEFAULT (1),
        strLinesXml         NVARCHAR(MAX) NOT NULL,    -- <lines><l a="account" d="debit" c="credit" t="tax code" x="tax" n="text"/></lines>
        dtmLastPosted       DATE NULL,
        dtmCreatedDate      DATETIME NOT NULL CONSTRAINT DF_tblRecurringJournals_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblRecurringJournals_User ON dbo.tblRecurringJournals (iUserID);
END
GO

-- Adds (@iRecurringJournalID 0) or changes one. @iPostedCount NULL keeps the count. Returns Result and Id.
CREATE OR ALTER PROCEDURE dbo.uspSaveRecurringJournal
    @iUserID             INT,
    @iRecurringJournalID INT = 0,
    @strName             NVARCHAR(200),
    @strDescription      NVARCHAR(500),
    @strFrequency        VARCHAR(20),
    @dtmStart            DATE,
    @dtmEnd              DATE = NULL,
    @bReverse            BIT = 0,
    @strLinesXml         NVARCHAR(MAX),
    @iPostedCount        INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF LTRIM(RTRIM(ISNULL(@strDescription, ''))) = '' OR @dtmStart IS NULL OR ISNULL(@strLinesXml, '') = ''
        OR @strFrequency NOT IN ('Weekly', 'Fortnightly', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly')
        OR (@dtmEnd IS NOT NULL AND @dtmEnd < @dtmStart)
    BEGIN
        SELECT 'Error: Check the description, how often it repeats and the dates.' AS Result, NULL AS Id;
        RETURN;
    END
    IF ISNULL(@iRecurringJournalID, 0) > 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.tblRecurringJournals WHERE iRecurringJournalID = @iRecurringJournalID AND iUserID = @iUserID)
        BEGIN
            SELECT 'Error: That repeating journal isn''t in this portfolio.' AS Result, NULL AS Id;
            RETURN;
        END
        UPDATE dbo.tblRecurringJournals
        SET strName = LTRIM(RTRIM(@strName)), strDescription = LTRIM(RTRIM(@strDescription)), strFrequency = @strFrequency,
            dtmStart = @dtmStart, dtmEnd = @dtmEnd, bReverse = ISNULL(@bReverse, 0), strLinesXml = @strLinesXml,
            iPostedCount = ISNULL(@iPostedCount, iPostedCount)
        WHERE iRecurringJournalID = @iRecurringJournalID AND iUserID = @iUserID;
        SELECT 'Success' AS Result, @iRecurringJournalID AS Id;
        RETURN;
    END
    INSERT dbo.tblRecurringJournals (iUserID, strName, strDescription, strFrequency, dtmStart, dtmEnd, iPostedCount, bReverse, strLinesXml, dtmLastPosted)
    VALUES (@iUserID, LTRIM(RTRIM(@strName)), LTRIM(RTRIM(@strDescription)), @strFrequency, @dtmStart, @dtmEnd, ISNULL(@iPostedCount, 0),
        ISNULL(@bReverse, 0), @strLinesXml, CASE WHEN ISNULL(@iPostedCount, 0) > 0 THEN @dtmStart END);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
END
GO

-- Moves the count on by one, only if it is still @iPostedCount (so two people posting at once can't post it twice).
CREATE OR ALTER PROCEDURE dbo.uspMarkRecurringJournalPosted
    @iUserID             INT,
    @iRecurringJournalID INT,
    @iPostedCount        INT,
    @dtmPosted           DATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRecurringJournals SET iPostedCount = iPostedCount + 1, dtmLastPosted = @dtmPosted
    WHERE iRecurringJournalID = @iRecurringJournalID AND iUserID = @iUserID AND iPostedCount = @iPostedCount;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: It has already been posted.' END AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSetRecurringJournalActive
    @iUserID             INT,
    @iRecurringJournalID INT,
    @bActive             BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.tblRecurringJournals SET bActive = @bActive WHERE iRecurringJournalID = @iRecurringJournalID AND iUserID = @iUserID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating journal isn''t in this portfolio.' END AS Result;
END
GO

-- Deletes the schedule only; journals already posted stay.
CREATE OR ALTER PROCEDURE dbo.uspDeleteRecurringJournal
    @iUserID             INT,
    @iRecurringJournalID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE dbo.tblRecurringJournals WHERE iRecurringJournalID = @iRecurringJournalID AND iUserID = @iUserID;
    SELECT CASE WHEN @@ROWCOUNT = 1 THEN 'Success' ELSE 'Error: That repeating journal isn''t in this portfolio.' END AS Result;
END
GO
