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
    private const string Scope = "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.readonly";

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
        resp.EnsureSuccessStatusCode();
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
            TokenExpiresUtc: DateTime.UtcNow.AddSeconds(token.ExpiresIn));
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
        resp.EnsureSuccessStatusCode();
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
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var body = new
        {
            timeMin = fromUtc.ToString("o"),
            timeMax = toUtc.ToString("o"),
            items = new[] { new { id = "primary" } }
        };
        var resp = await http.PostAsJsonAsync($"{ApiBase}/freeBusy", body, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var result = new List<BusyInterval>();
        if (doc.TryGetProperty("calendars", out var cals) &&
            cals.TryGetProperty("primary", out var primary) &&
            primary.TryGetProperty("busy", out var busy))
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
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));

        var body = new
        {
            summary = title,
            description,
            start = new { dateTime = startUtc.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = endUtc.ToString("o"), timeZone = "UTC" }
        };
        var resp = await http.PostAsJsonAsync($"{ApiBase}/calendars/primary/events", body, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc.GetProperty("id").GetString()!;
    }

    public async Task CancelEventAsync(CalendarConnection connection, string eventId, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _protector.Unprotect(connection.AccessTokenEncrypted));
        var resp = await http.DeleteAsync($"{ApiBase}/calendars/primary/events/{eventId}", ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Gone)
            resp.EnsureSuccessStatusCode();
    }

    private class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
