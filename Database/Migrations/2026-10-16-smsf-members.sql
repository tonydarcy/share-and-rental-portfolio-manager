-- =============================================
-- Migration: SMSF member accounts
-- Date:      16-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- Tax > SMSF members. Run it after 2026-10-16-tax-workpaper.sql. It only adds
-- tables and procedures. Safe to run more than once.
--
-- What it changes:
--   * tblSmsfMember (new): the fund's members and their dates of birth (for
--     the contribution caps and minimum pension).
--   * tblSmsfMemberAccount (new): each member's account for a year, in the
--     accumulation or retirement phase: opening balance, contributions,
--     rollovers in, pensions, lump sums and insurance premiums. Earnings and
--     tax are worked out by the site, not stored.
--   * uspGetSmsfMembers, uspSaveSmsfMember, uspDeleteSmsfMember,
--     uspSaveSmsfMemberAccount, uspDeleteSmsfMemberAccount (new). Account
--     changes refuse a locked year.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblSmsfMember', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSmsfMember (
        iMemberID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblSmsfMember PRIMARY KEY,
        iUserID        INT NOT NULL,
        strName        VARCHAR(200) NOT NULL,
        dtBirthDate    DATE NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblSmsfMember_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblSmsfMember_User ON dbo.tblSmsfMember (iUserID);
END
GO

IF OBJECT_ID('dbo.tblSmsfMemberAccount', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSmsfMemberAccount (
        iMemberAccountID   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblSmsfMemberAccount PRIMARY KEY,
        iMemberID          INT NOT NULL CONSTRAINT FK_tblSmsfMemberAccount_Member REFERENCES dbo.tblSmsfMember (iMemberID),
        iUserID            INT NOT NULL,
        iYear              INT NOT NULL,
        strPhase           VARCHAR(20) NOT NULL,   -- Accumulation, Retirement
        dblOpeningBalance  DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_Opening DEFAULT (0),
        dblConcessional    DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_CC DEFAULT (0),
        dblNonConcessional DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_NCC DEFAULT (0),
        dblRolloverIn      DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_Roll DEFAULT (0),
        dblPensionPaid     DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_Pension DEFAULT (0),
        dblLumpSum         DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_Lump DEFAULT (0),
        dblInsurance       DECIMAL(18, 2) NOT NULL CONSTRAINT DF_tblSmsfMemberAccount_Ins DEFAULT (0),
        CONSTRAINT UQ_tblSmsfMemberAccount UNIQUE (iMemberID, iYear, strPhase)
    );
    CREATE INDEX IX_tblSmsfMemberAccount_User ON dbo.tblSmsfMemberAccount (iUserID, iYear);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetSmsfMembers]
(
    @iUserId INT,
    @iYear   INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: The fund's members, then their accounts for the year.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT iMemberID, strName, dtBirthDate
    FROM tblSmsfMember WHERE iUserID = @iUserId
    ORDER BY strName;

    SELECT iMemberAccountID, iMemberID, iYear, strPhase, dblOpeningBalance, dblConcessional, dblNonConcessional,
           dblRolloverIn, dblPensionPaid, dblLumpSum, dblInsurance
    FROM tblSmsfMemberAccount WHERE iUserID = @iUserId AND iYear = @iYear
    ORDER BY iMemberID, strPhase;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveSmsfMember]
(
    @iUserId     INT,
    @iMemberID   INT = NULL,    -- NULL adds one
    @strName     VARCHAR(200),
    @dtBirthDate DATE = NULL
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or changes a member.
--              Returns one row: Result = 'Success', MemberId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strName = LTRIM(RTRIM(ISNULL(@strName, '')));
    IF @strName = ''
    BEGIN
        SELECT 'Error: Enter the member''s name.' AS Result, NULL AS MemberId;
        RETURN;
    END
    IF @iMemberID IS NULL
    BEGIN
        INSERT INTO tblSmsfMember (iUserID, strName, dtBirthDate) VALUES (@iUserId, @strName, @dtBirthDate);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS MemberId;
        RETURN;
    END
    UPDATE tblSmsfMember SET strName = @strName, dtBirthDate = @dtBirthDate
    WHERE iMemberID = @iMemberID AND iUserID = @iUserId;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That member was not found in this fund.' AS Result, NULL AS MemberId;
    ELSE
        SELECT 'Success' AS Result, @iMemberID AS MemberId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteSmsfMember]
(
    @iUserId   INT,
    @iMemberID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Deletes a member who has no accounts in any year.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM tblSmsfMemberAccount WHERE iMemberID = @iMemberID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: This member has accounts entered. Delete those first (in each year).' AS Result;
        RETURN;
    END
    DELETE FROM tblSmsfMember WHERE iMemberID = @iMemberID AND iUserID = @iUserId;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That member was not found in this fund.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveSmsfMemberAccount]
(
    @iUserId            INT,
    @iYear              INT,
    @dtYearEnd          DATE,
    @iMemberAccountID   INT = NULL,   -- NULL adds one
    @iMemberID          INT,
    @strPhase           VARCHAR(20),
    @dblOpeningBalance  DECIMAL(18, 2),
    @dblConcessional    DECIMAL(18, 2),
    @dblNonConcessional DECIMAL(18, 2),
    @dblRolloverIn      DECIMAL(18, 2),
    @dblPensionPaid     DECIMAL(18, 2),
    @dblLumpSum         DECIMAL(18, 2),
    @dblInsurance       DECIMAL(18, 2)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or changes a member's account for a year. A member has at
--              most one account per phase in a year.
--              Returns one row: Result = 'Success', MemberAccountId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its member accounts can''t change.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END
    IF NOT EXISTS (SELECT 1 FROM tblSmsfMember WHERE iMemberID = @iMemberID AND iUserID = @iUserId)
    BEGIN
        SELECT 'Error: Choose a member of this fund.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END
    IF ISNULL(@strPhase, '') NOT IN ('Accumulation', 'Retirement')
    BEGIN
        SELECT 'Error: Choose accumulation or retirement phase.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END
    IF @dblConcessional < 0 OR @dblNonConcessional < 0 OR @dblRolloverIn < 0 OR @dblPensionPaid < 0 OR @dblLumpSum < 0 OR @dblInsurance < 0
    BEGIN
        SELECT 'Error: Contributions and payments can''t be negative.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblSmsfMemberAccount
               WHERE iMemberID = @iMemberID AND iYear = @iYear AND strPhase = @strPhase AND iMemberAccountID <> ISNULL(@iMemberAccountID, 0))
    BEGIN
        SELECT 'Error: This member already has an account in that phase for the year. Edit it instead.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END

    IF @iMemberAccountID IS NULL
    BEGIN
        INSERT INTO tblSmsfMemberAccount (iMemberID, iUserID, iYear, strPhase, dblOpeningBalance, dblConcessional, dblNonConcessional,
                                          dblRolloverIn, dblPensionPaid, dblLumpSum, dblInsurance)
        VALUES (@iMemberID, @iUserId, @iYear, @strPhase, @dblOpeningBalance, @dblConcessional, @dblNonConcessional,
                @dblRolloverIn, @dblPensionPaid, @dblLumpSum, @dblInsurance);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS MemberAccountId;
        RETURN;
    END

    UPDATE tblSmsfMemberAccount
    SET iMemberID = @iMemberID, strPhase = @strPhase, dblOpeningBalance = @dblOpeningBalance, dblConcessional = @dblConcessional,
        dblNonConcessional = @dblNonConcessional, dblRolloverIn = @dblRolloverIn, dblPensionPaid = @dblPensionPaid,
        dblLumpSum = @dblLumpSum, dblInsurance = @dblInsurance
    WHERE iMemberAccountID = @iMemberAccountID AND iUserID = @iUserId AND iYear = @iYear;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That account was not found in this fund and year.' AS Result, NULL AS MemberAccountId;
    ELSE
        SELECT 'Success' AS Result, @iMemberAccountID AS MemberAccountId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteSmsfMemberAccount]
(
    @iUserId          INT,
    @iYear            INT,
    @dtYearEnd        DATE,
    @iMemberAccountID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Deletes a member's account for a year.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    IF dbo.fnLockedTo(@iUserId) >= @dtYearEnd
    BEGIN
        SELECT 'Error: This financial year is locked, so its member accounts can''t change.' AS Result;
        RETURN;
    END
    DELETE FROM tblSmsfMemberAccount WHERE iMemberAccountID = @iMemberAccountID AND iUserID = @iUserId AND iYear = @iYear;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That account was not found in this fund and year.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO
