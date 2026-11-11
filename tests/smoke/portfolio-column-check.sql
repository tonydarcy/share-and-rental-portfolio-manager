-- Every row of a table kept in step by a trg<table>Portfolio trigger (2026-11-11-portfolio-id-on-child-tables.sql)
-- must carry its portfolio after the demo data and the delete/convert checks have run.
SET NOCOUNT ON;
DECLARE @sql NVARCHAR(MAX) = N'', @bad NVARCHAR(MAX);
SELECT @sql += CASE WHEN @sql = N'' THEN N'' ELSE N' UNION ALL ' END
             + N'SELECT ''' + t.name + N''' AS strTable FROM dbo.' + t.name + N' WHERE iUserID IS NULL'
FROM sys.triggers tr JOIN sys.tables t ON t.object_id = tr.parent_id
WHERE tr.name = N'trg' + SUBSTRING(t.name, 4, 200) + N'Portfolio';
IF @sql = N'' THROW 50000, 'No trg<table>Portfolio triggers found: did 2026-11-11-portfolio-id-on-child-tables.sql run?', 1;
CREATE TABLE #bad (strTable SYSNAME);
INSERT INTO #bad EXEC (@sql);
SELECT TOP 1 @bad = strTable FROM #bad;  -- (no STRING_AGG: live databases run at compatibility level 120)
IF @bad IS NOT NULL
BEGIN
    SET @bad = N'Rows without a portfolio (iUserID) in: ' + @bad;
    THROW 50000, @bad, 1;
END
