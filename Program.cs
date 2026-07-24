using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using SlotSmith.Api.Calendar;
using SlotSmith.Api.Data;
using SlotSmith.Api.Models;
using SlotSmith.Api.Services;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

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

// Admin auth: a single shared password (Admin:Password, via user-secrets / env var — never
// committed), not a per-person account system. Angelo and Mark are the only people who'll ever
// log in here, so this matches the actual need instead of building out user accounts nobody
// asked for. A successful login sets a long-lived cookie; API calls without it get a plain 401
// (not a redirect — these are fetch() calls from the admin pages' JS, not browser navigations).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "slotsmith_admin";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Per-IP cap on booking creation, so a script hammering the public form can't flood the calendar
// with fake appointments. Keyed on the (forwarded-header-corrected) client IP; 5/hour is generous
// for a real customer — nobody books 5 appointments an hour — but blocks scripted abuse.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("bookings", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ── Admin auth ───────────────────────────────────────────────────────────

app.MapPost("/api/admin/login", async (HttpContext http, AdminLoginRequest req, IConfiguration config) =>
{
    var expected = config["Admin:Password"];
    if (string.IsNullOrWhiteSpace(expected) || expected.StartsWith("YOUR_"))
    {
        // Not configured — fail closed rather than silently accepting anything.
        return Results.Problem("Admin login isn't configured on this server yet.", statusCode: 500);
    }
    if (req.Password != expected)
        return Results.Unauthorized();

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok();
});

app.MapPost("/api/admin/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/admin/session", (HttpContext http) =>
    Results.Ok(new { LoggedIn = http.User.Identity?.IsAuthenticated ?? false }));

// ── Catalogue ────────────────────────────────────────────────────────────

app.MapGet("/api/business-info", (IConfiguration config) =>
    Results.Ok(new { BusinessName = config["App:BusinessName"] ?? "SlotSmith" }));

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

    // When called from the reschedule flow, ExcludeBookingToken identifies "the booking currently
    // being moved" so its own current slot doesn't show up as busy against itself — otherwise a
    // customer could never pick their existing time back from the picker.
    Booking? excludeBooking = req.ExcludeBookingToken is not null
        ? await repo.GetBookingByTokenAsync(req.ExcludeBookingToken)
        : null;

    var response = new List<AvailabilityResponseSlot>();

    foreach (var (staffId, totalPriceCents, totalDurationMinutes) in eligible)
    {
        var excludeBookingId = excludeBooking?.StaffId == staffId ? excludeBooking.BookingId : (int?)null;
        var busy = new List<BusyInterval>(await repo.GetExistingBookingsAsync(staffId, dayStartUtc, dayEndUtc, excludeBookingId));
        busy.AddRange(await repo.GetStaffTimeOffBusyAsync(staffId, dayStartUtc, dayEndUtc));

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
            var calendarBusy = await provider.GetBusyTimesAsync(connection, dayStartUtc, dayEndUtc);

            // freeBusy responses are plain time ranges with no event id, so we can't exclude "this
            // booking's event" by identity — instead drop any interval that exactly matches the
            // booking being rescheduled. Good enough in a booking-system-managed calendar; a
            // coincidental external event at the identical minute is vanishingly unlikely.
            if (excludeBookingId is not null && excludeBooking is not null)
            {
                calendarBusy = calendarBusy
                    .Where(b => !(b.StartUtc == excludeBooking.StartUtc && b.EndUtc == excludeBooking.EndUtc))
                    .ToList();
            }
            busy.AddRange(calendarBusy);
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
    // Honeypot: a field real users never see or fill (see booking.js). Any bot that blindly fills
    // every input trips it. Reject quietly — no need to tell it why.
    if (!string.IsNullOrWhiteSpace(req.Website))
        return Results.BadRequest("Something went wrong. Please try again.");

    // A human needs at least a few seconds to click through services → professional → time →
    // details before submitting; a script that posts directly to this endpoint typically doesn't
    // wait at all. This is a soft signal, not a hard guarantee (a patient bot can wait it out) —
    // combined with the honeypot and the per-IP rate limit below, it raises the bar cheaply.
    if (req.FormLoadedAtUnixMs is not null)
    {
        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - req.FormLoadedAtUnixMs.Value;
        if (elapsedMs is >= 0 and < 3000)
            return Results.BadRequest("Something went wrong. Please try again.");
    }

    if (req.Items is null || req.Items.Count == 0)
        return Results.BadRequest("At least one service is required.");

    // This only checks the address is well-formed (has a plausible local-part@domain.tld shape) —
    // it doesn't confirm the customer actually owns/can receive at it. That's a deliberate
    // trade-off: real ownership verification (magic link / OTP) would add a step to the booking
    // funnel most customers won't tolerate, and no mainstream booking tool (Fresha included) does
    // it either. A bad address just means that customer doesn't get their own confirmation email.
    var normalizedEmail = req.CustomerEmail?.Trim().ToLowerInvariant() ?? "";
    if (!IsValidEmail(normalizedEmail))
        return Results.BadRequest("Please enter a valid email address.");
    req = req with { CustomerEmail = normalizedEmail };

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

    // This endpoint otherwise trusts the client's chosen slot came from a recent /api/availability
    // call rather than re-running the full ComputeSlots check (see the "no preference" comment
    // above) — but time off specifically needs a hard guard here, since an admin could mark a
    // stylist unavailable after a customer already loaded the booking page with stale slots.
    var timeOffConflict = (await repo.GetStaffTimeOffBusyAsync(chosen.StaffId, req.StartUtc, endUtc)).Any();
    if (timeOffConflict)
        return Results.BadRequest("That stylist isn't available at that time. Please pick a different time or stylist.");

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

    var (bookingId, manageToken) = await repo.CreateBookingAsync(
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
            req.StartUtc, endUtc, venueTimeZone, chosen.TotalPriceCents, manageToken);
        confirmationSent = true;
    }
    catch (Exception ex)
    {
        // Don't fail the booking just because the confirmation email couldn't be sent.
        Console.WriteLine($"[Resend] Failed to send booking confirmation: {ex.Message}");
    }

    return Results.Ok(new { BookingId = bookingId, StaffId = chosen.StaffId, req.StartUtc, EndUtc = endUtc, CalendarConnected = calendarProvider is not null, ConfirmationSent = confirmationSent });
}).RequireRateLimiting("bookings");

// ── Manage my booking (token-based, no login) ──────────────────────────────
// The link in the confirmation email points at manage-booking.html?token=..., which calls
// these endpoints. Anyone with the token can view/reschedule/cancel — that's the intended
// trade-off for a no-login flow; the token is a 48-char random hex string, not guessable.

app.MapGet("/api/bookings/manage/{token}", async (string token, BookingRepository repo) =>
{
    var booking = await repo.GetBookingByTokenAsync(token);
    if (booking is null) return Results.NotFound();

    var customer = await repo.GetCustomerAsync(booking.CustomerId);
    var items = (await repo.GetBookingItemsAsync(booking.BookingId)).ToList();
    var allStaff = await repo.GetActiveStaffAsync();
    var allServices = await repo.GetActiveServicesAsync();

    var staffName = allStaff.FirstOrDefault(s => s.StaffId == booking.StaffId)?.DisplayName ?? "Staff member";
    var serviceNames = items.Select(i => allServices.FirstOrDefault(s => s.ServiceId == i.ServiceId)?.Name ?? "Service").ToList();
    var totalPriceCents = items.Sum(i => i.PriceCents);
    var totalDurationMinutes = items.Sum(i => i.DurationMinutes);

    return Results.Ok(new
    {
        booking.BookingId,
        booking.StaffId,
        booking.StartUtc,
        booking.EndUtc,
        booking.Status,
        CustomerName = customer?.Name,
        StaffName = staffName,
        ServiceIds = items.Select(i => i.ServiceId).ToList(), // needed so reschedule can re-query /api/availability
        ServiceNames = serviceNames,
        TotalPriceCents = totalPriceCents,
        TotalDurationMinutes = totalDurationMinutes
    });
});

app.MapPost("/api/bookings/manage/{token}/reschedule", async (string token, RescheduleBookingRequest req, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    var booking = await repo.GetBookingByTokenAsync(token);
    if (booking is null) return Results.NotFound();
    if (booking.Status != "Confirmed") return Results.BadRequest("This booking is no longer active.");

    var duration = booking.EndUtc - booking.StartUtc;
    var durationMinutes = (int)duration.TotalMinutes;
    var newEndUtc = req.NewStartUtc.Add(duration);

    // Re-validate against the same rules /api/availability uses (business hours, other bookings,
    // the staff member's connected calendar) so a reschedule can't be pushed outside business hours
    // or on top of something else — the manage-booking UI only offers valid slots, but the API has
    // to enforce it too, since nothing stops a direct call with an arbitrary time otherwise.
    var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(req.NewStartUtc, venueTimeZone));
    var dayOfWeek = (byte)(int)localDate.DayOfWeek;
    var hours = await repo.GetBusinessHoursAsync(dayOfWeek);
    if (hours is null) return Results.BadRequest("That day is outside business hours.");

    var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue), venueTimeZone);
    var dayEndUtc = dayStartUtc.AddDays(1);

    var busyForValidation = new List<BusyInterval>(
        await repo.GetExistingBookingsAsync(booking.StaffId, dayStartUtc, dayEndUtc, excludeBookingId: booking.BookingId));
    busyForValidation.AddRange(await repo.GetStaffTimeOffBusyAsync(booking.StaffId, dayStartUtc, dayEndUtc));

    foreach (var providerKey in calendarFactory.SupportedProviders)
    {
        var conn = await repo.GetCalendarConnectionAsync(booking.StaffId, providerKey);
        if (conn is null) continue;
        var provider = calendarFactory.Get(providerKey);
        if (conn.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
        {
            conn = await provider.RefreshTokenAsync(conn);
            await repo.UpsertCalendarConnectionAsync(conn);
        }
        var calendarBusyForValidation = await provider.GetBusyTimesAsync(conn, dayStartUtc, dayEndUtc);

        // Same fix as /api/availability's exclusion filter below (search ExcludeBookingToken): the
        // booking being rescheduled still has its own event sitting in the staff member's calendar
        // at this point — the old event isn't cancelled until after validation passes, further down
        // this handler — so without dropping it here, re-picking a time that overlaps the booking's
        // *current* slot (e.g. nudging it by 15 minutes) always fails validation against itself.
        calendarBusyForValidation = calendarBusyForValidation
            .Where(b => !(b.StartUtc == booking.StartUtc && b.EndUtc == booking.EndUtc))
            .ToList();
        busyForValidation.AddRange(calendarBusyForValidation);
    }

    var validSlots = AvailabilityEngine.ComputeSlots(
        localDate, hours, durationMinutes, busyForValidation, venueTimeZone, slotGranularityMinutes: 15, now: DateTime.UtcNow);
    if (!validSlots.Any(s => s.StartUtc == req.NewStartUtc))
        return Results.BadRequest("That time isn't available anymore. Please pick a different slot.");

    var customer = await repo.GetCustomerAsync(booking.CustomerId);
    var newCalendarEventId = booking.CalendarEventId;

    // Move the calendar event too: cancel the old one, create a new one at the new time.
    // (Avoids adding an UpdateEventAsync to ICalendarProvider just for this.)
    if (booking.CalendarProvider is not null && booking.CalendarEventId is not null)
    {
        var connection = await repo.GetCalendarConnectionAsync(booking.StaffId, booking.CalendarProvider);
        if (connection is not null)
        {
            var provider = calendarFactory.Get(booking.CalendarProvider);
            if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
            {
                connection = await provider.RefreshTokenAsync(connection);
                await repo.UpsertCalendarConnectionAsync(connection);
            }

            await provider.CancelEventAsync(connection, booking.CalendarEventId);

            var title = $"{customer?.Name ?? "Customer"} — booking";
            newCalendarEventId = await provider.CreateEventAsync(connection, title, description: null, req.NewStartUtc, newEndUtc);
        }
    }

    await repo.UpdateBookingTimeAsync(booking.BookingId, req.NewStartUtc, newEndUtc, newCalendarEventId);

    if (customer is not null)
    {
        try
        {
            var allStaff = await repo.GetActiveStaffAsync();
            var allServices = await repo.GetActiveServicesAsync();
            var items = await repo.GetBookingItemsAsync(booking.BookingId);
            var staffName = allStaff.FirstOrDefault(s => s.StaffId == booking.StaffId)?.DisplayName ?? "your stylist";
            var serviceNames = items.Select(i => allServices.FirstOrDefault(s => s.ServiceId == i.ServiceId)?.Name ?? "Service").ToList();

            await SendBookingRescheduledEmailAsync(
                builder.Configuration, customer.Email, customer.Name, staffName, serviceNames,
                req.NewStartUtc, newEndUtc, venueTimeZone, booking.ManageToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Resend] Failed to send reschedule confirmation: {ex.Message}");
        }
    }

    return Results.Ok(new { booking.BookingId, StartUtc = req.NewStartUtc, EndUtc = newEndUtc });
});

app.MapPost("/api/bookings/manage/{token}/cancel", async (string token, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    var booking = await repo.GetBookingByTokenAsync(token);
    if (booking is null) return Results.NotFound();
    if (booking.Status != "Confirmed") return Results.BadRequest("This booking is already cancelled.");
    // Nothing meaningful for "cancel" to do once the appointment's already happened — and without
    // this, cancelling one here would still fire the same "your booking has been cancelled" email
    // a live cancellation sends, which is confusing for something already over.
    if (booking.StartUtc < DateTime.UtcNow) return Results.BadRequest("This booking has already passed.");

    if (booking.CalendarProvider is not null && booking.CalendarEventId is not null)
    {
        var connection = await repo.GetCalendarConnectionAsync(booking.StaffId, booking.CalendarProvider);
        if (connection is not null)
        {
            var provider = calendarFactory.Get(booking.CalendarProvider);
            if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
            {
                connection = await provider.RefreshTokenAsync(connection);
                await repo.UpsertCalendarConnectionAsync(connection);
            }
            await provider.CancelEventAsync(connection, booking.CalendarEventId);
        }
    }

    await repo.CancelBookingAsync(booking.BookingId);

    var customer = await repo.GetCustomerAsync(booking.CustomerId);
    if (customer is not null)
    {
        try
        {
            var allStaff = await repo.GetActiveStaffAsync();
            var allServices = await repo.GetActiveServicesAsync();
            var items = await repo.GetBookingItemsAsync(booking.BookingId);
            var staffName = allStaff.FirstOrDefault(s => s.StaffId == booking.StaffId)?.DisplayName ?? "your stylist";
            var serviceNames = items.Select(i => allServices.FirstOrDefault(s => s.ServiceId == i.ServiceId)?.Name ?? "Service").ToList();

            await SendBookingCancelledEmailAsync(
                builder.Configuration, customer.Email, customer.Name, staffName, serviceNames, booking.StartUtc, venueTimeZone);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Resend] Failed to send cancellation confirmation: {ex.Message}");
        }
    }

    return Results.Ok();
});

// ── Admin: search all bookings by client (bookings-admin.html) ─────────────
// For phone-in requests — "can you move my Tuesday appointment" — where staff need to find a
// booking without the customer having their manage-booking link handy.

app.MapGet("/api/admin/bookings/search", async (string? q, BookingRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        return Results.Ok(Array.Empty<BookingSearchRow>());

    var results = await repo.SearchBookingsAsync(q.Trim());
    return Results.Ok(results);
}).RequireAuthorization();

// Default view on bookings-admin.html, before staff type a search — soonest-first so something
// like "tomorrow's 2pm" is visible without having to search for it.
app.MapGet("/api/admin/bookings/upcoming", async (BookingRepository repo) =>
{
    var results = await repo.GetUpcomingBookingsAsync(limit: 100);
    return Results.Ok(results);
}).RequireAuthorization();

// ── Service admin (Angelo editing prices / durations) ──────────────────────
// No auth on this yet — same known simplification as admin.html's calendar linking.
// Fine for a demo on an unlisted URL; flagged in README as something to lock down
// before this becomes a real multi-tenant product.

app.MapGet("/api/admin/services", async (BookingRepository repo) =>
{
    var categories = await repo.GetCategoriesAsync();
    var services = await repo.GetAllServicesForAdminAsync();

    var result = categories.Select(c => new
    {
        c.CategoryId,
        c.Name,
        Services = services.Where(s => s.CategoryId == c.CategoryId).Select(s => new
        {
            s.ServiceId,
            s.Name,
            PriceDollars = s.PriceCents / 100m,
            s.DurationMinutes,
            s.IsActive
        })
    });

    return Results.Ok(result);
}).RequireAuthorization();

app.MapPut("/api/admin/services/{id:int}", async (int id, UpdateServiceRequest req, BookingRepository repo) =>
{
    if (req.PriceDollars < 0 || req.DurationMinutes <= 0)
        return Results.BadRequest("Price must be >= 0 and duration must be > 0.");

    await repo.UpdateServiceAsync(id, (int)Math.Round(req.PriceDollars * 100), req.DurationMinutes, changeType: "Manual");
    return Results.Ok();
}).RequireAuthorization();

// Full price-change audit trail — every manual edit and every CPI bulk rise, per service.
app.MapGet("/api/admin/services/price-history", async (int? serviceId, BookingRepository repo) =>
{
    var history = await repo.GetServicePriceHistoryAsync(serviceId);
    return Results.Ok(history.Select(h => new
    {
        h.ServicePriceHistoryId,
        h.ServiceId,
        h.ServiceName,
        OldPriceDollars = h.OldPriceCents / 100m,
        NewPriceDollars = h.NewPriceCents / 100m,
        h.ChangeType,
        ChangedAtUtc = DateTime.SpecifyKind(h.ChangedAtUtc, DateTimeKind.Utc)
    }));
}).RequireAuthorization();

// CPI-style bulk price rise: apply X% to every service's current price, then round UP to the
// nearest $5 (never down — the point is to comfortably cover a cost increase, not undershoot
// it). Gated to once every 365 days so a stray extra click doesn't compound increases — derived
// from ServicePriceHistory's most recent 'CPI'-tagged row, not a separate counter.
const int PriceRiseCooldownDays = 365;

app.MapGet("/api/admin/services/price-rise-status", async (BookingRepository repo) =>
{
    var lastAppliedUtc = await repo.GetLastCpiPriceRiseUtcAsync();
    var nextEligibleUtc = lastAppliedUtc?.AddDays(PriceRiseCooldownDays);
    var eligibleNow = nextEligibleUtc is null || nextEligibleUtc <= DateTime.UtcNow;
    return Results.Ok(new { LastAppliedUtc = lastAppliedUtc, NextEligibleUtc = nextEligibleUtc, EligibleNow = eligibleNow });
}).RequireAuthorization();

app.MapPost("/api/admin/services/bulk-price-increase", async (BulkPriceIncreaseRequest req, BookingRepository repo) =>
{
    if (req.PercentIncrease <= 0) return Results.BadRequest("Percent increase must be greater than 0.");

    var lastAppliedUtc = await repo.GetLastCpiPriceRiseUtcAsync();
    if (lastAppliedUtc is not null)
    {
        var nextEligibleUtc = lastAppliedUtc.Value.AddDays(PriceRiseCooldownDays);
        if (nextEligibleUtc > DateTime.UtcNow)
        {
            var nextEligibleLocal = TimeZoneInfo.ConvertTimeFromUtc(nextEligibleUtc, venueTimeZone);
            return Results.BadRequest(
                $"A price rise was already applied on {TimeZoneInfo.ConvertTimeFromUtc(lastAppliedUtc.Value, venueTimeZone):d MMMM yyyy}. " +
                $"The next one isn't due until {nextEligibleLocal:d MMMM yyyy}.");
        }
    }

    var services = await repo.GetAllServicesForAdminAsync();
    var updatedCount = 0;
    foreach (var s in services)
    {
        var rawCents = s.PriceCents * (1 + req.PercentIncrease / 100m);
        var roundedCents = (int)(Math.Ceiling(rawCents / 500m) * 500m); // 500 cents = $5
        if (roundedCents != s.PriceCents)
        {
            await repo.UpdateServiceAsync(s.ServiceId, roundedCents, s.DurationMinutes, changeType: "CPI");
            updatedCount++;
        }
    }

    return Results.Ok(new { UpdatedCount = updatedCount });
}).RequireAuthorization();

// ── Staff admin (add/remove stylists, assign services) ──────────────────────
// "Remove" deactivates rather than hard-deletes — a stylist can have historical bookings and
// StaffService rows referencing them, and IsActive already governs visibility everywhere else
// (GetActiveStaffAsync, the booking flow's staff picker).

app.MapGet("/api/admin/staff", async (BookingRepository repo) =>
{
    var staff = await repo.GetAllStaffForAdminAsync();
    return Results.Ok(staff);
}).RequireAuthorization();

app.MapPost("/api/admin/staff", async (CreateStaffRequest req, BookingRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(req.DisplayName))
        return Results.BadRequest("Name is required.");

    var staffId = await repo.CreateStaffAsync(req.DisplayName.Trim(), req.Email, req.Bio);
    return Results.Ok(new { StaffId = staffId });
}).RequireAuthorization();

app.MapPut("/api/admin/staff/{id:int}", async (int id, UpdateStaffRequest req, BookingRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(req.DisplayName))
        return Results.BadRequest("Name is required.");

    await repo.UpdateStaffAsync(id, req.DisplayName.Trim(), req.Email, req.Bio, req.IsActive);
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/api/admin/staff/{id:int}/services", async (int id, BookingRepository repo) =>
{
    var serviceIds = await repo.GetStaffServiceIdsAsync(id);
    return Results.Ok(serviceIds);
}).RequireAuthorization();

app.MapPut("/api/admin/staff/{id:int}/services", async (int id, UpdateStaffServicesRequest req, BookingRepository repo) =>
{
    await repo.SetStaffServicesAsync(id, req.ServiceIds ?? new List<int>());
    return Results.Ok();
}).RequireAuthorization();

// ── Staff time off (e.g. Angelo taking a week off) ──────────────────────

app.MapGet("/api/admin/staff/{id:int}/time-off", async (int id, BookingRepository repo) =>
{
    var timeOff = await repo.GetStaffTimeOffAsync(id);
    return Results.Ok(timeOff);
}).RequireAuthorization();

app.MapPost("/api/admin/staff/{id:int}/time-off", async (int id, CreateTimeOffRequest req, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    var staff = await repo.GetStaffByIdAsync(id);
    if (staff is null) return Results.NotFound();

    if (!DateOnly.TryParse(req.StartDate, out var startDate) || !DateOnly.TryParse(req.EndDate, out var endDate))
        return Results.BadRequest("Invalid date.");
    if (endDate < startDate)
        return Results.BadRequest("End date must be on or after the start date.");

    // Inclusive of both calendar days — end boundary is the start of the day *after* EndDate,
    // same "day + 1" convention used everywhere else in this file for a UTC day range.
    var fromUtc = TimeZoneInfo.ConvertTimeToUtc(startDate.ToDateTime(TimeOnly.MinValue), venueTimeZone);
    var toUtc = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), venueTimeZone);

    // Put a visible block on the stylist's own connected calendar, if they have one — makes the
    // time off show up when they check their calendar directly, not just inside SlotSmith. Any
    // existing bookings that fall inside this range are deliberately left untouched (including
    // their own calendar events) until the customer reschedules via the email below — we don't
    // want to silently cancel/move a confirmed customer appointment on their behalf.
    string? timeOffCalendarProvider = null;
    string? timeOffCalendarEventId = null;
    foreach (var providerKey in calendarFactory.SupportedProviders)
    {
        var connection = await repo.GetCalendarConnectionAsync(id, providerKey);
        if (connection is null) continue;

        var provider = calendarFactory.Get(providerKey);
        if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
        {
            connection = await provider.RefreshTokenAsync(connection);
            await repo.UpsertCalendarConnectionAsync(connection);
        }

        var title = string.IsNullOrWhiteSpace(req.Reason) ? "Time off" : $"Time off — {req.Reason}";
        timeOffCalendarEventId = await provider.CreateEventAsync(connection, title, description: null, fromUtc, toUtc);
        timeOffCalendarProvider = providerKey;
        break; // a staff member only has one calendar connected in practice; first match wins
    }

    var timeOffId = await repo.CreateStaffTimeOffAsync(id, fromUtc, toUtc, req.Reason, timeOffCalendarProvider, timeOffCalendarEventId);

    // Anyone already booked with this stylist during the new time-off window needs to know —
    // their booking stays "Confirmed" (we don't auto-cancel or auto-pick a new time for them),
    // but the email points them at their manage-booking link to reschedule or cancel themselves.
    var affected = (await repo.GetBookingsInRangeAsync(id, fromUtc, toUtc)).ToList();
    var notifiedCount = 0;

    foreach (var affectedBooking in affected)
    {
        var customer = await repo.GetCustomerAsync(affectedBooking.CustomerId);
        if (customer is null) continue;
        try
        {
            var allServices = await repo.GetActiveServicesAsync();
            var items = await repo.GetBookingItemsAsync(affectedBooking.BookingId);
            var serviceNames = items.Select(i => allServices.FirstOrDefault(s => s.ServiceId == i.ServiceId)?.Name ?? "Service").ToList();

            await SendTimeOffRescheduleNeededEmailAsync(
                builder.Configuration, customer.Email, customer.Name, staff.DisplayName, serviceNames,
                affectedBooking.StartUtc, venueTimeZone, affectedBooking.ManageToken, req.Reason);
            notifiedCount++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Resend] Failed to send time-off notice for booking {affectedBooking.BookingId}: {ex.Message}");
        }
    }

    return Results.Ok(new { TimeOffId = timeOffId, AffectedBookingCount = affected.Count, NotifiedCount = notifiedCount });
}).RequireAuthorization();

app.MapDelete("/api/admin/staff/time-off/{timeOffId:int}", async (int timeOffId, BookingRepository repo, CalendarProviderFactory calendarFactory) =>
{
    var timeOff = await repo.GetStaffTimeOffByIdAsync(timeOffId);
    if (timeOff is { CalendarProvider: not null, CalendarEventId: not null })
    {
        var connection = await repo.GetCalendarConnectionAsync(timeOff.StaffId, timeOff.CalendarProvider);
        if (connection is not null)
        {
            var provider = calendarFactory.Get(timeOff.CalendarProvider);
            if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
            {
                connection = await provider.RefreshTokenAsync(connection);
                await repo.UpsertCalendarConnectionAsync(connection);
            }
            try { await provider.CancelEventAsync(connection, timeOff.CalendarEventId); }
            catch (Exception ex) { Console.WriteLine($"[Calendar] Failed to remove time-off event {timeOff.CalendarEventId}: {ex.Message}"); }
        }
    }

    await repo.DeleteStaffTimeOffAsync(timeOffId);
    return Results.Ok();
}).RequireAuthorization();

// Photo upload — plain multipart form (not JSON), saved under wwwroot/uploads/staff so
// UseStaticFiles serves it directly. NOTE for deployment: this directory needs to survive
// container rebuilds (a Docker volume mount), or an uploaded photo vanishes on the next deploy —
// see README "Known simplifications".
app.MapPost("/api/admin/staff/{id:int}/photo", async (int id, HttpRequest request, BookingRepository repo, IWebHostEnvironment env) =>
{
    var staff = await repo.GetStaffByIdAsync(id);
    if (staff is null) return Results.NotFound();

    if (!request.HasFormContentType)
        return Results.BadRequest("Expected a multipart/form-data upload.");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("photo");
    if (file is null || file.Length == 0)
        return Results.BadRequest("No photo file provided.");

    const long maxBytes = 5 * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest("Photo must be 5MB or smaller.");

    var extensionByContentType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };
    if (!extensionByContentType.TryGetValue(file.ContentType, out var extension))
        return Results.BadRequest("Photo must be a JPEG, PNG, or WEBP image.");

    var uploadsDir = Path.Combine(env.WebRootPath, "uploads", "staff");
    Directory.CreateDirectory(uploadsDir);

    // Unique filename per upload (not staffId+extension) so browsers don't cache-serve the old
    // photo after a replace — the URL actually changes.
    var fileName = $"{id}-{Guid.NewGuid():N}{extension}";
    var filePath = Path.Combine(uploadsDir, fileName);
    await using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    // Best-effort cleanup of the old file — only if it's one we uploaded ourselves (never touch
    // an externally-set PhotoUrl).
    if (staff.PhotoUrl is { } oldUrl && oldUrl.StartsWith("/uploads/staff/"))
    {
        var oldPath = Path.Combine(env.WebRootPath, oldUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { /* not worth failing the upload over */ }
    }

    var photoUrl = $"/uploads/staff/{fileName}";
    await repo.SetStaffPhotoUrlAsync(id, photoUrl);

    return Results.Ok(new { PhotoUrl = photoUrl });
}).RequireAuthorization();

app.MapDelete("/api/admin/staff/{id:int}/photo", async (int id, BookingRepository repo, IWebHostEnvironment env) =>
{
    var staff = await repo.GetStaffByIdAsync(id);
    if (staff is null) return Results.NotFound();

    if (staff.PhotoUrl is { } url && url.StartsWith("/uploads/staff/"))
    {
        var path = Path.Combine(env.WebRootPath, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    await repo.SetStaffPhotoUrlAsync(id, null);
    return Results.Ok();
}).RequireAuthorization();

// ── Calendar OAuth (admin-side: staff linking their own calendar) ─────────
// Protected the same way as the rest of the admin surface — connect/callback ride on the same
// browser session that started the OAuth flow, so the admin cookie is present on the redirect
// back from Google/Microsoft too.

app.MapGet("/api/calendar/{provider}/connect", (string provider, int staffId, CalendarProviderFactory calendarFactory) =>
{
    var cal = calendarFactory.Get(provider);
    var state = staffId.ToString();
    return Results.Redirect(cal.BuildAuthorizationUrl(staffId, state));
}).RequireAuthorization();

app.MapGet("/api/calendar/{provider}/callback", async (string provider, string code, string state, CalendarProviderFactory calendarFactory, BookingRepository repo) =>
{
    var staffId = int.Parse(state);
    var cal = calendarFactory.Get(provider);
    var connection = await cal.ExchangeCodeAsync(staffId, code);
    await repo.UpsertCalendarConnectionAsync(connection);
    return Results.Redirect($"/admin.html?connected={provider}&staffId={staffId}");
}).RequireAuthorization();

app.MapGet("/api/calendar/status", async (int staffId, BookingRepository repo) =>
{
    var connections = await repo.GetCalendarConnectionsForStaffAsync(staffId);
    return Results.Ok(connections.Select(c => new { c.Provider, c.ExternalAccountEmail, c.ConnectedAt, c.CalendarId }));
}).RequireAuthorization();

// Lists the account's calendars so the staff member can pick which one to use — accounts often
// have more than one (e.g. a personal calendar and a separate work one).
app.MapGet("/api/calendar/{provider}/calendars", async (string provider, int staffId, CalendarProviderFactory calendarFactory, BookingRepository repo) =>
{
    var connection = await repo.GetCalendarConnectionAsync(staffId, provider);
    if (connection is null)
        return Results.NotFound($"Staff member {staffId} hasn't connected {provider} yet.");

    var cal = calendarFactory.Get(provider);
    if (connection.TokenExpiresUtc < DateTime.UtcNow.AddMinutes(2))
    {
        connection = await cal.RefreshTokenAsync(connection);
        await repo.UpsertCalendarConnectionAsync(connection);
    }

    var calendars = await cal.ListCalendarsAsync(connection);
    return Results.Ok(new { SelectedCalendarId = connection.CalendarId, Calendars = calendars });
}).RequireAuthorization();

app.MapPost("/api/calendar/{provider}/select", async (string provider, SelectCalendarRequest req, BookingRepository repo) =>
{
    await repo.SetCalendarIdAsync(req.StaffId, provider, req.CalendarId);
    return Results.Ok();
}).RequireAuthorization();

app.Run();

// Format-only check via MailAddress, plus requiring a '.' in the domain — MailAddress alone
// happily accepts "a@b" with no TLD, which is never a real deliverable address.
static bool IsValidEmail(string email)
{
    if (string.IsNullOrWhiteSpace(email)) return false;
    try
    {
        var addr = new MailAddress(email);
        return addr.Host.Contains('.');
    }
    catch (FormatException)
    {
        return false;
    }
}

// Minimal iCalendar (RFC 5545) VEVENT, built by hand rather than pulling in a library for one
// event type. No line-folding at 75 octets (fine for our short summaries/descriptions — a real
// production version handling long text should fold long lines per the spec).
static string BuildIcsContent(
    string uid, DateTime startUtc, DateTime endUtc, string summary, string? description,
    int sequence, string method = "PUBLISH", string status = "CONFIRMED")
{
    static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    var sb = new StringBuilder();
    sb.Append("BEGIN:VCALENDAR\r\n");
    sb.Append("VERSION:2.0\r\n");
    sb.Append("PRODID:-//SlotSmith//Booking//EN\r\n");
    sb.Append($"METHOD:{method}\r\n");
    sb.Append("CALSCALE:GREGORIAN\r\n");
    sb.Append("BEGIN:VEVENT\r\n");
    sb.Append($"UID:{uid}\r\n");
    sb.Append($"DTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}\r\n");
    sb.Append($"DTSTART:{startUtc:yyyyMMddTHHmmssZ}\r\n");
    sb.Append($"DTEND:{endUtc:yyyyMMddTHHmmssZ}\r\n");
    sb.Append($"SUMMARY:{Escape(summary)}\r\n");
    if (!string.IsNullOrWhiteSpace(description))
        sb.Append($"DESCRIPTION:{Escape(description)}\r\n");
    sb.Append($"SEQUENCE:{sequence}\r\n");
    sb.Append($"STATUS:{status}\r\n");
    sb.Append("END:VEVENT\r\n");
    sb.Append("END:VCALENDAR\r\n");
    return sb.ToString();
}

// ── Email (Resend) ──────────────────────────────────────────────────────
// Same provider/pattern as OceanSwimmer.Api — plain REST call, no SDK. Reuses the same
// Resend account; only the API key + from-address need to be set per deployment.

async Task SendBookingConfirmationEmailAsync(
    IConfiguration config, string toEmail, string customerName, string staffName,
    List<string> serviceNames, DateTime startUtc, DateTime endUtc, TimeZoneInfo venueTimeZone, int totalPriceCents,
    string manageToken)
{
    var apiKey    = config["Resend:ApiKey"];
    var fromEmail = config["Resend:FromEmail"] ?? "noreply@mihoknows.com.au";
    var fromName  = config["Resend:FromName"]  ?? "SlotSmith Bookings";
    var baseUrl   = (config["App:BaseUrl"] ?? "https://booking.mihoknows.com.au").TrimEnd('/');

    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, venueTimeZone);
    var whenText = startLocal.ToString("dddd d MMMM, h:mm tt");
    var servicesText = string.Join(", ", serviceNames);
    // Hardcode "$" rather than ToString("C") — that formats using CurrentCulture, and the droplet's
    // Linux container has no culture data configured, so CurrentCulture falls back to invariant,
    // whose currency symbol is "¤" (the generic placeholder), not "$". Matches booking.js's fmtMoney.
    var priceText = "$" + (totalPriceCents / 100m).ToString("0.00");
    var manageUrl = $"{baseUrl}/manage-booking.html?token={manageToken}";

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
    {
        // Resend not configured — log instead, so local dev still works without a live key.
        Console.WriteLine($"[DEV] Booking confirmation for {toEmail}: {servicesText} with {staffName} on {whenText} ({priceText}). Manage: {manageUrl}");
        return;
    }

    var businessName = config["App:BusinessName"] ?? "SlotSmith";
    var ics = BuildIcsContent(
        uid: $"{manageToken}@slotsmith",
        startUtc: startUtc, endUtc: endUtc,
        summary: $"{servicesText} at {businessName}",
        description: $"With {staffName}. Manage this booking: {manageUrl}",
        sequence: 0);
    var icsAttachment = ("appointment.ics", Convert.ToBase64String(Encoding.UTF8.GetBytes(ics)));

    await SendResendEmailAsync(apiKey, fromEmail, fromName, toEmail,
        subject: "Your booking is confirmed",
        text: $"Hi {customerName},\n\nYour booking is confirmed:\n\n{servicesText}\nWith {staffName}\n{whenText}\nTotal: {priceText}\n\nNeed to change or cancel? {manageUrl}\n\nSee you then!",
        html: $@"
            <p>Hi {customerName},</p>
            <p>Your booking is confirmed:</p>
            <table style=""margin:16px 0;font-size:15px;"">
                <tr><td style=""color:#888;padding-right:12px;"">Service</td><td>{servicesText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">With</td><td>{staffName}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">When</td><td>{whenText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">Total</td><td>{priceText}</td></tr>
            </table>
            <p><a href=""{manageUrl}"" style=""color:#0a58ca;"">Manage or cancel this booking</a></p>
            <p style=""color:#888;font-size:13px;"">Attached is a calendar file (.ics) you can add to Google, Outlook, Apple Calendar, etc.</p>
            <p style=""color:#888;font-size:13px;"">See you then!</p>",
        attachment: icsAttachment);
}

async Task SendBookingRescheduledEmailAsync(
    IConfiguration config, string toEmail, string customerName, string staffName,
    List<string> serviceNames, DateTime newStartUtc, DateTime newEndUtc, TimeZoneInfo venueTimeZone, string manageToken)
{
    var apiKey    = config["Resend:ApiKey"];
    var fromEmail = config["Resend:FromEmail"] ?? "noreply@mihoknows.com.au";
    var fromName  = config["Resend:FromName"]  ?? "SlotSmith Bookings";
    var baseUrl   = (config["App:BaseUrl"] ?? "https://booking.mihoknows.com.au").TrimEnd('/');

    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(newStartUtc, venueTimeZone);
    var whenText = startLocal.ToString("dddd d MMMM, h:mm tt");
    var servicesText = string.Join(", ", serviceNames);
    var manageUrl = $"{baseUrl}/manage-booking.html?token={manageToken}";

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
    {
        Console.WriteLine($"[DEV] Booking rescheduled for {toEmail}: {servicesText} with {staffName} — new time {whenText}. Manage: {manageUrl}");
        return;
    }

    var businessName = config["App:BusinessName"] ?? "SlotSmith";
    // Same UID as the original confirmation — calendar apps that recognise a matching UID
    // (with a higher SEQUENCE) update the existing event rather than creating a duplicate.
    // We don't track a real per-booking sequence counter, so this is a fixed "1" rather than
    // an incrementing count across multiple reschedules — good enough for a demo.
    var ics = BuildIcsContent(
        uid: $"{manageToken}@slotsmith",
        startUtc: newStartUtc, endUtc: newEndUtc,
        summary: $"{servicesText} at {businessName}",
        description: $"With {staffName}. Manage this booking: {manageUrl}",
        sequence: 1);
    var icsAttachment = ("appointment.ics", Convert.ToBase64String(Encoding.UTF8.GetBytes(ics)));

    await SendResendEmailAsync(apiKey, fromEmail, fromName, toEmail,
        subject: "Your booking has been rescheduled",
        text: $"Hi {customerName},\n\nYour booking has been rescheduled:\n\n{servicesText}\nWith {staffName}\nNew time: {whenText}\n\nNeed to change again? {manageUrl}\n\nSee you then!",
        html: $@"
            <p>Hi {customerName},</p>
            <p>Your booking has been rescheduled:</p>
            <table style=""margin:16px 0;font-size:15px;"">
                <tr><td style=""color:#888;padding-right:12px;"">Service</td><td>{servicesText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">With</td><td>{staffName}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">New time</td><td>{whenText}</td></tr>
            </table>
            <p><a href=""{manageUrl}"" style=""color:#0a58ca;"">Manage or cancel this booking</a></p>
            <p style=""color:#888;font-size:13px;"">Attached is an updated calendar file (.ics) — re-adding it will update the event in most calendar apps.</p>
            <p style=""color:#888;font-size:13px;"">See you then!</p>",
        attachment: icsAttachment);
}

// Sent when an admin adds staff time off that overlaps a booking already on the books. Doesn't
// touch the booking's status or pick a new time automatically — it stays "Confirmed" at the old
// (now-unavailable) time until the customer acts on the manage-booking link themselves.
async Task SendTimeOffRescheduleNeededEmailAsync(
    IConfiguration config, string toEmail, string customerName, string staffName,
    List<string> serviceNames, DateTime startUtc, TimeZoneInfo venueTimeZone, string manageToken, string? reason)
{
    var apiKey    = config["Resend:ApiKey"];
    var fromEmail = config["Resend:FromEmail"] ?? "noreply@mihoknows.com.au";
    var fromName  = config["Resend:FromName"]  ?? "SlotSmith Bookings";
    var baseUrl   = (config["App:BaseUrl"] ?? "https://booking.mihoknows.com.au").TrimEnd('/');

    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, venueTimeZone);
    var whenText = startLocal.ToString("dddd d MMMM, h:mm tt");
    var servicesText = string.Join(", ", serviceNames);
    var manageUrl = $"{baseUrl}/manage-booking.html?token={manageToken}";
    var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})";

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
    {
        Console.WriteLine($"[DEV] Time-off notice for {toEmail}: {staffName} unavailable{reasonSuffix} for booking on {whenText}. Manage: {manageUrl}");
        return;
    }

    await SendResendEmailAsync(apiKey, fromEmail, fromName, toEmail,
        subject: $"{staffName} is unavailable for your upcoming appointment",
        text: $"Hi {customerName},\n\n{staffName} is unavailable{reasonSuffix} on {whenText}, when you're currently booked in for:\n\n{servicesText}\n\nPlease pick a new time here: {manageUrl}\n\nSorry for the inconvenience!",
        html: $@"
            <p>Hi {customerName},</p>
            <p>{staffName} is unavailable{reasonSuffix} on <strong>{whenText}</strong>, when you're currently booked in for:</p>
            <table style=""margin:16px 0;font-size:15px;"">
                <tr><td style=""color:#888;padding-right:12px;"">Service</td><td>{servicesText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">With</td><td>{staffName}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">Currently booked</td><td>{whenText}</td></tr>
            </table>
            <p><a href=""{manageUrl}"" style=""color:#0a58ca;"">Pick a new time or cancel</a></p>
            <p style=""color:#888;font-size:13px;"">Sorry for the inconvenience — see you soon!</p>");
}

async Task SendBookingCancelledEmailAsync(
    IConfiguration config, string toEmail, string customerName, string staffName,
    List<string> serviceNames, DateTime startUtc, TimeZoneInfo venueTimeZone)
{
    var apiKey    = config["Resend:ApiKey"];
    var fromEmail = config["Resend:FromEmail"] ?? "noreply@mihoknows.com.au";
    var fromName  = config["Resend:FromName"]  ?? "SlotSmith Bookings";
    var baseUrl   = (config["App:BaseUrl"] ?? "https://booking.mihoknows.com.au").TrimEnd('/');

    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, venueTimeZone);
    var whenText = startLocal.ToString("dddd d MMMM, h:mm tt");
    var servicesText = string.Join(", ", serviceNames);
    var bookingUrl = $"{baseUrl}/booking.html";

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
    {
        Console.WriteLine($"[DEV] Booking cancelled for {toEmail}: {servicesText} with {staffName} on {whenText}.");
        return;
    }

    await SendResendEmailAsync(apiKey, fromEmail, fromName, toEmail,
        subject: "Your booking has been cancelled",
        text: $"Hi {customerName},\n\nYour booking has been cancelled:\n\n{servicesText}\nWith {staffName}\n{whenText}\n\nChanged your mind? Book again: {bookingUrl}",
        html: $@"
            <p>Hi {customerName},</p>
            <p>Your booking has been cancelled:</p>
            <table style=""margin:16px 0;font-size:15px;"">
                <tr><td style=""color:#888;padding-right:12px;"">Service</td><td>{servicesText}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">With</td><td>{staffName}</td></tr>
                <tr><td style=""color:#888;padding-right:12px;"">Was</td><td>{whenText}</td></tr>
            </table>
            <p><a href=""{bookingUrl}"" style=""color:#0a58ca;"">Book a new appointment</a></p>");
}

async Task SendResendEmailAsync(string apiKey, string fromEmail, string fromName,
    string toEmail, string subject, string text, string html,
    (string Filename, string Base64Content)? attachment = null)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    // Resend's attachments field takes base64 content + a filename; it doesn't let you override
    // the Content-Type it infers (no way to force "text/calendar" for .ics), but every major mail
    // client (Gmail, Outlook, Apple Mail) still recognises a .ics attachment by extension and
    // offers an "Add to calendar" action regardless.
    var body = new Dictionary<string, object?>
    {
        ["from"]    = $"{fromName} <{fromEmail}>",
        ["to"]      = new[] { toEmail },
        ["subject"] = subject,
        ["text"]    = text,
        ["html"]    = html,
    };
    if (attachment is not null)
    {
        body["attachments"] = new[]
        {
            new { filename = attachment.Value.Filename, content = attachment.Value.Base64Content }
        };
    }
    var payload = JsonSerializer.Serialize(body);

    var response = await http.PostAsync(
        "https://api.resend.com/emails",
        new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[Resend] Error {(int)response.StatusCode}: {errorBody}");
    }
}

record AvailabilityRequest(List<int> ServiceIds, int? StaffId, string Date, string? ExcludeBookingToken = null);
record AvailabilityResponseSlot(int StaffId, DateTime StartUtc, DateTime EndUtc, int TotalPriceCents, int TotalDurationMinutes);
record SelectCalendarRequest(int StaffId, string CalendarId);
