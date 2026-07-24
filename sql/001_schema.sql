-- SlotSmith.Api — initial schema.
-- SQL Server. Run against a dedicated database, e.g. SlotSmith.
-- Matches Dapper-first convention used across Mark's other projects (no EF Core).

CREATE TABLE dbo.Staff (
    StaffId       INT IDENTITY(1,1) PRIMARY KEY,
    DisplayName   NVARCHAR(100)   NOT NULL,
    Email         NVARCHAR(200)   NULL,
    PhotoUrl      NVARCHAR(400)   NULL,
    Bio           NVARCHAR(500)   NULL,
    SortOrder     INT             NOT NULL DEFAULT 0,
    IsActive      BIT             NOT NULL DEFAULT 1,
    CreatedAt     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.ServiceCategory (
    CategoryId    INT IDENTITY(1,1) PRIMARY KEY,
    Name          NVARCHAR(100)   NOT NULL,
    SortOrder     INT             NOT NULL DEFAULT 0
);

CREATE TABLE dbo.Service (
    ServiceId       INT IDENTITY(1,1) PRIMARY KEY,
    CategoryId      INT             NOT NULL REFERENCES dbo.ServiceCategory(CategoryId),
    Name            NVARCHAR(200)   NOT NULL,
    DescriptionText NVARCHAR(1000)  NULL,
    DurationMinutes INT             NOT NULL,
    PriceCents      INT             NOT NULL,
    PriceIsFrom     BIT             NOT NULL DEFAULT 0,   -- true => show "from $X" like Fresha does for variable-length services
    SortOrder       INT             NOT NULL DEFAULT 0,
    IsActive        BIT             NOT NULL DEFAULT 1
);

-- Which staff can perform which service. Absence of a row = staff doesn't offer it.
-- Overrides let a senior stylist charge more for the same service without a duplicate Service row.
CREATE TABLE dbo.StaffService (
    StaffId                   INT NOT NULL REFERENCES dbo.Staff(StaffId),
    ServiceId                 INT NOT NULL REFERENCES dbo.Service(ServiceId),
    PriceCentsOverride        INT NULL,
    DurationMinutesOverride   INT NULL,
    PRIMARY KEY (StaffId, ServiceId)
);

CREATE TABLE dbo.Customer (
    CustomerId    INT IDENTITY(1,1) PRIMARY KEY,
    Name          NVARCHAR(200)   NOT NULL,
    Email         NVARCHAR(200)   NOT NULL,
    Phone         NVARCHAR(30)    NULL,
    CreatedAt     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Booking (
    BookingId          INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId         INT             NOT NULL REFERENCES dbo.Customer(CustomerId),
    StaffId            INT             NOT NULL REFERENCES dbo.Staff(StaffId),
    StartUtc           DATETIME2       NOT NULL,
    EndUtc              DATETIME2       NOT NULL,
    Status             NVARCHAR(20)    NOT NULL DEFAULT 'Confirmed',   -- Confirmed | Cancelled
    CalendarProvider   NVARCHAR(20)    NULL,                          -- Google | Microsoft | NULL (staff not connected)
    CalendarEventId    NVARCHAR(200)   NULL,
    Notes              NVARCHAR(500)   NULL,
    ManageToken         NVARCHAR(64)   NOT NULL,   -- opaque random token; "manage my booking" links use this instead of login
    CreatedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Booking_ManageToken UNIQUE (ManageToken)
);

CREATE TABLE dbo.BookingItem (
    BookingItemId   INT IDENTITY(1,1) PRIMARY KEY,
    BookingId       INT NOT NULL REFERENCES dbo.Booking(BookingId),
    ServiceId       INT NOT NULL REFERENCES dbo.Service(ServiceId),
    PriceCents      INT NOT NULL,
    DurationMinutes INT NOT NULL
);

-- One row per staff member per connected calendar provider.
-- Tokens are encrypted at rest via ASP.NET Core Data Protection before insert.
CREATE TABLE dbo.CalendarConnection (
    CalendarConnectionId    INT IDENTITY(1,1) PRIMARY KEY,
    StaffId                 INT           NOT NULL REFERENCES dbo.Staff(StaffId),
    Provider                NVARCHAR(20)  NOT NULL,   -- Google | Microsoft
    ExternalAccountEmail    NVARCHAR(200) NULL,
    AccessTokenEncrypted    NVARCHAR(MAX) NOT NULL,
    RefreshTokenEncrypted   NVARCHAR(MAX) NOT NULL,
    TokenExpiresUtc         DATETIME2     NOT NULL,
    ConnectedAt             DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CalendarId              NVARCHAR(400) NULL,   -- which of the account's calendars to use; NULL = provider's default until picked
    CONSTRAINT UQ_CalendarConnection_Staff_Provider UNIQUE (StaffId, Provider)
);

-- Simple weekly hours. NULL open/close = closed that day.
CREATE TABLE dbo.BusinessHours (
    DayOfWeek   TINYINT PRIMARY KEY,   -- 0=Sunday .. 6=Saturday
    OpenTime    TIME    NULL,
    CloseTime   TIME    NULL
);

-- One row per price change to any service, whether from a single manual edit in
-- services-admin.html ('Manual') or a bulk CPI rise ('CPI'). Doubles as the "once a year" gate
-- on the CPI rise: the API refuses to apply another one within 365 days of the most recent
-- ChangedAtUtc where ChangeType = 'CPI'.
CREATE TABLE dbo.ServicePriceHistory (
    ServicePriceHistoryId INT IDENTITY(1,1) PRIMARY KEY,
    ServiceId       INT             NOT NULL REFERENCES dbo.Service(ServiceId),
    OldPriceCents   INT             NOT NULL,
    NewPriceCents   INT             NOT NULL,
    ChangeType      NVARCHAR(20)    NOT NULL,   -- 'Manual' | 'CPI'
    ChangedAtUtc    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

-- A stylist unavailable for a date range (e.g. taking a week off). Blocks new bookings/
-- reschedules for that range (fed into AvailabilityEngine as another busy-interval source) and
-- drives the "please reschedule" email sent to anyone already booked in that window.
CREATE TABLE dbo.StaffTimeOff (
    TimeOffId         INT           IDENTITY(1,1) PRIMARY KEY,
    StaffId           INT           NOT NULL REFERENCES dbo.Staff(StaffId),
    StartUtc          DATETIME2     NOT NULL,
    EndUtc            DATETIME2     NOT NULL,
    Reason            NVARCHAR(200) NULL,
    CalendarProvider  NVARCHAR(20)  NULL,   -- Google | Microsoft | NULL (staff not connected)
    CalendarEventId   NVARCHAR(200) NULL,   -- the "day off" block on the stylist's calendar, if any
    CreatedAt         DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
