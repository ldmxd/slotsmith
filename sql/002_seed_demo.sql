-- Demo seed data modelled on ATSI Hair Supplies (Brookvale) for the Fresha-replacement pitch.
-- Staff names and a handful of real service names/prices are taken from their public Fresha
-- listing (as of July 2026); the rest is representative placeholder data. Before using this for
-- a real demo, confirm the full price list with the salon — this is NOT their complete menu
-- (they list 51 services on Fresha; only a representative slice is seeded here).

SET IDENTITY_INSERT dbo.Staff ON;
INSERT INTO dbo.Staff (StaffId, DisplayName, SortOrder) VALUES
    (1, 'Angelo',  1),
    (2, 'Tanya',   2),
    (3, 'Caroline',3),
    (4, 'Zuzana',  4);
SET IDENTITY_INSERT dbo.Staff OFF;

INSERT INTO dbo.BusinessHours (DayOfWeek, OpenTime, CloseTime) VALUES
    (0, NULL,     NULL),      -- Sunday: closed
    (1, NULL,     NULL),      -- Monday: closed
    (2, '09:00', '17:00'),    -- Tuesday
    (3, '09:00', '17:00'),    -- Wednesday
    (4, '09:00', '17:00'),    -- Thursday
    (5, '09:00', '17:00'),    -- Friday
    (6, '08:30', '16:00');    -- Saturday

SET IDENTITY_INSERT dbo.ServiceCategory ON;
INSERT INTO dbo.ServiceCategory (CategoryId, Name, SortOrder) VALUES
    (1, 'Hair Cut',         1),
    (2, 'Shampoo & Blowdry',2),
    (3, 'Colour',           3),
    (4, 'Foils & Highlights',4),
    (5, 'Treatments',       5),
    (6, 'Hair Ups',         6),
    (7, 'Miscellaneous',    7);
SET IDENTITY_INSERT dbo.ServiceCategory OFF;

SET IDENTITY_INSERT dbo.Service ON;
INSERT INTO dbo.Service (ServiceId, CategoryId, Name, DurationMinutes, PriceCents, PriceIsFrom, SortOrder) VALUES
    (1, 1, 'Ladies Shampoo Cut Style',   60,  10000, 1, 1),
    (2, 1, 'Mens Shampoo Cut',           45,   6000, 1, 2),
    (3, 1, 'Restyle',                    75,  12000, 1, 3),
    (4, 1, 'Fringe Trim',                15,   2000, 0, 4),
    (5, 2, 'Blowdry',                    45,   5500, 1, 1),
    (6, 2, 'Shampoo & Set',              45,   5000, 1, 2),
    (7, 3, 'Roots Colour 60g-70g',       75,  17000, 1, 1),
    (8, 3, 'Full Head Colour',          120,  22000, 1, 2),
    (9, 3, 'Semi Permanent Colour',      60,  12000, 1, 3),
   (10, 3, 'Tint',                       60,  11000, 1, 4),
   (11, 4, 'Half Head Foils',           105,  18000, 1, 1),
   (12, 4, 'Full Head Foils',           150,  24000, 1, 2),
   (13, 4, 'Balayage',                  180,  28000, 1, 3),
   (14, 5, 'Olaplex Treatment',          30,   4500, 1, 1),
   (15, 5, 'Keratin Treatment',         150,  35000, 1, 2),
   (16, 5, 'Scalp Treatment',            30,   4000, 1, 3),
   (17, 6, 'Formal Hair Up',             60,  12000, 1, 1),
   (18, 6, 'Bridal Trial',               75,  15000, 1, 2),
   (19, 7, 'Miscellaneous',               5,   1000, 0, 1),
   (20, 7, 'Consultation',               15,      0, 0, 2);
SET IDENTITY_INSERT dbo.Service OFF;

-- All four staff offer everything in this demo seed. In reality you'd curate per stylist
-- (e.g. only senior stylists doing balayage/keratin) via targeted inserts instead of a cross join.
INSERT INTO dbo.StaffService (StaffId, ServiceId)
SELECT s.StaffId, sv.ServiceId
FROM dbo.Staff s
CROSS JOIN dbo.Service sv;
