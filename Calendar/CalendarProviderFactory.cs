namespace SlotSmith.Api.Calendar;

/// <summary>Resolves the right ICalendarProvider by the string stored in dbo.CalendarConnection.Provider.</summary>
public class CalendarProviderFactory
{
    private readonly Dictionary<string, ICalendarProvider> _providers;

    public CalendarProviderFactory(IEnumerable<ICalendarProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);
    }

    public ICalendarProvider Get(string providerKey) =>
        _providers.TryGetValue(providerKey, out var p)
            ? p
            : throw new InvalidOperationException($"Unknown calendar provider '{providerKey}'");

    public IReadOnlyCollection<string> SupportedProviders => _providers.Keys;
}
