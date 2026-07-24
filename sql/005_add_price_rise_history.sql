-- Adds the table that backs both the full price-change audit trail and the "once a year" gate
-- on the bulk CPI price rise in services-admin.html. Logs every price change to any service —
-- manual edits and CPI bulk rises alike (ChangeType distinguishes them). Run against a database
-- created before this existed.

CREATE TABLE dbo.ServicePriceHistory (
    ServicePriceHistoryId INT IDENTITY(1,1) PRIMARY KEY,
    ServiceId       INT             NOT NULL REFERENCES dbo.Service(ServiceId),
    OldPriceCents   INT             NOT NULL,
    NewPriceCents   INT             NOT NULL,
    ChangeType      NVARCHAR(20)    NOT NULL,   -- 'Manual' | 'CPI'
    ChangedAtUtc    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
