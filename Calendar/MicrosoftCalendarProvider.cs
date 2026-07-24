using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SlotSmith.Api.Models;

namespace SlotSmith.Api.Calendar;

/// <summary>
/// Outlook / Microsoft 365 calendar via plain REST against Microsoft Graph (no Graph SDK,
/// same rationale as GoogleCalendarProvider — fewer dependencies, transparent token flow).
///
/// Setup required in Entra (Azure AD) admin center (see README.md "Outlook setup"):
///   1. App registrations → New registration. Supported account types: "Accounts in any
///      organizational directory and personal Microsoft accounts" (so it works with a
///      plain outlook.com/hotmail account too, not just a work 365 tenant).
///      Redirect URI (Web): https://mihoknows.com.au/api/calendar/microsoft/callback
///   2. Certificates & secrets → new client secret.
///   3. API permissions → Microsoft Graph → Delegated → Calendars.ReadWrite, offline_access,
///      User.Read. Grant admin consent if the tenant requires it (personal accounts don't).
/// </summary>
public class MicrosoftCalendarProvider : ICalendarProvider
{
    public string ProviderKey => "Microsoft";

    private const string AuthEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string Scope = "offline_access Calendars.ReadWrite User.Read";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;

    public MicrosoftCalendarProvider(IHttpClientFactory httpFactory, IConfiguration config, IDataProtectionProvider dpProvider)
    {
        _httpFactory = httpFactory;
        _config = config;
        _protector = dpProvider.CreateProtector("SlotSmith.Api.CalendarTokens.Microsoft");
    }

    private string ClientId => _config["Calendar:Microsoft:ClientId"]
        ?? throw new InvalidOperationException("Missing Calendar:Microsoft:ClientId");
    private string ClientSecret => _config["Calendar:Microsoft:ClientSecret"]
        ?? throw new InvalidOperationException("Missing Calendar:Microsoft:ClientSecret");
    private string RedirectUri => _config["Calendar:Microsoft:RedirectUri"]
        ?? throw new InvalidOperationException("Missing Calendar:Microsoft:RedirectUri");

    public string BuildAuthorizationUrl(int staffId, string state)
    {
        var qs = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = Scope,
            ["state"] = state
        };
        return AuthEndpoint + "?" + string.Join("&", qs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>resp.EnsureSuccessStatusCode() throws without ever reading the body, which is where
    /// Microsoft actually explains what went wrong. This reads the body first so failures are
    /// diagnosable instead of a bare "400 Bad Request".</summary>
    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Microsoft Graph call failed ({(int)resp.StatusCode} {resp.StatusCode}): {body}");
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
            ["grant_type"] = "authorization_code",
            ["scope"] = Scope
        }), ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var token = await resp.Content.ReadFromJsonAsync<MsTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty token response from Microsoft");

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
            CalendarId: null);   // defaults to the mailbox's default calendar until picked
    }

    private async Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        var resp = await http.GetAsync($"{GraphBase}/me", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (doc.TryGetProperty("mail", out var mail) && mail.GetString() is { Length: > 0 } m) return m;
        return doc.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null;
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
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        }), ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var token = await resp.Content.ReadFromJsonAsync<MsTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty refresh response from Microsoft");

        return connection with
        {
            AccessTokenEncrypted = _protector.Protect(token.AccessToken!),
            // Microsoft rotates refresh tokens on every use — persist the new one, don't keep the old.
            RefreshTokenEncrypted = string.IsNullOrEmpty(token.RefreshToken)
                ? connection.RefreshTokenEncrypted
                : _protector.Protect(token.RefreshToken),
            TokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn)
        };
    }

    /// <summary>Base Graph path for this connection's chosen calendar, or the mailbox's default
    /// calendar (plain "/me/...") if none has been picked yet.</summary>
    private static string CalendarBase(CalendarConnection connection) =>
        connection.CalendarId is null
            ? $"{GraphBase}/me"
            : $"{GraphBase}/me/calendars/{Uri.EscapeDataString(connection.CalendarId)}";

    public async Task<List<BusyInterval>> GetBusyTimesAsync(CalendarConnection connection, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));
        // Prefer header pins the response to UTC regardless of the mailbox's configured timezone.
        http.DefaultRequestHeaders.Add("Prefer", "outlook.timezone=\"UTC\"");

        var url = $"{CalendarBase(connection)}/calendarView?startDateTime={Uri.EscapeDataString(fromUtc.ToString("o"))}" +
                  $"&endDateTime={Uri.EscapeDataString(toUtc.ToString("o"))}&$select=start,end,showAs&$top=250";
        var resp = await http.GetAsync(url, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var result = new List<BusyInterval>();
        if (doc.TryGetProperty("value", out var events))
        {
            foreach (var ev in events.EnumerateArray())
            {
                var showAs = ev.TryGetProperty("showAs", out var sa) ? sa.GetString() : "busy";
                if (showAs == "free") continue; // stylist marked themselves available during this event

                var start = DateTime.Parse(ev.GetProperty("start").GetProperty("dateTime").GetString()! + "Z").ToUniversalTime();
                var end = DateTime.Parse(ev.GetProperty("end").GetProperty("dateTime").GetString()! + "Z").ToUniversalTime();
                result.Add(new BusyInterval(start, end));
            }
        }
        return result;
    }

    public async Task<string> CreateEventAsync(CalendarConnection connection, string title, string? description, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var body = new
        {
            subject = title,
            body = new { contentType = "Text", content = description ?? "" },
            start = new { dateTime = startUtc.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = endUtc.ToString("o"), timeZone = "UTC" }
        };
        var resp = await http.PostAsJsonAsync($"{CalendarBase(connection)}/events", body, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc.GetProperty("id").GetString()!;
    }

    public async Task CancelEventAsync(CalendarConnection connection, string eventId, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));
        var resp = await http.DeleteAsync($"{CalendarBase(connection)}/events/{eventId}", ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            await EnsureSuccessWithBodyAsync(resp, ct);
    }

    public async Task<List<CalendarOption>> ListCalendarsAsync(CalendarConnection connection, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var resp = await http.GetAsync($"{GraphBase}/me/calendars?$select=id,name,isDefaultCalendar", ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var result = new List<CalendarOption>();
        if (doc.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                var isDefault = item.TryGetProperty("isDefaultCalendar", out var d) && d.GetBoolean();
                result.Add(new CalendarOption(id, name, isDefault));
            }
        }
        return result;
    }

    private class MsTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
