-- SMSF extras (roadmap 35): transfer balance account report (TBAR) events, the actuary's exempt pension
-- income (ECPI) details and the trustees' investment strategy for each year. Safe to run twice.
-- No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblTbarEvents', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblTbarEvents (
        iEventID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblTbarEvents PRIMARY KEY,
        iUserID       INT NOT NULL,
        iMemberID     INT NOT NULL,
        dtmEvent      DATE NOT NULL,
        strEventType  VARCHAR(30) NOT NULL,
        dblAmount     DECIMAL(19,4) NOT NULL,
        strNote       NVARCHAR(500) NULL,
        dtmReported   DATE NULL
    );
    CREATE INDEX IX_tblTbarEvents_User ON dbo.tblTbarEvents (iUserID, dtmEvent);
END
GO

-- One row per fund and financial year (the year it ends). strStrategy holds the strategy as XML text.
IF OBJECT_ID('dbo.tblSmsfYearExtras', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSmsfYearExtras (
        iUserID              INT NOT NULL,
        iYear                INT NOT NULL,
        strEcpiMethod        VARCHAR(20) NULL,
        dblActuaryPct        DECIMAL(9,4) NULL,
        strActuary           NVARCHAR(200) NULL,
        dtmCertificate       DATE NULL,
        strStrategy          NVARCHAR(MAX) NULL,
        dtmStrategyReviewed  DATE NULL,
        CONSTRAINT PK_tblSmsfYearExtras PRIMARY KEY (iUserID, iYear)
    );
END
GO

-- The member is checked against the fund's own members (tblSmsfMember, from the SMSF members migration)
CREATE OR ALTER PROCEDURE dbo.uspSaveTbarEvent
    @iUserID INT, @iEventID INT = 0, @iMemberID INT, @dtmEvent DATE, @strEventType VARCHAR(30), @dblAmount DECIMAL(19,4),
    @strNote NVARCHAR(500) = NULL, @dtmReported DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM tblSmsfMember WHERE iMemberID = @iMemberID AND iUserID = @iUserID)
    BEGIN SELECT 'Choose one of the fund''s members.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF @strEventType NOT IN ('Commenced', 'Commutation', 'StopsRetirement', 'Lrba', 'StructuredSettlement', 'OtherCredit', 'OtherDebit')
    BEGIN SELECT 'Choose the kind of event.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    IF ISNULL(@dblAmount, 0) <= 0 BEGIN SELECT 'Enter the amount.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END

    IF ISNULL(@iEventID, 0) = 0
    BEGIN
        INSERT INTO tblTbarEvents (iUserID, iMemberID, dtmEvent, strEventType, dblAmount, strNote, dtmReported)
        VALUES (@iUserID, @iMemberID, @dtmEvent, @strEventType, @dblAmount, @strNote, @dtmReported);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS Id;
        RETURN;
    END
    UPDATE tblTbarEvents
    SET iMemberID = @iMemberID, dtmEvent = @dtmEvent, strEventType = @strEventType, dblAmount = @dblAmount, strNote = @strNote, dtmReported = @dtmReported
    WHERE iEventID = @iEventID AND iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That event was not found.' AS Result, CAST(NULL AS INT) AS Id; RETURN; END
    SELECT 'Success' AS Result, @iEventID AS Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspSetTbarReported @iUserID INT, @iEventID INT, @dtmReported DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE tblTbarEvents SET dtmReported = @dtmReported WHERE iEventID = @iEventID AND iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That event was not found.'; RETURN; END
    SELECT 'Success';
END
GO

CREATE OR ALTER PROCEDURE dbo.uspDeleteTbarEvent @iUserID INT, @iEventID INT
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblTbarEvents WHERE iEventID = @iEventID AND iUserID = @iUserID;
    IF @@ROWCOUNT = 0 BEGIN SELECT 'That event was not found.'; RETURN; END
    SELECT 'Success';
END
GO

-- @bStrategy 0 saves the ECPI details only, 1 the investment strategy only, so each form leaves the other alone
CREATE OR ALTER PROCEDURE dbo.uspSaveSmsfYearExtras
    @iUserID INT, @iYear INT, @bStrategy BIT,
    @strEcpiMethod VARCHAR(20) = NULL, @dblActuaryPct DECIMAL(9,4) = NULL, @strActuary NVARCHAR(200) = NULL, @dtmCertificate DATE = NULL,
    @strStrategy NVARCHAR(MAX) = NULL, @dtmStrategyReviewed DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @bStrategy = 0 AND @strEcpiMethod IS NOT NULL AND @strEcpiMethod NOT IN ('Segregated', 'Proportionate', 'Both')
    BEGIN SELECT 'Choose the ECPI method.'; RETURN; END
    IF @dblActuaryPct IS NOT NULL AND (@dblActuaryPct < 0 OR @dblActuaryPct > 100)
    BEGIN SELECT 'The actuary''s percentage must be between 0 and 100.'; RETURN; END

    IF NOT EXISTS (SELECT 1 FROM tblSmsfYearExtras WHERE iUserID = @iUserID AND iYear = @iYear)
        INSERT INTO tblSmsfYearExtras (iUserID, iYear) VALUES (@iUserID, @iYear);
    IF @bStrategy = 0
        UPDATE tblSmsfYearExtras SET strEcpiMethod = @strEcpiMethod, dblActuaryPct = @dblActuaryPct, strActuary = @strActuary, dtmCertificate = @dtmCertificate
        WHERE iUserID = @iUserID AND iYear = @iYear;
    ELSE
        UPDATE tblSmsfYearExtras SET strStrategy = @strStrategy, dtmStrategyReviewed = @dtmStrategyReviewed
        WHERE iUserID = @iUserID AND iYear = @iYear;
    SELECT 'Success';
END
GO
