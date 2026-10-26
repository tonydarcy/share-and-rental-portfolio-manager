-- =============================================
-- Migration: more of the BAS (cash basis, PAYG instalments and the other labels)
-- Date:      26-Oct-2026
-- Run this against your Share Portfolio Manager database before deploying the build with cash-basis GST and the full
-- activity statement. Run it after 2026-10-21-26-invoice-books.sql (and 2026-10-21-25-bills.sql). Safe to run more than once.
--
-- What it changes:
--   * tblBasSettings: whether GST is reported on a cash basis, and how PAYG instalments are worked out (an amount the
--     ATO gives, or instalment income times the ATO's rate).
--   * tblBasLabels: amounts entered for one period's other labels (W1 to W4, T1, T2, T7, T9, F1, 1C to 1F, 7C, 7D...).
--   * New: uspSaveBasSettings, uspSaveBasLabel.
--   * New: uspGetSalesTaxCashLines: the taxed lines of invoices and bills by when they were paid, each scaled to the part
--     paid that day (a credit note applied counts as paid on the day it was applied).
-- =============================================
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('dbo.tblSalesTaxSetup', 'U') IS NULL OR OBJECT_ID('dbo.tblInvoicePayments', 'U') IS NULL OR OBJECT_ID('dbo.tblBillPayments', 'U') IS NULL
BEGIN
    RAISERROR('Stopped: run 2026-10-17-sales-tax.sql, 2026-10-18-invoicing.sql and 2026-10-21-25-bills.sql first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END
GO

IF OBJECT_ID('dbo.tblBasSettings', 'U') IS NULL
    CREATE TABLE dbo.tblBasSettings (
        iUserID        INT NOT NULL CONSTRAINT PK_tblBasSettings PRIMARY KEY,
        bCashBasis     BIT NOT NULL CONSTRAINT DF_tblBasSettings_Cash DEFAULT (0),
        strPaygMethod  VARCHAR(10) NOT NULL CONSTRAINT DF_tblBasSettings_Payg DEFAULT ('None'),   -- None, Amount or Rate
        dblPaygAmount  DECIMAL(19,2) NULL,     -- option 1: the instalment amount on the ATO's statement (T7)
        dblPaygRate    DECIMAL(9,4) NULL,      -- option 2: the ATO's instalment rate, as a percentage (T2)
        dtmUpdated     DATETIME NOT NULL CONSTRAINT DF_tblBasSettings_Updated DEFAULT (GETDATE())
    );
GO

IF OBJECT_ID('dbo.tblBasLabels', 'U') IS NULL
    CREATE TABLE dbo.tblBasLabels (
        iUserID       INT NOT NULL,
        dtmPeriodFrom DATE NOT NULL,
        dtmPeriodTo   DATE NOT NULL,
        strLabel      VARCHAR(10) NOT NULL,
        dblAmount     DECIMAL(19,4) NOT NULL,
        CONSTRAINT PK_tblBasLabels PRIMARY KEY (iUserID, dtmPeriodFrom, strLabel)
    );
GO

CREATE OR ALTER PROCEDURE dbo.uspSaveBasSettings
    @iUserID       INT,
    @bCashBasis    BIT,
    @strPaygMethod VARCHAR(10),
    @dblPaygAmount DECIMAL(19,2) = NULL,
    @dblPaygRate   DECIMAL(9,4) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF ISNULL(@strPaygMethod, '') NOT IN ('None', 'Amount', 'Rate')
    BEGIN
        SELECT 'Error: Choose how PAYG instalments are worked out.' AS Result;
        RETURN;
    END
    IF @dblPaygAmount < 0 OR @dblPaygRate < 0 OR @dblPaygRate > 100
    BEGIN
        SELECT 'Error: The instalment amount and rate can''t be negative, and the rate is a percentage.' AS Result;
        RETURN;
    END
    IF EXISTS (SELECT 1 FROM tblBasSettings WHERE iUserID = @iUserID)
        UPDATE tblBasSettings SET bCashBasis = @bCashBasis, strPaygMethod = @strPaygMethod, dblPaygAmount = @dblPaygAmount,
               dblPaygRate = @dblPaygRate, dtmUpdated = GETDATE()
        WHERE iUserID = @iUserID;
    ELSE
        INSERT tblBasSettings (iUserID, bCashBasis, strPaygMethod, dblPaygAmount, dblPaygRate)
        VALUES (@iUserID, @bCashBasis, @strPaygMethod, @dblPaygAmount, @dblPaygRate);
    SELECT 'Success' AS Result;
END
GO

-- Saves one label's amount for a period; a NULL amount removes it (back to the worked-out figure, if any).
CREATE OR ALTER PROCEDURE dbo.uspSaveBasLabel
    @iUserID   INT,
    @dtFrom    DATE,
    @dtTo      DATE,
    @strLabel  VARCHAR(10),
    @dblAmount DECIMAL(19,4) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @dtFrom IS NULL OR @dtTo IS NULL OR @dtTo < @dtFrom OR ISNULL(@strLabel, '') = ''
    BEGIN
        SELECT 'Error: Choose the period and label.' AS Result;
        RETURN;
    END
    IF dbo.fnLockedTo(@iUserID) >= @dtTo
    BEGIN
        SELECT 'Error: This period is locked, so its activity statement can''t change.' AS Result;
        RETURN;
    END
    DELETE FROM tblBasLabels WHERE iUserID = @iUserID AND dtmPeriodFrom = @dtFrom AND strLabel = @strLabel;
    IF @dblAmount IS NOT NULL
        INSERT tblBasLabels (iUserID, dtmPeriodFrom, dtmPeriodTo, strLabel, dblAmount) VALUES (@iUserID, @dtFrom, @dtTo, @strLabel, @dblAmount);
    SELECT 'Success' AS Result;
END
GO

CREATE OR ALTER PROCEDURE dbo.uspGetSalesTaxCashLines
    @iUserId INT,
    @dtFrom  DATE,
    @dtTo    DATE
AS
-- =============================================
-- Author:      Tony Darcy & Claude
-- Date:        26-Oct-2026
-- Description: For cash-basis GST: each payment (or applied credit) in the period on
--              an approved invoice or bill, with the document's taxed journal lines
--              scaled by the part of its total paid. Same columns as
--              uspGetSalesTaxLines, dated on the payment day.
-- =============================================
BEGIN
    SET NOCOUNT ON;
    WITH Paid AS (
        SELECT I.iJournalID, P.dtmPaymentDate, P.dblAmount / NULLIF(I.dblTotal, 0) AS dblPart,
               'Invoice' AS strSourceModule, 'Payment ' + CONVERT(VARCHAR(10), P.dtmPaymentDate, 23) + ' on invoice ' + I.strInvoiceNumber AS strNote
        FROM tblInvoicePayments P JOIN tblInvoices I ON I.iInvoiceID = P.iInvoiceID
        WHERE I.iUserID = @iUserId AND I.strStatus = 'Approved' AND I.iJournalID IS NOT NULL
          AND P.dtmPaymentDate >= @dtFrom AND P.dtmPaymentDate <= @dtTo
        UNION ALL
        SELECT B.iJournalID, P.dtmPaymentDate, P.dblAmount / NULLIF(B.dblTotal, 0),
               'Bill', 'Payment ' + CONVERT(VARCHAR(10), P.dtmPaymentDate, 23) + ' on bill ' + ISNULL(B.strBillNumber, CAST(B.iBillID AS VARCHAR(12)))
        FROM tblBillPayments P JOIN tblBills B ON B.iBillID = P.iBillID
        WHERE B.iUserID = @iUserId AND B.strStatus = 'Approved' AND B.iJournalID IS NOT NULL
          AND P.dtmPaymentDate >= @dtFrom AND P.dtmPaymentDate <= @dtTo
    )
    SELECT J.iJournalID, CAST(X.dtmPaymentDate AS DATETIME) AS dtmTransactionDate, X.strNote + ' - ' + J.strDescription AS strJournalDescription,
           X.strSourceModule, JL.iJournalLineID, JL.strDescription, JL.iTaxCodeID, A.iAccountNumber, A.strAccountName,
           ROUND((ISNULL(JL.dblDebit, 0) - ISNULL(JL.dblCredit, 0)) * X.dblPart, 2) AS Net,
           ROUND(CASE WHEN ISNULL(JL.dblCredit, 0) > 0 THEN -ISNULL(JL.dblTaxAmount, 0) ELSE ISNULL(JL.dblTaxAmount, 0) END * X.dblPart, 2) AS Tax
    FROM Paid X
    JOIN tblJournal J ON J.iJournalID = X.iJournalID AND J.iUserID = @iUserId
    JOIN tblJournalLines JL ON JL.iJournalID = J.iJournalID
    JOIN tblTaxCodes T ON T.iTaxCodeID = JL.iTaxCodeID AND T.iUserID = J.iUserID
    LEFT JOIN tblChartOfAccounts A ON A.iAccountID = JL.iAccountID
    WHERE JL.bIsTaxLine = 0 AND X.dblPart IS NOT NULL
    ORDER BY X.dtmPaymentDate, J.iJournalID, JL.iJournalLineID;
END
GO

PRINT 'BAS extras migration done.';
GO
