-- Phones signed in to the app that want alerts as notifications (Firebase Cloud Messaging tokens), one row per phone.
-- Read and written by PushDeviceService (src/PortfolioManager.Core/Alerts/PushDevices.cs). Safe to run twice.
IF OBJECT_ID('dbo.tblPushDevices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblPushDevices
    (
        iPushDeviceID INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_tblPushDevices PRIMARY KEY,
        iLoginID      INT NOT NULL,
        strPlatform   VARCHAR(10) NOT NULL,
        strToken      VARCHAR(400) NOT NULL,
        strDeviceName NVARCHAR(100) NULL,
        dtmAdded      DATETIME2(0) NOT NULL,
        dtmLastSeen   DATETIME2(0) NOT NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_tblPushDevices_Token' AND object_id = OBJECT_ID('dbo.tblPushDevices'))
    CREATE UNIQUE INDEX UX_tblPushDevices_Token ON dbo.tblPushDevices (strToken);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tblPushDevices_Login' AND object_id = OBJECT_ID('dbo.tblPushDevices'))
    CREATE INDEX IX_tblPushDevices_Login ON dbo.tblPushDevices (iLoginID, dtmLastSeen);
GO
