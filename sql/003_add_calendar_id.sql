-- Adds calendar selection to CalendarConnection — a Google/Outlook account can have multiple
-- calendars, and the stylist's work bookings might not live on their default one. Run this
-- against a database created before this column existed.

ALTER TABLE dbo.CalendarConnection
ADD CalendarId NVARCHAR(400) NULL;
