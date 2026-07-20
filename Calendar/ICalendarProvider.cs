using SlotSmith.Api.Models;

namespace SlotSmith.Api.Calendar;

/// <summary>
/// Everything the booking engine needs from a staff member's external calendar.
/// The booking engine never talks to Google or Microsoft directly — only through this
/// interface — so adding a third provider later (iCloud, CalDAV, whatever) means writing
/// one new class, not touching availability/booking logic.
/// </summary>
public interface ICalendarProvider
{
    /// <summary>Provider key stored in dbo.CalendarConnection.Provider, e.g. "Google" / "Microsoft".</summary>
    string ProviderKey { get; }

    /// <summary>Redirect URL to start the OAuth consent flow for a given staff member.</summary>
    string BuildAuthorizationUrl(int staffId, string state);

    /// <summary>Exchange the authorization code from the OAuth callback for tokens.</summary>
    Task<CalendarConnection> ExchangeCodeAsync(int staffId, string code, CancellationToken ct = default);

    /// <summary>Busy intervals for this staff member's calendar in the given UTC window.</summary>
    Task<List<BusyInterval>> GetBusyTimesAsync(CalendarConnection connection, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Create the confirmed-booking event on the staff member's calendar. Returns the provider's event id.</summary>
    Task<string> CreateEventAsync(CalendarConnection connection, string title, string? description, DateTime startUtc, DateTime endUtc, CancellationToken ct = default);

    /// <summary>Remove a previously created event (booking cancelled).</summary>
    Task CancelEventAsync(CalendarConnection connection, string eventId, CancellationToken ct = default);

    /// <summary>Refresh an expired access token, returning the updated connection (caller persists it).</summary>
    Task<CalendarConnection> RefreshTokenAsync(CalendarConnection connection, CancellationToken ct = default);
}
