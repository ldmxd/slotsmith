-- Adds the token customers use to view/reschedule/cancel their own booking from the
-- confirmation email, without needing an account or login. Run against a database created
-- before this existed.
--
-- Existing rows get a random token backfilled via NEWID() so the column can be NOT NULL;
-- those old bookings just won't have had a manage link in their (already-sent) email.

ALTER TABLE dbo.Booking
ADD ManageToken NVARCHAR(64) NULL;

UPDATE dbo.Booking
SET ManageToken = REPLACE(CAST(NEWID() AS NVARCHAR(36)), '-', '') + REPLACE(CAST(NEWID() AS NVARCHAR(36)), '-', '')
WHERE ManageToken IS NULL;

ALTER TABLE dbo.Booking
ALTER COLUMN ManageToken NVARCHAR(64) NOT NULL;

ALTER TABLE dbo.Booking
ADD CONSTRAINT UQ_Booking_ManageToken UNIQUE (ManageToken);
