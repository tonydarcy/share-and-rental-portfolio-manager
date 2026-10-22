-- =============================================
-- Migration: bank allocation rules
-- Date:      15-Oct-2026
-- Run this against your Share Portfolio Manager database BEFORE deploying the site build with
-- Accounting > Allocation rules. It only adds a table and procedures.
-- Safe to run more than once.
--
-- What it changes:
--   * tblAllocationRule (new): "bank lines containing this text go to that
--     account", per portfolio, optionally only for deposits or withdrawals.
--     The site applies them with uspAllocateSuspenseTransaction, as if the
--     lines were assigned by hand.
--   * uspGetAllocationRules, uspSaveAllocationRule, uspDeleteAllocationRule (new).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblAllocationRule', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblAllocationRule (
        iRuleID        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblAllocationRule PRIMARY KEY,
        iUserID        INT NOT NULL,
        strContains    VARCHAR(200) NOT NULL,
        strDirection   VARCHAR(10) NOT NULL CONSTRAINT DF_tblAllocationRule_Direction DEFAULT ('Any'),  -- Any, Deposit, Withdrawal
        iAccountID     INT NOT NULL,
        strDescription VARCHAR(500) NULL,   -- the allocation's description; NULL keeps the bank's
        iSortOrder     INT NOT NULL CONSTRAINT DF_tblAllocationRule_Sort DEFAULT (0),
        dtmCreatedDate DATETIME NOT NULL CONSTRAINT DF_tblAllocationRule_Created DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblAllocationRule_User ON dbo.tblAllocationRule (iUserID, iSortOrder);
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspGetAllocationRules]
(
    @iUserId INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: The portfolio's allocation rules in the order they are tried,
--              with the target account's number and name.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    SELECT R.iRuleID, R.strContains, R.strDirection, R.iAccountID, R.strDescription, R.iSortOrder,
           A.iAccountNumber, A.strAccountName
    FROM tblAllocationRule R
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = R.iAccountID
    WHERE R.iUserID = @iUserId
    ORDER BY R.iSortOrder, R.iRuleID;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspSaveAllocationRule]
(
    @iUserId        INT,
    @iRuleID        INT = NULL,          -- NULL adds a rule
    @strContains    VARCHAR(200),
    @strDirection   VARCHAR(10),
    @iAccountID     INT,
    @strDescription VARCHAR(500) = NULL,
    @iSortOrder     INT = NULL           -- NULL: last (new) or unchanged (existing)
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Adds or changes one of the portfolio's allocation rules. The
--              account must be a postable account of the portfolio (or a
--              system one) and not Bank Import Suspense.
--              Returns one row: Result = 'Success', RuleId; or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;

    SET @strContains = LTRIM(RTRIM(ISNULL(@strContains, '')));
    SET @strDescription = NULLIF(LTRIM(RTRIM(ISNULL(@strDescription, ''))), '');

    IF @strContains = ''
    BEGIN
        SELECT 'Error: Enter the text to look for.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF ISNULL(@strDirection, '') NOT IN ('Any', 'Deposit', 'Withdrawal')
    BEGIN
        SELECT 'Error: Choose deposits, withdrawals or both.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts
                   WHERE iAccountID = @iAccountID AND (iUserID = @iUserId OR iUserID = 0)
                     AND ISNULL(bIsPostable, 1) = 1 AND strAccountName <> 'Bank Import Suspense')
    BEGIN
        SELECT 'Error: Choose an account from the list.' AS Result, NULL AS RuleId;
        RETURN;
    END

    IF @iRuleID IS NULL
    BEGIN
        INSERT INTO tblAllocationRule (iUserID, strContains, strDirection, iAccountID, strDescription, iSortOrder)
        VALUES (@iUserId, @strContains, @strDirection, @iAccountID, @strDescription,
                ISNULL(@iSortOrder, (SELECT ISNULL(MAX(iSortOrder), 0) + 10 FROM tblAllocationRule WHERE iUserID = @iUserId)));
        SELECT 'Success' AS Result, CAST(SCOPE_IDENTITY() AS INT) AS RuleId;
        RETURN;
    END

    UPDATE tblAllocationRule
    SET strContains = @strContains,
        strDirection = @strDirection,
        iAccountID = @iAccountID,
        strDescription = @strDescription,
        iSortOrder = ISNULL(@iSortOrder, iSortOrder)
    WHERE iRuleID = @iRuleID AND iUserID = @iUserId;

    IF @@ROWCOUNT = 0
        SELECT 'Error: That rule was not found in this portfolio.' AS Result, NULL AS RuleId;
    ELSE
        SELECT 'Success' AS Result, @iRuleID AS RuleId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[uspDeleteAllocationRule]
(
    @iUserId INT,
    @iRuleID INT
)
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        15-Oct-2026
-- Description: Deletes one of the portfolio's allocation rules. Lines already
--              allocated by it stay as they are.
--              Returns one row: Result = 'Success' or 'Error: ...'.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    DELETE FROM tblAllocationRule WHERE iRuleID = @iRuleID AND iUserID = @iUserId;
    IF @@ROWCOUNT = 0
        SELECT 'Error: That rule was not found in this portfolio.' AS Result;
    ELSE
        SELECT 'Success' AS Result;
END
GO
