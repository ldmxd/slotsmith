namespace SlotSmith.Api.Models;

public record Staff(int StaffId, string DisplayName, string? Email, string? PhotoUrl, string? Bio, int SortOrder, bool IsActive);

public record ServiceCategory(int CategoryId, string Name, int SortOrder);

public record Service(
    int ServiceId,
    int CategoryId,
    string Name,
    string? DescriptionText,
    int DurationMinutes,
    int PriceCents,
    bool PriceIsFrom,
    int SortOrder,
    bool IsActive);

public record StaffServiceOffering(
    int StaffId,
    int ServiceId,
    int EffectivePriceCents,
    int EffectiveDurationMinutes);

public record BusinessHours(byte DayOfWeek, TimeSpan? OpenTime, TimeSpan? CloseTime);

public record CalendarConnection(
    int CalendarConnectionId,
    int StaffId,
    string Provider,
    string? ExternalAccountEmail,
    string AccessTokenEncrypted,
    string RefreshTokenEncrypted,
    DateTime TokenExpiresUtc,
    DateTime ConnectedAt,
    string? CalendarId);   // null until the staff member picks one — providers fall back to their default calendar

public record CalendarOption(string Id, string Name, bool IsDefault);

public record Customer(int CustomerId, string Name, string Email, string? Phone, DateTime CreatedAt);

public record Booking(
    int BookingId,
    int CustomerId,
    int StaffId,
    DateTime StartUtc,
    DateTime EndUtc,
    string Status,
    string? CalendarProvider,
    string? CalendarEventId,
    string? Notes,
    string ManageToken,
    DateTime CreatedAt);

public record BookingItemRow(int BookingItemId, int BookingId, int ServiceId, int PriceCents, int DurationMinutes);

// ── API request/response shapes ─────────────────────────────────────────

public record BookingRequestItem(int ServiceId);

public record CreateBookingRequest(
    int StaffId,                 // 0 = "no preference", server picks first available
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    DateTime StartUtc,
    List<BookingRequestItem> Items,
    string? Notes,
    // Bot-mitigation fields — both optional so nothing breaks if an older client doesn't send them.
    string? Website = null,             // honeypot: real users never see/fill this field
    long? FormLoadedAtUnixMs = null);   // client timestamp from when the booking form first loaded

public record AvailabilitySlot(DateTime StartUtc, DateTime EndUtc);

public record BusyInterval(DateTime StartUtc, DateTime EndUtc);

public record RescheduleBookingRequest(DateTime NewStartUtc);

public record UpdateServiceRequest(decimal PriceDollars, int DurationMinutes);

public record ServicePriceHistoryRow(
    int ServicePriceHistoryId,
    int ServiceId,
    string ServiceName,
    int OldPriceCents,
    int NewPriceCents,
    string ChangeType,
    DateTime ChangedAtUtc);

public record CreateStaffRequest(string DisplayName, string? Email, string? Bio);

public record UpdateStaffRequest(string DisplayName, string? Email, string? Bio, bool IsActive);

public record UpdateStaffServicesRequest(List<int> ServiceIds);

public record AdminLoginRequest(string Password);

public record BulkPriceIncreaseRequest(decimal PercentIncrease);

public record StaffTimeOff(
    int TimeOffId, int StaffId, DateTime StartUtc, DateTime EndUtc, string? Reason,
    string? CalendarProvider, string? CalendarEventId, DateTime CreatedAt);

// StartDate/EndDate are venue-local calendar dates ("yyyy-MM-dd"), inclusive of both ends —
// matches the AvailabilityRequest.Date convention (a plain date string, parsed server-side).
public record CreateTimeOffRequest(string StartDate, string EndDate, string? Reason);

// One row per matching booking from the admin "all bookings" search (bookings-admin.html).
// Includes ManageToken — safe here since this endpoint is behind admin auth — so the admin page
// can link straight into the existing customer-facing manage-booking.html reschedule/cancel flow
// instead of re-implementing it.
public record BookingSearchRow(
    int BookingId, string ManageToken, string Status, DateTime StartUtc, DateTime EndUtc,
    string CustomerName, string CustomerEmail, string? CustomerPhone,
    int StaffId, string StaffName, string ServiceNames, int TotalPriceCents);
