-- =============================================
-- Migration: company franking account
-- Date:      16-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- Tax > Company tax and franking. Run it after 2026-10-15-lock-periods.sql (it
-- uses fnLockedTo). It only adds a table and procedures. Safe to run more than once.
--
-- What it changes:
--   * tblFrankingEntry (new): franking account entries the payouts don't
--     supply: the opening balance, tax paid (credits), franked dividends paid
--     and tax refunds (debits). Franking credits received on dividends come
--     from the payouts. Amount is positive for a credit, negative for a debit.
--   * uspGetFrankingEntries, uspAddFrankingEntry, uspDeleteFrankingEntry (new).
--     Adding or deleting refuses a date in a locked period.
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblFrankingEntry', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblFrankingEntry (
        iEntryID       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblFrankingEntry PRIMARY KEY,
        iUserID        INT NOT NULL,
        dtDate         DATE NOT NULL,
        strKind        VARCHAR(20) NOT NULL,   -- Opening, TaxPaid, DividendPaid, Refund, Other
        strDescription VARCHAR(200) NOT NULL,
        dblAmount      DECIMAL(18, 2) NOT NULL,
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblFrankingEntry_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblFrankingEntry_User ON dbo.tblFrankingEntry (iUserID, dtDate);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetFrankingEntries]
(
    @iUserId INT,
    @dtTo    DATE
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Every franking account entry up to the date, oldest first.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT iEntryID, dtDate, strKind, strDescription, dblAmount
    FROM tblFrankingEntry
    WHERE iUserID = @iUserId AND dtDate <= @dtTo
    ORDER BY dtDate, iEntryID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspAddFrankingEntry]
(
    @iUserId        INT,
    @dtDate         DATE,
    @strKind        VARCHAR(20),
    @strDescription VARCHAR(200),
    @dblAmount      DECIMAL(18, 2)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Adds a franking account entry. Credits are positive, debits negative.
--              Returns one row: Result = 'Success', EntryId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SET @strDescription = LTRIM(RTRIM(ISNULL(@strDescription, '')));
    IF dbo.fnLockedTo(@iUserId) >= @dtDate
    BEGIN
        SELECT 'Error: That date is in a locked period.' AS Result, NULL AS EntryId;
        RETURN;
    END
    IF ISNULL(@strKind, '') NOT IN ('Opening', 'TaxPaid', 'DividendPaid', 'Refund', 'Other')
    BEGIN
        SELECT 'Error: Choose the kind of entry.' AS Result, NULL AS EntryId;
        RETURN;
    END
    IF ISNULL(@dblAmount, 0) = 0
    BEGIN
        SELECT 'Error: Enter an amount.' AS Result, NULL AS EntryId;
        RETURN;
    END
    IF @strKind = 'Opening' AND EXISTS (SELECT 1 FROM tblFrankingEntry WHERE iUserID = @iUserId AND strKind = 'Opening')
    BEGIN
        SELECT 'Error: The opening balance has already been entered. Delete it to enter a new one.' AS Result, NULL AS EntryId;
        RETURN;
    END
    IF @strDescription = ''
        SET @strDescription = @strKind;

    INSERT INTO tblFrankingEntry (iUserID, dtDate, strKind, strDescription, dblAmount)
    VALUES (@iUserId, @dtDate, @strKind, @strDescription, @dblAmount);
    SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS EntryId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteFrankingEntry]
(
    @iUserId  INT,
    @iEntryID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        16-Oct-2026
-- Description: Deletes a franking account entry not dated in a locked period.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    DECLARE @dtDate DATE = (SELECT dtDate FROM tblFrankingEntry WHERE iEntryID = @iEntryID AND iUserID = @iUserId);
    IF @dtDate IS NULL
    BEGIN
        SELECT 'Error: That entry was not found in this portfolio.' AS Result;
        RETURN;
    END
    IF dbo.fnLockedTo(@iUserId) >= @dtDate
    BEGIN
        SELECT 'Error: That entry is dated in a locked period.' AS Result;
        RETURN;
    END
    DELETE FROM tblFrankingEntry WHERE iEntryID = @iEntryID AND iUserID = @iUserId;
    SELECT 'Success' AS Result;
END
GO
