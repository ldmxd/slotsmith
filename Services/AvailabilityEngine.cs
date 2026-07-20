using SlotSmith.Api.Models;

namespace SlotSmith.Api.Services;

/// <summary>
/// Turns (business hours) - (calendar busy times) - (existing bookings) into a list of
/// bookable slots for a given staff member and service duration. Pure function, no I/O —
/// callers fetch busy times / bookings first and pass them in, which keeps this testable
/// without a live database or calendar connection.
/// </summary>
public static class AvailabilityEngine
{
    /// <param name="dateLocal">The calendar day to compute slots for, in venue-local time.</param>
    /// <param name="hours">Open/close time for that day of week, or null open/close if closed.</param>
    /// <param name="durationMinutes">Requested service duration.</param>
    /// <param name="busy">Merged busy intervals (calendar events + existing bookings), in UTC.</param>
    /// <param name="venueTimeZone">IANA/Windows tz id the venue operates in, e.g. "Australia/Sydney".</param>
    /// <param name="slotGranularityMinutes">Step between candidate start times, e.g. 15.</param>
    /// <param name="now">Current UTC instant, so past slots today are excluded.</param>
    public static List<AvailabilitySlot> ComputeSlots(
        DateOnly dateLocal,
        BusinessHours hours,
        int durationMinutes,
        IReadOnlyList<BusyInterval> busy,
        TimeZoneInfo venueTimeZone,
        int slotGranularityMinutes,
        DateTime now)
    {
        var slots = new List<AvailabilitySlot>();

        if (hours.OpenTime is null || hours.CloseTime is null)
            return slots; // closed that day

        var openLocal = dateLocal.ToDateTime(TimeOnly.FromTimeSpan(hours.OpenTime.Value));
        var closeLocal = dateLocal.ToDateTime(TimeOnly.FromTimeSpan(hours.CloseTime.Value));

        var openUtc = TimeZoneInfo.ConvertTimeToUtc(openLocal, venueTimeZone);
        var closeUtc = TimeZoneInfo.ConvertTimeToUtc(closeLocal, venueTimeZone);

        var duration = TimeSpan.FromMinutes(durationMinutes);
        var step = TimeSpan.FromMinutes(slotGranularityMinutes);

        var merged = MergeIntervals(busy);

        for (var start = openUtc; start + duration <= closeUtc; start += step)
        {
            if (start < now) continue; // don't offer slots in the past

            var end = start + duration;
            if (!Overlaps(start, end, merged))
                slots.Add(new AvailabilitySlot(start, end));
        }

        return slots;
    }

    private static bool Overlaps(DateTime start, DateTime end, IReadOnlyList<BusyInterval> merged)
    {
        foreach (var b in merged)
        {
            if (start < b.EndUtc && end > b.StartUtc)
                return true;
        }
        return false;
    }

    private static List<BusyInterval> MergeIntervals(IReadOnlyList<BusyInterval> intervals)
    {
        if (intervals.Count == 0) return new List<BusyInterval>();

        var sorted = intervals.OrderBy(i => i.StartUtc).ToList();
        var merged = new List<BusyInterval> { sorted[0] };

        foreach (var current in sorted.Skip(1))
        {
            var last = merged[^1];
            if (current.StartUtc <= last.EndUtc)
                merged[^1] = last with { EndUtc = current.EndUtc > last.EndUtc ? current.EndUtc : last.EndUtc };
            else
                merged.Add(current);
        }

        return merged;
    }
}
