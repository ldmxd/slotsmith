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

    public async Task<IEnumerable<BusyInterval>> GetExistingBookingsAsync(int staffId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        return await conn.QueryAsync<BusyInterval>(@"
            SELECT StartUtc, EndUtc
            FROM dbo.Booking
            WHERE StaffId = @staffId AND Status = 'Confirmed'
              AND StartUtc < @toUtc AND EndUtc > @fromUtc",
            new { staffId, fromUtc, toUtc });
    }

    public async Task<CalendarConnection?> GetCalendarConnectionAsync(int staffId, string provider)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<CalendarConnection>(
            "SELECT * FROM dbo.CalendarConnection WHERE StaffId = @staffId AND Provider = @provider",
            new { staffId, provider });
    }

    public async Task<IEnumerable<CalendarConnection>> GetCalendarConnectionsForStaffAsync(int staffId)
    {
        using var conn = Open();
        return await conn.QueryAsync<CalendarConnection>(
            "SELECT * FROM dbo.CalendarConnection WHERE StaffId = @staffId",
            new { staffId });
    }

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

    public async Task<int> CreateBookingAsync(int customerId, int staffId, DateTime startUtc, DateTime endUtc,
        string? calendarProvider, string? calendarEventId, string? notes, IEnumerable<(int ServiceId, int PriceCents, int DurationMinutes)> items)
    {
        using var conn = Open();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var bookingId = await conn.QuerySingleAsync<int>(@"
            INSERT INTO dbo.Booking (CustomerId, StaffId, StartUtc, EndUtc, CalendarProvider, CalendarEventId, Notes)
            OUTPUT INSERTED.BookingId
            VALUES (@customerId, @staffId, @startUtc, @endUtc, @calendarProvider, @calendarEventId, @notes)",
            new { customerId, staffId, startUtc, endUtc, calendarProvider, calendarEventId, notes }, tx);

        foreach (var item in items)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO dbo.BookingItem (BookingId, ServiceId, PriceCents, DurationMinutes)
                VALUES (@bookingId, @ServiceId, @PriceCents, @DurationMinutes)",
                new { bookingId, item.ServiceId, item.PriceCents, item.DurationMinutes }, tx);
        }

        tx.Commit();
        return bookingId;
    }
}
