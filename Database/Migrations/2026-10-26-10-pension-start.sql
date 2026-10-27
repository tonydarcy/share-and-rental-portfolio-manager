-- =============================================
-- Migration: pensions started during the year
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with pro-rata minimum pensions
-- and the actuary data file. Run it after 2026-10-16-smsf-members.sql. Safe to run more than once.
--
-- What it changes:
--   * tblSmsfMemberAccount gets dtPensionStart (the day a pension started, when it started during the year) and
--     dblStartBalance (the account balance that day). The minimum pension for that year is then worked out on the
--     start balance for the days left in the year.
--   * uspGetSmsfMembers returns them; uspSaveSmsfMemberAccount takes @dtPensionStart and @dblStartBalance.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblSmsfMemberAccount', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-16-smsf-members.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF COL_LENGTH('dbo.tblSmsfMemberAccount', 'dtPensionStart') IS NULL
    ALTER TABLE dbo.tblSmsfMemberAccount ADD dtPensionStart DATE NULL, dblStartBalance DECIMAL(18, 2) NULL;
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
-- Date:        26-Oct-2026
-- Description: Also the pension start date and balance.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT iMemberID, strName, dtBirthDate
    FROM tblSmsfMember WHERE iUserID = @iUserId
    ORDER BY strName;

    SELECT iMemberAccountID, iMemberID, iYear, strPhase, dblOpeningBalance, dblConcessional, dblNonConcessional,
           dblRolloverIn, dblPensionPaid, dblLumpSum, dblInsurance, dtPensionStart, dblStartBalance
    FROM tblSmsfMemberAccount WHERE iUserID = @iUserId AND iYear = @iYear
    ORDER BY iMemberID, strPhase;
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
    @dblInsurance       DECIMAL(18, 2),
    @dtPensionStart     DATE = NULL,           -- a pension started during the year
    @dblStartBalance    DECIMAL(18, 2) = NULL  -- its balance that day
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds or changes a member's account for a year. A member has at
--              most one account per phase in a year.
--              Returns one row: Result = 'Success', MemberAccountId; or 'Error: ...'.
-- Date:        26-Oct-2026
-- Description: Takes the start date and balance of a pension started in the year.
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
       OR @dblStartBalance < 0
    BEGIN
        SELECT 'Error: Contributions and payments can''t be negative.' AS Result, NULL AS MemberAccountId;
        RETURN;
    END
    IF @strPhase <> 'Retirement'
        SELECT @dtPensionStart = NULL, @dblStartBalance = NULL;
    IF @dtPensionStart IS NOT NULL AND (@dtPensionStart > @dtYearEnd OR @dtPensionStart <= DATEADD(YEAR, -1, @dtYearEnd))
    BEGIN
        SELECT 'Error: The pension start date must be in this financial year. Leave it blank for a pension running all year.' AS Result, NULL AS MemberAccountId;
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
                                          dblRolloverIn, dblPensionPaid, dblLumpSum, dblInsurance, dtPensionStart, dblStartBalance)
        VALUES (@iMemberID, @iUserId, @iYear, @strPhase, @dblOpeningBalance, @dblConcessional, @dblNonConcessional,
                @dblRolloverIn, @dblPensionPaid, @dblLumpSum, @dblInsurance, @dtPensionStart, @dblStartBalance);
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS MemberAccountId;
        RETURN;
    END

    UPDATE tblSmsfMemberAccount
    SET iMemberID = @iMemberID, strPhase = @strPhase, dblOpeningBalance = @dblOpeningBalance, dblConcessional = @dblConcessional,
        dblNonConcessional = @dblNonConcessional, dblRolloverIn = @dblRolloverIn, dblPensionPaid = @dblPensionPaid,
        dblLumpSum = @dblLumpSum, dblInsurance = @dblInsurance, dtPensionStart = @dtPensionStart, dblStartBalance = @dblStartBalance
    WHERE iMemberAccountID = @iMemberAccountID AND iUserID = @iUserId AND iYear = @iYear;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That account was not found in this fund and year.' AS Result, NULL AS MemberAccountId;
    ELSE
        SELECT 'Success' AS Result, @iMemberAccountID AS MemberAccountId;
END
GO

PRINT 'Pension start migration done.';
GO
