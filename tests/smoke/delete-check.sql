-- Run by build-database.sh after the demo portfolio is added. Deletes the demo data and then the whole demo account
-- with the delete procs, inside a transaction that is rolled back, and fails if anything goes wrong or if any row
-- for the demo portfolio is left behind. So a table added later that the delete procs can't clear fails CI here.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @id INT = (SELECT MIN(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');
DECLARE @r TABLE (Result NVARCHAR(4000), Deleted INT NULL, Kept INT NULL);
DECLARE @d TABLE (Result NVARCHAR(4000));

BEGIN TRANSACTION;

EXEC dbo.AddNewPortfolio @id, 'Second portfolio', 'Company';
DECLARE @second INT = (SELECT MAX(iUserId) FROM tblUsers WHERE strLoginEmail = 'demo@example.com');

-- Deleting trades forgets the Navexa sync's record of the trades it added (so a re-sync adds them again), and only those
INSERT INTO tblNavexaSyncItem (iUserId, strNavexaType, strNavexaId, strStatus, strKind)
VALUES (@id, 'trade', 'smoke-1', 'Imported', 'Trade'), (@id, 'dividend', 'smoke-2', 'Imported', 'Payout');
INSERT INTO @d EXEC dbo.uspDeletePortfolioData @id, @id, 'Trades';
IF NOT EXISTS (SELECT 1 FROM @d WHERE Result = 'Deleted')
    THROW 50000, 'uspDeletePortfolioData Trades failed', 1;
IF EXISTS (SELECT 1 FROM tblNavexaSyncItem WHERE iUserId = @id AND strNavexaId = 'smoke-1')
   OR NOT EXISTS (SELECT 1 FROM tblNavexaSyncItem WHERE iUserId = @id AND strNavexaId = 'smoke-2')
    THROW 50000, 'Deleting trades should forget the Navexa sync records of trades only', 1;
DELETE FROM @d;

INSERT INTO @d EXEC dbo.uspDeletePortfolioData @id, @id, 'All';
IF NOT EXISTS (SELECT 1 FROM @d WHERE Result = 'Deleted')
    THROW 50000, 'uspDeletePortfolioData All failed', 1;
IF EXISTS (SELECT 1 FROM tblShareTrades WHERE iUserId = @id) OR EXISTS (SELECT 1 FROM tblJournal WHERE iUserID = @id)
    THROW 50000, 'uspDeletePortfolioData All left trades or journals', 1;

INSERT INTO @r EXEC dbo.uspDeleteLogin @id;
IF NOT EXISTS (SELECT 1 FROM @r WHERE Result = 'Account Deleted' AND Deleted = 2)
    THROW 50000, 'uspDeleteLogin failed', 1;

DECLARE @sql NVARCHAR(MAX) = N'', @left NVARCHAR(MAX);
SELECT @sql += N'UNION ALL SELECT ''' + T.name + N''' FROM dbo.' + QUOTENAME(T.name) + N' WHERE ' + QUOTENAME(C.name) + N' IN (@a, @b) '
FROM sys.tables T JOIN sys.columns C ON C.object_id = T.object_id
WHERE T.is_ms_shipped = 0 AND LOWER(C.name) IN ('iuserid', 'iportfolioid', 'iloginid');
SET @sql = N'SELECT @left = STRING_AGG(t, '', '') FROM (SELECT DISTINCT t FROM (' + STUFF(@sql, 1, 10, N'') + N') X(t)) Y';
EXEC sp_executesql @sql, N'@a INT, @b INT, @left NVARCHAR(MAX) OUTPUT', @a = @id, @b = @second, @left = @left OUTPUT;
IF @left IS NOT NULL
BEGIN
    DECLARE @msg NVARCHAR(2000) = N'Deleting the demo account left rows in: ' + @left;
    THROW 50000, @msg, 1;
END

ROLLBACK TRANSACTION;
PRINT 'Delete check passed: the demo account deleted cleanly (rolled back).';
