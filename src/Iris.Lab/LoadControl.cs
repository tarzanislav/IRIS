using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// The moving load: between the load the console set (share of time and of cores) and the most it allows, a
/// computer finds by itself how much it can give. While it is calm – no strain, nothing else using the processor,
/// nobody at the keyboard, cool – it adds an eighth of the way every two minutes; at the first sign of trouble it
/// gives back half of what it took (at most every half minute) and waits five minutes before it adds again. Adding
/// slowly and giving back fast keeps it from swinging, and the set load is always the floor: the console's choice is
/// what the computer does when it cannot do more.
/// Given the lines it learns, it also looks for the load that learns most: it measures the lines per second at a
/// level before it adds, and when the step up learns fewer (a small processor that slows down when it heats up at
/// full load) it goes back and does not go past that level for two hours. A thread that learns a little slower
/// because there are more of them is no trouble – only fewer lines in all are.
/// </summary>
public sealed class LoadControl
{
    /// <summary>How long it must be calm before it adds, and between two additions.</summary>
    public static readonly TimeSpan Calm = TimeSpan.FromMinutes(2);

    /// <summary>How long it waits after giving back before it adds again.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    /// <summary>The least time between two steps back.</summary>
    public static readonly TimeSpan Back = TimeSpan.FromSeconds(30);

    /// <summary>One addition, as a share of the way from the set load to the most allowed.</summary>
    public const double Up = 0.125;

    /// <summary>Degrees below the limit of temperature where it stops adding and gives back.</summary>
    public const double WarmMargin = 8;

    /// <summary>The processor share of other programs from which it gives back (at the agent's limit it stops anyway).</summary>
    public const double Others = 15;

    /// <summary>Seconds since the last input under which someone is taken to be at the computer.</summary>
    public const double Present = 120;

    /// <summary>How long it measures the lines per second at a level before it compares.</summary>
    public static readonly TimeSpan Measure = TimeSpan.FromMinutes(3);

    /// <summary>How long a level that learned fewer lines stays the top.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromHours(2);

    /// <summary>How much fewer lines a step up may learn before it is taken back (the measuring is not exact).</summary>
    public const double Slower = 0.04;

    // a look later than this after the one before (the learning paused between rounds): the measuring starts again
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(45);

    private DateTime _changed = DateTime.MinValue;
    private DateTime _lastLook = DateTime.MinValue;
    private DateTime _measuredFrom = DateTime.MinValue;
    private long _linesFrom;
    private double? _below;           // lines per second at the level before the last step up
    private double _belowLevel;
    private double _top = 1;
    private DateTime _topUntil = DateTime.MinValue;
    private DateTime _calmSince = DateTime.MinValue;
    private DateTime _waitUntil = DateTime.MinValue;

    /// <summary>Where it is: 0 = the set load, 1 = the most allowed.</summary>
    public double Level { get; private set; }

    /// <summary>Why it does not add now (strain, others, user, warm, slower – the step up learned fewer lines), or null while it may.</summary>
    public string? Holding { get; private set; }

    /// <summary>Lines per second at the present level (the last measuring), or null while not measured.</summary>
    public double? Speed { get; private set; }

    /// <summary>The highest level it may go to now (lower for a while after a step up learned fewer lines).</summary>
    public double Top => _top;

    /// <summary>
    /// One look at the computer while it learns (every few seconds); returns whether the level changed. strain is the
    /// level of the strain judge (0 calm, 1 busy, 2 struggling) – without the learning's own slowing down, temperature
    /// and idle seconds null where unknown, lines all lines learned so far (null: it does not look for the best load).
    /// </summary>
    public bool Look(DateTime now, int strain, double cpuOthers, double? temperature, double hot, double? idleSeconds, long? lines = null)
    {
        if (lines is { } count)
        {
            if (now - _lastLook > Gap || _measuredFrom == DateTime.MinValue)
            {
                (_measuredFrom, _linesFrom) = (now, count);
            }

            _lastLook = now;
            Speed = now - _measuredFrom >= Measure ? (count - _linesFrom) / (now - _measuredFrom).TotalSeconds : null;
            if (now >= _topUntil)
            {
                _top = 1;
            }

            // the step up learned fewer lines than the level below: back, and not past it for a while
            if (_below is { } below && Speed is { } speed && Level > _belowLevel)
            {
                if (speed < below * (1 - Slower))
                {
                    _top = _belowLevel;
                    _topUntil = now + Ceiling;
                    Level = _belowLevel;
                    Holding = "slower";
                    Moved(now, count);
                    _below = null;
                    return true;
                }

                _below = null;
            }
        }

        Holding = strain >= 1 ? "strain"
            : cpuOthers >= Others ? "others"
            : idleSeconds is { } idle && idle < Present ? "user"
            : temperature is { } t && t >= hot - WarmMargin ? "warm"
            : null;
        if (_calmSince == DateTime.MinValue)
        {
            _calmSince = now;
        }

        if (Holding is not null)
        {
            _calmSince = now;
            if (Level > 0 && now - _changed >= Back)
            {
                Level = Level <= Up / 2 ? 0 : Level / 2;
                _changed = now;
                _waitUntil = now + Wait;
                _below = null;
                Moved(now, lines);
                return true;
            }

            return false;
        }

        var measured = lines is null || Speed is not null;
        if (Level < _top && measured && now - _calmSince >= Calm && now - _changed >= Calm && now >= _waitUntil)
        {
            (_below, _belowLevel) = (Speed, Level);
            Level = Math.Min(_top, Level + Up);
            _changed = now;
            Moved(now, lines);
            return true;
        }

        if (Level >= _top && _top < 1)
        {
            Holding = "slower";
        }

        return false;
    }

    // a new level: the measuring of its lines per second starts
    private void Moved(DateTime now, long? lines)
    {
        _changed = now;
        if (lines is { } count)
        {
            (_measuredFrom, _linesFrom) = (now, count);
            Speed = null;
        }
    }

    /// <summary>Back to the set load (the moving load switched off, or new settings).</summary>
    public void Reset()
    {
        Level = 0;
        Holding = null;
        Speed = _below = null;
        _top = 1;
        _changed = _calmSince = _waitUntil = _lastLook = _measuredFrom = _topUntil = DateTime.MinValue;
    }

    /// <summary>
    /// The share of time and the threads at the present level: both go from the set value to the most allowed together.
    /// Beyond the set threads it stays within the physical cores – two threads of one core add little and warm it –
    /// and where the cores have no second thread, one core is left to the drawing of lines and the system (a learning
    /// thread pushed off its core holds up all the others).
    /// </summary>
    public (double Duty, int Threads) Load(LoadSettings settings, int logical, int? physical)
    {
        var duty = settings.Duty + (Level * Math.Max(0, settings.DutyMax - settings.Duty));
        var cores = settings.Cores + (Level * Math.Max(0, settings.CoresMax - settings.Cores));
        var floor = Threads(settings.Cores, logical);
        var threads = Threads(cores, logical);
        if (physical is { } p && p > 0)
        {
            var cap = logical > p ? p : Math.Max(1, p - 1);
            threads = Math.Min(threads, Math.Max(floor, cap));
        }

        return (Math.Clamp(duty, 0.05, 1), Math.Max(floor, threads));
    }

    /// <summary>The threads of a share of the logical processors (at least one).</summary>
    public static int Threads(double cores, int logical) => Math.Max(1, (int)Math.Floor(logical * cores));

    public JsonObject ToJson() => new()
    {
        ["level"] = Math.Round(Level, 3),
        ["holding"] = Holding,
        ["speed"] = Speed is { } s ? Math.Round(s, 1) : null,
        ["top"] = Math.Round(_top, 3),
    };
}

/// <summary>The load the console set for a computer: the share of time and of cores, and with the moving load the most of both.</summary>
public sealed record LoadSettings(double Duty, double Cores, bool Moving = false, double DutyMax = 1, double CoresMax = 1)
{
    public static LoadSettings From(JsonObject settings, LoadSettings? before = null)
    {
        before ??= new LoadSettings(0.5, 0.5);
        var duty = Math.Clamp(settings["duty"]?.GetValue<double>() ?? before.Duty, 0.05, 1);
        var cores = Math.Clamp(settings["cores"]?.GetValue<double>() ?? before.Cores, 0.05, 1);
        return new LoadSettings(duty, cores,
            settings["moving"]?.GetValue<bool>() ?? before.Moving,
            Math.Clamp(settings["duty_max"]?.GetValue<double>() ?? before.DutyMax, duty, 1),
            Math.Clamp(settings["cores_max"]?.GetValue<double>() ?? before.CoresMax, cores, 1));
    }
}
