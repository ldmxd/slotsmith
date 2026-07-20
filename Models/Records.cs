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
    DateTime TokenExpiresUtc);

// ── API request/response shapes ─────────────────────────────────────────

public record BookingRequestItem(int ServiceId);

public record CreateBookingRequest(
    int StaffId,                 // 0 = "no preference", server picks first available
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    DateTime StartUtc,
    List<BookingRequestItem> Items,
    string? Notes);

public record AvailabilitySlot(DateTime StartUtc, DateTime EndUtc);

public record BusyInterval(DateTime StartUtc, DateTime EndUtc);
