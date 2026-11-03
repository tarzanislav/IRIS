using System.Globalization;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// When the computers may learn: on working days from a time in the afternoon until a time in the morning
/// (15:00 – 6:00 by default), and on Saturdays and Sundays all day when allowed. Times are the local time of the hub.
/// </summary>
public sealed record LabSchedule(TimeOnly From, TimeOnly Until, bool Weekends)
{
    public static LabSchedule Default { get; } = new(new TimeOnly(15, 0), new TimeOnly(6, 0), Weekends: true);

    /// <summary>Whether learning may run at this local time.</summary>
    public bool IsOpen(DateTime local)
    {
        if (Weekends && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(local);
        return From == Until || (From < Until ? time >= From && time < Until : time >= From || time < Until);
    }

    /// <summary>How long until the state changes (open → closed or closed → open), to the minute, at most a week.</summary>
    public TimeSpan UntilChange(DateTime local)
    {
        var open = IsOpen(local);
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Kind).AddMinutes(1);
        for (var t = start; t < local.AddDays(7); t = t.AddMinutes(1))
        {
            if (IsOpen(t) != open)
            {
                return t - local;
            }
        }

        return TimeSpan.FromDays(7);
    }

    public JsonObject ToJson() => new()
    {
        ["from"] = From.ToString("HH:mm", CultureInfo.InvariantCulture),
        ["until"] = Until.ToString("HH:mm", CultureInfo.InvariantCulture),
        ["weekends"] = Weekends,
    };

    public static LabSchedule FromJson(JsonObject? json) => json is null ? Default : new(
        TimeOnly.ParseExact(json["from"]?.GetValue<string>() ?? "15:00", "HH:mm", CultureInfo.InvariantCulture),
        TimeOnly.ParseExact(json["until"]?.GetValue<string>() ?? "06:00", "HH:mm", CultureInfo.InvariantCulture),
        json["weekends"]?.GetValue<bool>() ?? true);
}
