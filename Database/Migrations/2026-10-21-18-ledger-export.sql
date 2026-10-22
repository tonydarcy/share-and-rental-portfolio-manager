-- Export to Xero and MYOB (roadmap 33): the account code (and tax rate or code) each ledger account
-- is sent as. Safe to run twice. No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblLedgerExportMap', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblLedgerExportMap (
        iMapID       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblLedgerExportMap PRIMARY KEY,
        iUserID      INT NOT NULL,
        strSystem    VARCHAR(10) NOT NULL,   -- Xero or MYOB
        iAccountID   INT NOT NULL,
        strCode      NVARCHAR(30) NOT NULL,
        strTaxCode   NVARCHAR(50) NULL,
        CONSTRAINT UQ_tblLedgerExportMap UNIQUE (iUserID, strSystem, iAccountID)
    );
END
GO

-- Blank code: removes the mapping, so the account goes out under its own number
CREATE OR ALTER PROCEDURE dbo.uspSaveLedgerExportMap
    @iUserID INT, @strSystem VARCHAR(10), @iAccountID INT, @strCode NVARCHAR(30), @strTaxCode NVARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @strSystem NOT IN ('Xero', 'MYOB')
    BEGIN SELECT 'Choose Xero or MYOB.'; RETURN; END
    IF NOT EXISTS (SELECT 1 FROM tblChartOfAccounts WHERE iAccountID = @iAccountID AND iUserID IN (0, @iUserID))
    BEGIN SELECT 'That account is not in this portfolio.'; RETURN; END

    SET @strCode = LTRIM(RTRIM(@strCode));
    SET @strTaxCode = NULLIF(LTRIM(RTRIM(@strTaxCode)), '');
    IF ISNULL(@strCode, '') = '' AND @strTaxCode IS NULL
        DELETE FROM tblLedgerExportMap WHERE iUserID = @iUserID AND strSystem = @strSystem AND iAccountID = @iAccountID;
    ELSE IF EXISTS (SELECT 1 FROM tblLedgerExportMap WHERE iUserID = @iUserID AND strSystem = @strSystem AND iAccountID = @iAccountID)
        UPDATE tblLedgerExportMap SET strCode = ISNULL(@strCode, ''), strTaxCode = @strTaxCode
        WHERE iUserID = @iUserID AND strSystem = @strSystem AND iAccountID = @iAccountID;
    ELSE
        INSERT INTO tblLedgerExportMap (iUserID, strSystem, iAccountID, strCode, strTaxCode)
        VALUES (@iUserID, @strSystem, @iAccountID, ISNULL(@strCode, ''), @strTaxCode);
    SELECT 'Success';
END
GO
