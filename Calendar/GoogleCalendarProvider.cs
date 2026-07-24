using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SlotSmith.Api.Models;

namespace SlotSmith.Api.Calendar;

/// <summary>
/// Google Calendar via plain REST + OAuth2 (no Google.Apis SDK — keeps the dependency
/// footprint small and the token flow transparent, consistent with how OceanSwimmer.Api
/// hand-rolls its OAuth handling rather than pulling in heavier client libraries).
///
/// Setup required in Google Cloud Console (see README.md "Google Calendar setup"):
///   1. Create a project, enable the Google Calendar API.
///   2. OAuth consent screen — External, Testing mode is fine for a demo (up to 100 test users).
///   3. Credentials → OAuth client ID → Web application.
///      Redirect URI: https://mihoknows.com.au/api/calendar/google/callback
///   4. Put the client id/secret in appsettings / env vars (see appsettings.json).
/// </summary>
public class GoogleCalendarProvider : ICalendarProvider
{
    public string ProviderKey => "Google";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ApiBase = "https://www.googleapis.com/calendar/v3";
    // calendar.events: create/edit/cancel the confirmed-booking event.
    // calendar.freebusy: read-only, scoped specifically to free/busy queries.
    // calendar.calendarlist.readonly: lets ListCalendarsAsync show the account's calendar names
    // so the staff member can pick which one to use — read-only, no event content access.
    // userinfo.email: just so GetAccountEmailAsync can show which account is connected in
    // admin.html instead of "unknown account" — doesn't grant anything beyond reading the address.
    // None of these is calendar.readonly, which would grant read access to every calendar's
    // actual event content — broader than anything this app does.
    private const string Scope = "https://www.googleapis.com/auth/calendar.events "
        + "https://www.googleapis.com/auth/calendar.freebusy "
        + "https://www.googleapis.com/auth/calendar.calendarlist.readonly "
        + "https://www.googleapis.com/auth/userinfo.email";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;

    public GoogleCalendarProvider(IHttpClientFactory httpFactory, IConfiguration config, IDataProtectionProvider dpProvider)
    {
        _httpFactory = httpFactory;
        _config = config;
        _protector = dpProvider.CreateProtector("SlotSmith.Api.CalendarTokens.Google");
    }

    private string ClientId => _config["Calendar:Google:ClientId"]
        ?? throw new InvalidOperationException("Missing Calendar:Google:ClientId");
    private string ClientSecret => _config["Calendar:Google:ClientSecret"]
        ?? throw new InvalidOperationException("Missing Calendar:Google:ClientSecret");
    private string RedirectUri => _config["Calendar:Google:RedirectUri"]
        ?? throw new InvalidOperationException("Missing Calendar:Google:RedirectUri");

    public string BuildAuthorizationUrl(int staffId, string state)
    {
        var qs = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",           // force refresh_token on every connect, not just the first
            ["state"] = state
        };
        return AuthEndpoint + "?" + string.Join("&", qs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>resp.EnsureSuccessStatusCode() throws without ever reading the body, which is where
    /// Google actually explains what went wrong (e.g. "redirect_uri_mismatch", "invalid_grant").
    /// This reads the body first so failures are diagnosable instead of a bare "400 Bad Request".</summary>
    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Google API call failed ({(int)resp.StatusCode} {resp.StatusCode}): {body}");
    }

    public async Task<CalendarConnection> ExchangeCodeAsync(int staffId, string code, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = RedirectUri,
            ["grant_type"] = "authorization_code"
        }), ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var token = await resp.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty token response from Google");

        var email = await GetAccountEmailAsync(token.AccessToken!, ct);

        return new CalendarConnection(
            CalendarConnectionId: 0,
            StaffId: staffId,
            Provider: ProviderKey,
            ExternalAccountEmail: email,
            AccessTokenEncrypted: _protector.Protect(token.AccessToken!),
            RefreshTokenEncrypted: _protector.Protect(token.RefreshToken ?? ""),
            TokenExpiresUtc: DateTime.UtcNow.AddSeconds(token.ExpiresIn),
            ConnectedAt: DateTime.UtcNow,
            CalendarId: null);   // defaults to "primary" until the staff member picks one via admin.html
    }

    private async Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        var resp = await http.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    public async Task<CalendarConnection> RefreshTokenAsync(CalendarConnection connection, CancellationToken ct = default)
    {
        var refreshToken = _protector.Unprotect(connection.RefreshTokenEncrypted);
        var http = _httpFactory.CreateClient();
        var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token"
        }), ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var token = await resp.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty refresh response from Google");

        return connection with
        {
            AccessTokenEncrypted = _protector.Protect(token.AccessToken!),
            TokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn)
        };
    }

    public async Task<List<BusyInterval>> GetBusyTimesAsync(CalendarConnection connection, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var calendarId = connection.CalendarId ?? "primary";
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var body = new
        {
            timeMin = fromUtc.ToString("o"),
            timeMax = toUtc.ToString("o"),
            items = new[] { new { id = calendarId } }
        };
        var resp = await http.PostAsJsonAsync($"{ApiBase}/freeBusy", body, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var result = new List<BusyInterval>();
        if (doc.TryGetProperty("calendars", out var cals) &&
            cals.TryGetProperty(calendarId, out var cal) &&
            cal.TryGetProperty("busy", out var busy))
        {
            foreach (var b in busy.EnumerateArray())
            {
                result.Add(new BusyInterval(
                    b.GetProperty("start").GetDateTime().ToUniversalTime(),
                    b.GetProperty("end").GetDateTime().ToUniversalTime()));
            }
        }
        return result;
    }

    public async Task<string> CreateEventAsync(CalendarConnection connection, string title, string? description, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var calendarId = connection.CalendarId ?? "primary";
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var body = new
        {
            summary = title,
            description,
            start = new { dateTime = startUtc.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = endUtc.ToString("o"), timeZone = "UTC" }
        };
        var resp = await http.PostAsJsonAsync($"{ApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc.GetProperty("id").GetString()!;
    }

    public async Task CancelEventAsync(CalendarConnection connection, string eventId, CancellationToken ct = default)
    {
        var calendarId = connection.CalendarId ?? "primary";
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));
        var resp = await http.DeleteAsync($"{ApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{eventId}", ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Gone)
            await EnsureSuccessWithBodyAsync(resp, ct);
    }

    public async Task<List<CalendarOption>> ListCalendarsAsync(CalendarConnection connection, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var resp = await http.GetAsync($"{ApiBase}/users/me/calendarList", ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var result = new List<CalendarOption>();
        if (doc.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var name = item.TryGetProperty("summary", out var s) ? s.GetString() ?? id : id;
                var isDefault = item.TryGetProperty("primary", out var p) && p.GetBoolean();
                result.Add(new CalendarOption(id, name, isDefault));
            }
        }
        return result;
    }

    private class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
