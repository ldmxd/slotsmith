using Dapper;
using Microsoft.AspNetCore.DataProtection;
using SlotSmith.Api.Calendar;
using SlotSmith.Api.Data;
using SlotSmith.Api.Models;
using SlotSmith.Api.Services;
using System.Net.Http.Headers;
using System.Text.Json;

// ── Config ───────────────────────────────────────────────────────────────
var connStr = Environment.GetEnvironmentVariable("SLOTSMITH_SQL");
if (string.IsNullOrEmpty(connStr))
    throw new Exception("Missing SLOTSMITH_SQL connection string");

var venueTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("keys"));

builder.Services.AddHttpClient();
builder.Services.AddSingleton(new BookingRepository(connStr));
builder.Services.AddSingleton<ICalendarProvider, GoogleCalendarProvider>();
builder.Services.AddSingleton<ICalendarProvider, MicrosoftCalendarProvider>();
builder.Services.AddSingleton<CalendarProviderFactory>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Catalogue ────────────────────────────────────────────────────────────

app.MapGet("/api/services", async (BookingRepository repo) =>
{
    var categories = await repo.GetCategoriesAsync();
    var services = await repo.GetActiveServicesAsync();

    var result = categories.Select(c => new
    {
        c.CategoryId,
        c.Name,
        Services = services.Where(s => s.CategoryId == c.CategoryId).Select(s => new
        {
            s.ServiceId,
            s.Name,
            s.DescriptionText,
            s.DurationMinutes,
            PriceDollars = s.PriceCents / 100m,
            s.PriceIsFrom
        })
    }).Where(c => c.Services.Any());

    return Results.Ok(result);
});

app.MapGet("/api/staff", async (BookingRepository repo) =>
{
    var staff = await repo.GetActiveStaffAsync();
    return Results.Ok(staff.Select(s => new { s.StaffId, s.DisplayName, s.PhotoUrl, s.Bio }));
});

// ── Availability ─────────────────────────────────────────────────────────
// staffId = 0 means "no preference" — returns the union of slots across every eligible
// staff member, each slot tagged with which staff member it's actually for.

app.MapPost("/api/availability", async (AvailabilityRequest req, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    if (req.ServiceIds is null || req.ServiceIds.Count == 0)
        return Results.BadRequest("At least one service is required.");

    var eligible = (await repo.GetEligibleStaffAsync(req.ServiceIds)).ToList();
    if (req.StaffId is > 0)
        eligible = eligible.Where(e => e.StaffId == req.StaffId).ToList();

    if (eligible.Count == 0)
        return Results.Ok(Array.Empty<object>());

    var date = DateOnly.Parse(req.Date);
    var dayOfWeek = (byte)(int)date.DayOfWeek;
    var hours = await repo.GetBusinessHoursAsync(dayOfWeek);
    if (hours is null)
        return Results.Ok(Array.Empty<object>());

    var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), venueTimeZone);
    var dayEndUtc = dayStartUtc.AddDays(1);

    var response = new List<AvailabilityResponseSlot>();

    foreach (var (staffId, totalPriceCents, totalDurationMinutes) in eligible)
    {
        var busy = new List<BusyInterval>(await repo.GetExistingBookingsAsync(staffId, dayStartUtc, dayEndUtc));

        // Pull in the connected calendar's busy times too, if this staff member has linked one.
        foreach (var providerKey in calendarFactory.SupportedProviders)
        {
            var connection = await repo.GetCalendarConnectionAsync(staffId, providerKey);
            if (connection is null) continue;

            var provider = calendarFactory.Get(providerKey);
            if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
            {
                connection = await provider.RefreshTokenAsync(connection);
                await repo.UpsertCalendarConnectionAsync(connection);
            }
            busy.AddRange(await provider.GetBusyTimesAsync(connection, dayStartUtc, dayEndUtc));
        }

        var slots = AvailabilityEngine.ComputeSlots(
            date, hours, totalDurationMinutes, busy, venueTimeZone, slotGranularityMinutes: 15, now: DateTime.UtcNow);

        foreach (var slot in slots)
            response.Add(new AvailabilityResponseSlot(staffId, slot.StartUtc, slot.EndUtc, totalPriceCents, totalDurationMinutes));
    }

    return Results.Ok(response.OrderBy(r => r.StartUtc));
});

// ── Bookings ─────────────────────────────────────────────────────────────

app.MapPost("/api/bookings", async (CreateBookingRequest req, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    if (req.Items is null || req.Items.Count == 0)
        return Results.BadRequest("At least one service is required.");

    var serviceIds = req.Items.Select(i => i.ServiceId).ToList();
    var eligible = (await repo.GetEligibleStaffAsync(serviceIds)).ToList();
    if (req.StaffId > 0)
        eligible = eligible.Where(e => e.StaffId == req.StaffId).ToList();

    if (eligible.Count == 0)
        return Results.BadRequest("No staff member available for the selected services.");

    // "No preference" — pick the first eligible staff member who's actually free at that time.
    // (A production version would re-check availability server-side per candidate; kept simple here.)
    var chosen = eligible.First();
    var endUtc = req.StartUtc.AddMinutes(chosen.TotalDurationMinutes);

    var customerId = await repo.CreateCustomerAsync(req.CustomerName, req.CustomerEmail, req.CustomerPhone);

    string? calendarProvider = null;
    string? calendarEventId = null;

    foreach (var providerKey in calendarFactory.SupportedProviders)
    {
        var connection = await repo.GetCalendarConnectionAsync(chosen.StaffId, providerKey);
        if (connection is null) continue;

        var provider = calendarFactory.Get(providerKey);
        if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
        {
            connection = await provider.RefreshTokenAsync(connection);
            await repo.UpsertCalendarConnectionAsync(connection);
        }

        var title = $"{req.CustomerName} — booking";
        var description = string.Join(", ", req.Items.Select(i => i.ServiceId)); // resolved to names client-side / in a fuller version
        calendarEventId = await provider.CreateEventAsync(connection, title, description, req.StartUtc, endUtc);
        calendarProvider = providerKey;
        break; // a staff member only has one calendar connected in practice; first match wins
    }

    var items = (await repo.GetStaffServiceBreakdownAsync(chosen.StaffId, serviceIds)).ToList();

    var bookingId = await repo.CreateBookingAsync(
        customerId, chosen.StaffId, req.StartUtc, endUtc, calendarProvider, calendarEventId, req.Notes, items);

    var confirmationSent = false;
    try
    {
        var allStaff = await repo.GetActiveStaffAsync();
        var allServices = await repo.GetActiveServicesAsync();
        var staffName = allStaff.FirstOrDefault(s => s.StaffId == chosen.StaffId)?.DisplayName ?? "your stylist";
        var serviceNames = allServices.Where(s => serviceIds.Contains(s.ServiceId)).Select(s => s.Name).ToList();

        await SendBookingConfirmationEmailAsync(
            builder.Configuration, req.CustomerEmail, req.CustomerName, staffName, serviceNames,
            req.StartUtc, endUtc, venueTimeZone, chosen.TotalPriceCents);
        confirmationSent = true;
    }
    catch (Exception ex)
    {
        // Don't fail the booking just because the confirmation email couldn't be sent.
        Console.WriteLine($"[Resend] Failed to send booking confirmation: {ex.Message}");
    }

    return Results.Ok(new { BookingId = bookingId, StaffId = chosen.StaffId, req.StartUtc, EndUtc = endUtc, CalendarConnected = calendarProvider is not null, ConfirmationSent = confirmationSent });
});

// ── Calendar OAuth (admin-side: staff linking their own calendar) ─────────

app.MapGet("/api/calendar/{provider}/connect", (string provider, int staffId, CalendarProviderFactory calendarFactory) =>
{
    var cal = calendarFactory.Get(provider);
    var state = staffId.ToString();
    return Results.Redirect(cal.BuildAuthorizationUrl(staffId, state));
});

app.MapGet("/api/calendar/{provider}/callback", async (string provider, string code, string state, CalendarProviderFactory calendarFactory, BookingRepository repo) =>
{
    var staffId = int.Parse(state);
    var cal = calendarFactory.Get(provider);
    var connection = await cal.ExchangeCodeAsync(staffId, code);
    await repo.UpsertCalendarConnectionAsync(connection);
    return Results.Redirect($"/admin.html?connected={provider}&staffId={staffId}");
});

app.MapGet("/api/calendar/status", async (int staffId, BookingRepository repo) =>
{
    var connections = await repo.GetCalendarConnectionsForStaffAsync(staffId);
    return Results.Ok(connections.Select(c => new { c.Provider, c.ExternalAccountEmail, c.ConnectedAt }));
});

app.Run();

// ── Email (Resend) ──────────────────────────────────────────────────────
// Same provider/pattern as OceanSwimmer.Api — plain REST call, no SDK. Reuses the same
// Resend account; only the API key + from-address need to be set per deployment.

async Task SendBookingConfirmationEmailAsync(
    IConfiguration config, string toEmail, string customerName, string staffName,
    List<string> serviceNames, DateTime startUtc, DateTime endUtc, TimeZoneInfo venueTimeZone, int totalPriceCents)
{
    var apiKey    = config["Resend:ApiKey"];
    var fromEmail = config["Resend:FromEmail"] ?? "noreply@mihoknows.com.au";
    var fromName  = config["Resend:FromName"]  ?? "SlotSmith Bookings";

    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, venueTimeZone);
    var whenText = startLocal.ToString("dddd d MMMM, h:mm tt");
    var servicesText = string.Join(", ", serviceNames);
    var priceText = (totalPriceCents / 100m).ToString("C");

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
    {
        // Resend not configured — log instead, so local dev still works without a live key.
        Console.WriteLine($"[DEV] Booking confirmation for {toEmail}: {servicesText} with {staffName} on {whenText} ({priceText})");
        return;
    }

    await SendResendEmailAsync(apiKey, fromEmail, fromName, toEmail,
        subject: "Your booking is confirmed",
        text: $"Hi {customerName},\n\nYour booking is confirmed:\n\n{servicesText}\nWith {staffName}\n{whenText}\nTotal: {priceText}\n\nSee you then!",
        html: $@"
            <p>Hi {customerName},</p>
            <p>Your booking is confirmed:</p>
            <table style=""margin:16px 0;font-size:15px;"">
                <tr><td style=""color:#888;padding-right:12px;"">Service</td><td>{servicesText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">With</td><td>{staffName}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">When</td><td>{whenText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">Total</td><td>{priceText}</td></tr>
            </table>
            <p style=""color:#888;font-size:13px;"">See you then!</p>");
}

async Task SendResendEmailAsync(string apiKey, string fromEmail, string fromName,
    string toEmail, string subject, string text, string html)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = JsonSerializer.Serialize(new
    {
        from    = $"{fromName} <{fromEmail}>",
        to      = new[] { toEmail },
        subject,
        text,
        html
    });

    var response = await http.PostAsync(
        "https://api.resend.com/emails",
        new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[Resend] Error {(int)response.StatusCode}: {body}");
    }
}

record AvailabilityRequest(List<int> ServiceIds, int? StaffId, string Date);
record AvailabilityResponseSlot(int StaffId, DateTime StartUtc, DateTime EndUtc, int TotalPriceCents, int TotalDurationMinutes);
