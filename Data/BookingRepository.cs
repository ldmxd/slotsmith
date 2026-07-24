using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using SlotSmith.Api.Models;

namespace SlotSmith.Api.Data;

/// <summary>Thin Dapper data-access layer. Raw SQL by design — matches the Dapper-first
/// convention used across Mark's other projects (no EF Core, no repository-per-entity ceremony
/// beyond what's needed to keep Program.cs readable).</summary>
public class BookingRepository
{
    private readonly string _connStr;
    public BookingRepository(string connStr) => _connStr = connStr;

    private SqlConnection Open() => new(_connStr);

    public async Task<IEnumerable<Staff>> GetActiveStaffAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<Staff>(
            "SELECT StaffId, DisplayName, Email, PhotoUrl, Bio, SortOrder, IsActive FROM dbo.Staff WHERE IsActive = 1 ORDER BY SortOrder");
    }

    // ── Staff admin (add/deactivate stylists, assign services) ─────────────

    public async Task<IEnumerable<Staff>> GetAllStaffForAdminAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<Staff>(
            "SELECT StaffId, DisplayName, Email, PhotoUrl, Bio, SortOrder, IsActive FROM dbo.Staff ORDER BY SortOrder, DisplayName");
    }

    public async Task<int> CreateStaffAsync(string displayName, string? email, string? bio)
    {
        using var conn = Open();
        // New staff go to the end of the display order by default — one more than whatever's
        // currently highest (COALESCE handles the very first row, when MAX(SortOrder) is null).
        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO dbo.Staff (DisplayName, Email, Bio, SortOrder, IsActive)
            OUTPUT INSERTED.StaffId
            VALUES (@displayName, @email, @bio,
                    (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM dbo.Staff), 1)",
            new { displayName, email, bio });
    }

    public async Task UpdateStaffAsync(int staffId, string displayName, string? email, string? bio, bool isActive)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.Staff SET DisplayName = @displayName, Email = @email, Bio = @bio, IsActive = @isActive WHERE StaffId = @staffId",
            new { staffId, displayName, email, bio, isActive });
    }

    public async Task<Staff?> GetStaffByIdAsync(int staffId)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<Staff>(
            "SELECT StaffId, DisplayName, Email, PhotoUrl, Bio, SortOrder, IsActive FROM dbo.Staff WHERE StaffId = @staffId",
            new { staffId });
    }

    public async Task SetStaffPhotoUrlAsync(int staffId, string? photoUrl)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.Staff SET PhotoUrl = @photoUrl WHERE StaffId = @staffId",
            new { staffId, photoUrl });
    }

    public async Task<IEnumerable<int>> GetStaffServiceIdsAsync(int staffId)
    {
        using var conn = Open();
        return await conn.QueryAsync<int>(
            "SELECT ServiceId FROM dbo.StaffService WHERE StaffId = @staffId",
            new { staffId });
    }

    /// <summary>Replaces the full set of services a staff member offers with exactly the given list
    /// (any price/duration overrides on rows that stay get preserved; removed/re-added services
    /// lose their override and fall back to the base Service price/duration — acceptable for how
    /// small and infrequent this admin action is).</summary>
    public async Task SetStaffServicesAsync(int staffId, IReadOnlyList<int> serviceIds)
    {
        using var conn = Open();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM dbo.StaffService WHERE StaffId = @staffId AND ServiceId NOT IN @serviceIds",
            new { staffId, serviceIds = serviceIds.Count > 0 ? serviceIds : new[] { 0 } }, tx);

        foreach (var serviceId in serviceIds)
        {
            await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.StaffService WHERE StaffId = @staffId AND ServiceId = @serviceId)
                    INSERT INTO dbo.StaffService (StaffId, ServiceId) VALUES (@staffId, @serviceId)",
                new { staffId, serviceId }, tx);
        }

        tx.Commit();
    }

    public async Task<IEnumerable<ServiceCategory>> GetCategoriesAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<ServiceCategory>(
            "SELECT CategoryId, Name, SortOrder FROM dbo.ServiceCategory ORDER BY SortOrder");
    }

    public async Task<IEnumerable<Service>> GetActiveServicesAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<Service>(@"
            SELECT ServiceId, CategoryId, Name, DescriptionText, DurationMinutes, PriceCents, PriceIsFrom, SortOrder, IsActive
            FROM dbo.Service
            WHERE IsActive = 1
            ORDER BY CategoryId, SortOrder");
    }

    /// <summary>Staff who can perform every service in the given list, with per-staff price/duration
    /// resolved (override if set, else the base Service value).</summary>
    public async Task<IEnumerable<(int StaffId, int TotalPriceCents, int TotalDurationMinutes)>> GetEligibleStaffAsync(IReadOnlyList<int> serviceIds)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<(int StaffId, int ServiceId, int PriceCents, int DurationMinutes)>(@"
            SELECT ss.StaffId, ss.ServiceId,
                   COALESCE(ss.PriceCentsOverride, sv.PriceCents) AS PriceCents,
                   COALESCE(ss.DurationMinutesOverride, sv.DurationMinutes) AS DurationMinutes
            FROM dbo.StaffService ss
            JOIN dbo.Service sv ON sv.ServiceId = ss.ServiceId
            JOIN dbo.Staff st ON st.StaffId = ss.StaffId AND st.IsActive = 1
            WHERE ss.ServiceId IN @serviceIds",
            new { serviceIds });

        return rows.GroupBy(r => r.StaffId)
            .Where(g => serviceIds.All(id => g.Any(r => r.ServiceId == id))) // staff must offer ALL requested services
            .Select(g => (StaffId: g.Key, TotalPriceCents: g.Sum(r => r.PriceCents), TotalDurationMinutes: g.Sum(r => r.DurationMinutes)));
    }

    public async Task<BusinessHours?> GetBusinessHoursAsync(byte dayOfWeek)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<BusinessHours>(
            "SELECT DayOfWeek, OpenTime, CloseTime FROM dbo.BusinessHours WHERE DayOfWeek = @dayOfWeek",
            new { dayOfWeek });
    }

    /// <param name="excludeBookingId">Pass the booking's own id when re-validating a reschedule,
    /// so its current (pre-move) slot doesn't count as "busy" against itself.</param>
    public async Task<IEnumerable<BusyInterval>> GetExistingBookingsAsync(int staffId, DateTime fromUtc, DateTime toUtc, int? excludeBookingId = null)
    {
        using var conn = Open();
        return await conn.QueryAsync<BusyInterval>(@"
            SELECT StartUtc, EndUtc
            FROM dbo.Booking
            WHERE StaffId = @staffId AND Status = 'Confirmed'
              AND StartUtc < @toUtc AND EndUtc > @fromUtc
              AND (@excludeBookingId IS NULL OR BookingId <> @excludeBookingId)",
            new { staffId, fromUtc, toUtc, excludeBookingId });
    }

    // Explicit column list, in the exact order the CalendarConnection record's constructor expects —
    // see the comment on GetBookingByTokenAsync for why SELECT * is fragile here (positional mapping
    // against physical column order, which ALTER TABLE-appended columns like CalendarId don't control).
    private const string CalendarConnectionColumns = @"
        CalendarConnectionId, StaffId, Provider, ExternalAccountEmail, AccessTokenEncrypted,
        RefreshTokenEncrypted, TokenExpiresUtc, ConnectedAt, CalendarId";

    public async Task<CalendarConnection?> GetCalendarConnectionAsync(int staffId, string provider)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<CalendarConnection>(
            $"SELECT {CalendarConnectionColumns} FROM dbo.CalendarConnection WHERE StaffId = @staffId AND Provider = @provider",
            new { staffId, provider });
    }

    public async Task<IEnumerable<CalendarConnection>> GetCalendarConnectionsForStaffAsync(int staffId)
    {
        using var conn = Open();
        return await conn.QueryAsync<CalendarConnection>(
            $"SELECT {CalendarConnectionColumns} FROM dbo.CalendarConnection WHERE StaffId = @staffId",
            new { staffId });
    }

    /// <summary>Insert or update a connection's tokens/email. Note this deliberately does NOT touch
    /// CalendarId — a token refresh shouldn't reset which calendar the staff member already picked.
    /// Use SetCalendarIdAsync for that.</summary>
    public async Task UpsertCalendarConnectionAsync(CalendarConnection c)
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            MERGE dbo.CalendarConnection AS target
            USING (SELECT @StaffId AS StaffId, @Provider AS Provider) AS src
              ON target.StaffId = src.StaffId AND target.Provider = src.Provider
            WHEN MATCHED THEN UPDATE SET
                ExternalAccountEmail = @ExternalAccountEmail,
                AccessTokenEncrypted = @AccessTokenEncrypted,
                RefreshTokenEncrypted = @RefreshTokenEncrypted,
                TokenExpiresUtc = @TokenExpiresUtc
            WHEN NOT MATCHED THEN INSERT
                (StaffId, Provider, ExternalAccountEmail, AccessTokenEncrypted, RefreshTokenEncrypted, TokenExpiresUtc)
                VALUES (@StaffId, @Provider, @ExternalAccountEmail, @AccessTokenEncrypted, @RefreshTokenEncrypted, @TokenExpiresUtc);",
            c);
    }

    public async Task SetCalendarIdAsync(int staffId, string provider, string calendarId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.CalendarConnection SET CalendarId = @calendarId WHERE StaffId = @staffId AND Provider = @provider",
            new { staffId, provider, calendarId });
    }

    public async Task<IEnumerable<(int ServiceId, int PriceCents, int DurationMinutes)>> GetStaffServiceBreakdownAsync(int staffId, IReadOnlyList<int> serviceIds)
    {
        using var conn = Open();
        return await conn.QueryAsync<(int ServiceId, int PriceCents, int DurationMinutes)>(@"
            SELECT ss.ServiceId,
                   COALESCE(ss.PriceCentsOverride, sv.PriceCents) AS PriceCents,
                   COALESCE(ss.DurationMinutesOverride, sv.DurationMinutes) AS DurationMinutes
            FROM dbo.StaffService ss
            JOIN dbo.Service sv ON sv.ServiceId = ss.ServiceId
            WHERE ss.StaffId = @staffId AND ss.ServiceId IN @serviceIds",
            new { staffId, serviceIds });
    }

    public async Task<int> CreateCustomerAsync(string name, string email, string? phone)
    {
        using var conn = Open();
        var existing = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT CustomerId FROM dbo.Customer WHERE Email = @email", new { email });
        if (existing is not null) return existing.Value;

        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO dbo.Customer (Name, Email, Phone) OUTPUT INSERTED.CustomerId
            VALUES (@name, @email, @phone)",
            new { name, email, phone });
    }

    private static string GenerateManageToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); // 48 hex chars, unguessable

    /// <summary>Returns the new booking's id and its manage token (for the "manage my booking" email link).</summary>
    public async Task<(int BookingId, string ManageToken)> CreateBookingAsync(int customerId, int staffId, DateTime startUtc, DateTime endUtc,
        string? calendarProvider, string? calendarEventId, string? notes, IEnumerable<(int ServiceId, int PriceCents, int DurationMinutes)> items)
    {
        var manageToken = GenerateManageToken();

        using var conn = Open();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var bookingId = await conn.QuerySingleAsync<int>(@"
            INSERT INTO dbo.Booking (CustomerId, StaffId, StartUtc, EndUtc, CalendarProvider, CalendarEventId, Notes, ManageToken)
            OUTPUT INSERTED.BookingId
            VALUES (@customerId, @staffId, @startUtc, @endUtc, @calendarProvider, @calendarEventId, @notes, @manageToken)",
            new { customerId, staffId, startUtc, endUtc, calendarProvider, calendarEventId, notes, manageToken }, tx);

        foreach (var item in items)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO dbo.BookingItem (BookingId, ServiceId, PriceCents, DurationMinutes)
                VALUES (@bookingId, @ServiceId, @PriceCents, @DurationMinutes)",
                new { bookingId, item.ServiceId, item.PriceCents, item.DurationMinutes }, tx);
        }

        tx.Commit();
        return (bookingId, manageToken);
    }

    // ── Staff time off ───────────────────────────────────────────────────────

    // Explicit column list, in the exact order the StaffTimeOff record's constructor expects —
    // same reasoning as CalendarConnectionColumns/GetBookingByTokenAsync: it has several
    // same-typed nullable-string properties (Reason, CalendarProvider, CalendarEventId), so
    // Dapper's positional constructor mapping needs the SELECT list order to match, not whatever
    // order the columns physically sit in (CalendarProvider/CalendarEventId were bolted on later
    // via ALTER TABLE, which always appends at the end).
    private const string StaffTimeOffColumns =
        "TimeOffId, StaffId, StartUtc, EndUtc, Reason, CalendarProvider, CalendarEventId, CreatedAt";

    public async Task<IEnumerable<StaffTimeOff>> GetStaffTimeOffAsync(int staffId)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<StaffTimeOff>(
            $"SELECT {StaffTimeOffColumns} FROM dbo.StaffTimeOff WHERE StaffId = @staffId ORDER BY StartUtc DESC",
            new { staffId });

        // Same Kind=Unspecified issue as Booking's DATETIME2 columns (see GetBookingByTokenAsync).
        return rows.Select(t => t with
        {
            StartUtc = DateTime.SpecifyKind(t.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(t.EndUtc, DateTimeKind.Utc),
            CreatedAt = DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)
        });
    }

    public async Task<StaffTimeOff?> GetStaffTimeOffByIdAsync(int timeOffId)
    {
        using var conn = Open();
        var t = await conn.QueryFirstOrDefaultAsync<StaffTimeOff>(
            $"SELECT {StaffTimeOffColumns} FROM dbo.StaffTimeOff WHERE TimeOffId = @timeOffId",
            new { timeOffId });
        return t is null ? null : t with
        {
            StartUtc = DateTime.SpecifyKind(t.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(t.EndUtc, DateTimeKind.Utc),
            CreatedAt = DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)
        };
    }

    /// <summary>calendarProvider/calendarEventId are the "day off" block created on the stylist's
    /// connected calendar, if they have one — see the time-off endpoint in Program.cs. Both null
    /// if they're not calendar-connected; time off still blocks bookings either way, it just won't
    /// be visible on an external calendar.</summary>
    public async Task<int> CreateStaffTimeOffAsync(
        int staffId, DateTime startUtc, DateTime endUtc, string? reason,
        string? calendarProvider, string? calendarEventId)
    {
        using var conn = Open();
        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO dbo.StaffTimeOff (StaffId, StartUtc, EndUtc, Reason, CalendarProvider, CalendarEventId)
            OUTPUT INSERTED.TimeOffId
            VALUES (@staffId, @startUtc, @endUtc, @reason, @calendarProvider, @calendarEventId)",
            new { staffId, startUtc, endUtc, reason, calendarProvider, calendarEventId });
    }

    public async Task DeleteStaffTimeOffAsync(int timeOffId)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM dbo.StaffTimeOff WHERE TimeOffId = @timeOffId", new { timeOffId });
    }

    /// <summary>Time-off ranges overlapping [fromUtc,toUtc) for a staff member, as BusyIntervals —
    /// fed into the same busy-interval merge as bookings and connected-calendar events in
    /// AvailabilityEngine, so time off blocks new bookings/reschedules exactly like anything else
    /// on the stylist's plate.</summary>
    public async Task<IEnumerable<BusyInterval>> GetStaffTimeOffBusyAsync(int staffId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        return await conn.QueryAsync<BusyInterval>(@"
            SELECT StartUtc, EndUtc
            FROM dbo.StaffTimeOff
            WHERE StaffId = @staffId AND StartUtc < @toUtc AND EndUtc > @fromUtc",
            new { staffId, fromUtc, toUtc });
    }

    /// <summary>Confirmed bookings for a staff member overlapping a range — used to find who needs
    /// a "please reschedule" email when new time off is added on top of existing bookings.</summary>
    public async Task<IEnumerable<Booking>> GetBookingsInRangeAsync(int staffId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        var bookings = await conn.QueryAsync<Booking>(@"
            SELECT BookingId, CustomerId, StaffId, StartUtc, EndUtc, Status,
                   CalendarProvider, CalendarEventId, Notes, ManageToken, CreatedAt
            FROM dbo.Booking
            WHERE StaffId = @staffId AND Status = 'Confirmed'
              AND StartUtc < @toUtc AND EndUtc > @fromUtc",
            new { staffId, fromUtc, toUtc });

        return bookings.Select(b => b with
        {
            StartUtc = DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(b.EndUtc, DateTimeKind.Utc),
            CreatedAt = DateTime.SpecifyKind(b.CreatedAt, DateTimeKind.Utc)
        });
    }

    // ── Admin: search/browse all bookings by client (bookings-admin.html) ───

    // Shared SELECT columns / FROM+JOINs / GROUP BY for both SearchBookingsAsync and
    // GetUpcomingBookingsAsync — only the WHERE, ORDER BY, and (for the latter) a TOP differ
    // between "find this client's bookings" and "what's coming up". Explicit column list, in the
    // exact order BookingSearchRow's constructor expects — same Dapper positional-mapping
    // reasoning as everywhere else in this file (several same-typed string properties here:
    // ManageToken, Status, CustomerName, CustomerEmail, CustomerPhone, StaffName, ServiceNames).
    private const string BookingSearchColumns = @"
        b.BookingId, b.ManageToken, b.Status, b.StartUtc, b.EndUtc,
        c.Name AS CustomerName, c.Email AS CustomerEmail, c.Phone AS CustomerPhone,
        b.StaffId, st.DisplayName AS StaffName,
        STRING_AGG(sv.Name, ', ') WITHIN GROUP (ORDER BY sv.SortOrder) AS ServiceNames,
        SUM(bi.PriceCents) AS TotalPriceCents";

    private const string BookingSearchFromJoins = @"
        FROM dbo.Booking b
        JOIN dbo.Customer c ON c.CustomerId = b.CustomerId
        JOIN dbo.Staff st ON st.StaffId = b.StaffId
        JOIN dbo.BookingItem bi ON bi.BookingId = b.BookingId
        JOIN dbo.Service sv ON sv.ServiceId = bi.ServiceId";

    private const string BookingSearchGroupBy = @"
        GROUP BY b.BookingId, b.ManageToken, b.Status, b.StartUtc, b.EndUtc,
                 c.Name, c.Email, c.Phone, b.StaffId, st.DisplayName";

    private static IEnumerable<BookingSearchRow> FixKinds(IEnumerable<BookingSearchRow> rows) =>
        // Same Kind=Unspecified issue as Booking's DATETIME2 columns (see GetBookingByTokenAsync).
        rows.Select(r => r with
        {
            StartUtc = DateTime.SpecifyKind(r.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(r.EndUtc, DateTimeKind.Utc)
        });

    /// <summary>Bookings whose customer name, email, or phone contains the search text (any
    /// status — cancelled bookings are included so staff can see history, not just what's still
    /// active). Includes each booking's ManageToken so the admin page can hand off to the
    /// existing customer-facing manage-booking.html reschedule/cancel flow rather than
    /// duplicating that logic for an admin-specific path.</summary>
    public async Task<IEnumerable<BookingSearchRow>> SearchBookingsAsync(string query)
    {
        using var conn = Open();
        var pattern = $"%{query}%";
        var rows = await conn.QueryAsync<BookingSearchRow>(
            $@"SELECT {BookingSearchColumns}
            {BookingSearchFromJoins}
            WHERE c.Name LIKE @pattern OR c.Email LIKE @pattern OR c.Phone LIKE @pattern
            {BookingSearchGroupBy}
            ORDER BY b.StartUtc DESC",
            new { pattern });

        return FixKinds(rows);
    }

    /// <summary>The next <paramref name="limit"/> confirmed bookings from now, soonest first — the
    /// default view on bookings-admin.html so staff can find something like "tomorrow's 2pm"
    /// without having to search for it.</summary>
    public async Task<IEnumerable<BookingSearchRow>> GetUpcomingBookingsAsync(int limit)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<BookingSearchRow>(
            $@"SELECT TOP (@limit) {BookingSearchColumns}
            {BookingSearchFromJoins}
            WHERE b.Status = 'Confirmed' AND b.StartUtc >= @nowUtc
            {BookingSearchGroupBy}
            ORDER BY b.StartUtc ASC",
            new { limit, nowUtc = DateTime.UtcNow });

        return FixKinds(rows);
    }

    // ── Manage-my-booking (token-based, no login) ───────────────────────────

    public async Task<Booking?> GetBookingByTokenAsync(string manageToken)
    {
        using var conn = Open();
        // Explicit column list, in the exact order the Booking record's constructor expects.
        // Dapper maps SELECT * positionally when a type has several same-typed properties (several
        // nullable strings here), so it's fragile against physical column order — which itself
        // depends on whether a column was in the original CREATE TABLE or bolted on later via
        // ALTER TABLE (ALTER always appends at the end, regardless of where the record declares it).
        var booking = await conn.QueryFirstOrDefaultAsync<Booking>(@"
            SELECT BookingId, CustomerId, StaffId, StartUtc, EndUtc, Status,
                   CalendarProvider, CalendarEventId, Notes, ManageToken, CreatedAt
            FROM dbo.Booking
            WHERE ManageToken = @manageToken",
            new { manageToken });

        // SQL Server DATETIME2 carries no timezone, so Dapper reads these back as Kind=Unspecified.
        // Serialized to JSON that way, they lose their "Z" suffix — and JS then parses the timestamp
        // as local time instead of UTC, silently shifting it by the browser's UTC offset. These
        // columns are UTC by convention (hence the Utc suffix in their names); tag them explicitly.
        return booking is null ? null : booking with
        {
            StartUtc = DateTime.SpecifyKind(booking.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(booking.EndUtc, DateTimeKind.Utc),
            CreatedAt = DateTime.SpecifyKind(booking.CreatedAt, DateTimeKind.Utc)
        };
    }

    public async Task<Customer?> GetCustomerAsync(int customerId)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<Customer>(
            "SELECT * FROM dbo.Customer WHERE CustomerId = @customerId",
            new { customerId });
    }

    public async Task<IEnumerable<BookingItemRow>> GetBookingItemsAsync(int bookingId)
    {
        using var conn = Open();
        return await conn.QueryAsync<BookingItemRow>(
            "SELECT * FROM dbo.BookingItem WHERE BookingId = @bookingId",
            new { bookingId });
    }

    public async Task UpdateBookingTimeAsync(int bookingId, DateTime startUtc, DateTime endUtc, string? calendarEventId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.Booking SET StartUtc = @startUtc, EndUtc = @endUtc, CalendarEventId = @calendarEventId WHERE BookingId = @bookingId",
            new { bookingId, startUtc, endUtc, calendarEventId });
    }

    public async Task CancelBookingAsync(int bookingId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.Booking SET Status = 'Cancelled' WHERE BookingId = @bookingId",
            new { bookingId });
    }

    // ── Service admin (price/duration editing) ──────────────────────────────

    public async Task<IEnumerable<Service>> GetAllServicesForAdminAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<Service>(@"
            SELECT ServiceId, CategoryId, Name, DescriptionText, DurationMinutes, PriceCents, PriceIsFrom, SortOrder, IsActive
            FROM dbo.Service
            ORDER BY CategoryId, SortOrder");
    }

    /// <param name="changeType">'Manual' (single edit in services-admin.html) or 'CPI' (bulk price
    /// rise). Logged to ServicePriceHistory only when the price actually changes — a duration-only
    /// edit doesn't count as a price change.</param>
    public async Task UpdateServiceAsync(int serviceId, int priceCents, int durationMinutes, string changeType)
    {
        using var conn = Open();
        // OUTPUT captures the before/after price atomically in the same statement — no separate
        // SELECT-then-UPDATE race with whatever else might be touching this row.
        var change = await conn.QuerySingleAsync<(int OldPriceCents, int NewPriceCents)>(@"
            UPDATE dbo.Service
            SET PriceCents = @priceCents, DurationMinutes = @durationMinutes
            OUTPUT DELETED.PriceCents AS OldPriceCents, INSERTED.PriceCents AS NewPriceCents
            WHERE ServiceId = @serviceId",
            new { serviceId, priceCents, durationMinutes });

        if (change.OldPriceCents != change.NewPriceCents)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO dbo.ServicePriceHistory (ServiceId, OldPriceCents, NewPriceCents, ChangeType)
                VALUES (@serviceId, @OldPriceCents, @NewPriceCents, @changeType)",
                new { serviceId, change.OldPriceCents, change.NewPriceCents, changeType });
        }
    }

    // ── Price history / bulk CPI price rise (once-a-year gate) ──────────────

    public async Task<DateTime?> GetLastCpiPriceRiseUtcAsync()
    {
        using var conn = Open();
        var lastApplied = await conn.QueryFirstOrDefaultAsync<DateTime?>(
            "SELECT MAX(ChangedAtUtc) FROM dbo.ServicePriceHistory WHERE ChangeType = 'CPI'");
        // Same Kind=Unspecified issue as Booking's DATETIME2 columns — tag it Utc so callers
        // (and any JSON serialization) treat it correctly instead of as local time.
        return lastApplied is null ? null : DateTime.SpecifyKind(lastApplied.Value, DateTimeKind.Utc);
    }

    /// <summary>Full price-change audit trail, most recent first. Pass a serviceId to see one
    /// service's history only.</summary>
    public async Task<IEnumerable<ServicePriceHistoryRow>> GetServicePriceHistoryAsync(int? serviceId = null)
    {
        using var conn = Open();
        return await conn.QueryAsync<ServicePriceHistoryRow>(@"
            SELECT h.ServicePriceHistoryId, h.ServiceId, sv.Name AS ServiceName,
                   h.OldPriceCents, h.NewPriceCents, h.ChangeType, h.ChangedAtUtc
            FROM dbo.ServicePriceHistory h
            JOIN dbo.Service sv ON sv.ServiceId = h.ServiceId
            WHERE @serviceId IS NULL OR h.ServiceId = @serviceId
            ORDER BY h.ChangedAtUtc DESC",
            new { serviceId });
    }
}
