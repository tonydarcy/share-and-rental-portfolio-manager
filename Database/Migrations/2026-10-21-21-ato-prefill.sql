-- ATO prefill import (roadmap 36): the figures from the ATO's prefill report for a client and year,
-- kept to compare with the ledger. Safe to run twice. No USE line: pick your database in SSMS first.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.tblPrefillItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblPrefillItems (
        iItemID      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblPrefillItems PRIMARY KEY,
        iUserID      INT NOT NULL,
        iYear        INT NOT NULL,
        strCategory  VARCHAR(30) NOT NULL,
        strPayer     NVARCHAR(200) NULL,
        dblAmount    DECIMAL(19,4) NOT NULL,
        dtmImported  DATETIME NOT NULL CONSTRAINT DF_tblPrefillItems_Imported DEFAULT (GETDATE())
    );
    CREATE INDEX IX_tblPrefillItems_UserYear ON dbo.tblPrefillItems (iUserID, iYear);
END
GO

-- Replaces the year's items with <i c="Interest" p="Bank name" v="12.34"/> elements; an empty list clears them
CREATE OR ALTER PROCEDURE dbo.uspReplacePrefillItems @iUserID INT, @iYear INT, @xmlItems XML
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRAN;
    DELETE FROM tblPrefillItems WHERE iUserID = @iUserID AND iYear = @iYear;
    INSERT INTO tblPrefillItems (iUserID, iYear, strCategory, strPayer, dblAmount)
    SELECT @iUserID, @iYear, X.n.value('@c', 'varchar(30)'), NULLIF(X.n.value('@p', 'nvarchar(200)'), ''), X.n.value('@v', 'decimal(19,4)')
    FROM @xmlItems.nodes('/i') X(n)
    WHERE ISNULL(X.n.value('@c', 'varchar(30)'), '') <> '';
    COMMIT;
    SELECT 'Success';
END
GO
