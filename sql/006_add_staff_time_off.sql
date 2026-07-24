-- Lets a stylist be marked unavailable for a date range (e.g. Angelo taking a week off).
-- Time-off ranges are fed into AvailabilityEngine as another busy-interval source, alongside
-- existing bookings and connected-calendar events, so they block new bookings/reschedules the
-- same way. Run against a database created before this existed.

CREATE TABLE dbo.StaffTimeOff (
    TimeOffId   INT IDENTITY(1,1) PRIMARY KEY,
    StaffId     INT           NOT NULL REFERENCES dbo.Staff(StaffId),
    StartUtc    DATETIME2     NOT NULL,
    EndUtc      DATETIME2     NOT NULL,
    Reason      NVARCHAR(200) NULL,
    CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
