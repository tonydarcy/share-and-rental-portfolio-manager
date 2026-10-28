-- =============================================
-- Keep the database open between visits. With AUTO_CLOSE on (some hosting control panels turn it on), SQL Server
-- shuts the database whenever nobody is connected and throws away its cached plans and pages. The next visitor
-- then waits while it re-reads the share price table from disk, which can take longer than the 30-second timeout.
-- Run it in the site's own database. Safe to run twice.
-- =============================================
IF DATABASEPROPERTYEX(DB_NAME(), 'IsAutoClose') = 1
    ALTER DATABASE CURRENT SET AUTO_CLOSE OFF;
GO
SELECT DB_NAME() AS [Database], DATABASEPROPERTYEX(DB_NAME(), 'IsAutoClose') AS [AutoCloseOn];
GO
