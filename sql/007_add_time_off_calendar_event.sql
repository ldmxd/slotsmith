-- Lets a time-off entry carry its own calendar event — a visible "day off" block on the
-- stylist's connected calendar, separate from any existing bookings that happen to fall inside
-- the range (those are deliberately left alone; the customer reschedules them via the emailed
-- manage-booking link, see Program.cs). Run against a database created before this existed.

ALTER TABLE dbo.StaffTimeOff ADD CalendarProvider NVARCHAR(20) NULL;
ALTER TABLE dbo.StaffTimeOff ADD CalendarEventId NVARCHAR(200) NULL;
