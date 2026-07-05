using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyGame.Core.World;

/// <summary>
/// In-game clock and calendar machinery.
///
/// Port of <c>engine/calendar.ts</c>. The TS module kept a single monotonic
/// <c>clockMinutes</c> counter and a separate <see cref="CalendarSpec"/>
/// describing how to render that counter as a human date. This C# port
/// preserves both pieces:
///  - <see cref="GameTime"/>: a SIMPLIFIED immutable record holding
///    (Day, Hour, Minute) — used by the desktop engine as the primary
///    in-world clock surface. <see cref="GameTime.Advance"/> returns a new
///    <see cref="GameTime"/>; <see cref="GameTime.Start"/> is day 1, 08:00.
///  - <see cref="CalendarSpec"/> + <see cref="Calendar.BreakDownClock"/>: the
///    full rich calendar (year/era/months/seasons/weekdays) ported from the
///    TS, so a world-builder can install a custom calendar and have it
///    round-trip through saves. <see cref="Calendar.FormatClock"/> /
///    <see cref="Calendar.FormatClockLong"/> render the rich form.
///
/// The desktop World holds a <see cref="GameTime"/> for the simple display
/// ("День 3, 14:30") the spec calls for; the richer CalendarSpec machinery
/// is available for the future world-builder subsystem.
/// </summary>
public static class Calendar
{
    /// <summary>
    /// Total days in one year of a calendar (sum of month lengths). Returns
    /// 0 for an empty calendar (defensive — should not happen for valid data).
    /// </summary>
    public static int DaysPerYear(CalendarSpec cal) =>
        cal.Months.Sum(m => m.Days);

    /// <summary>
    /// Convert elapsed-minutes-since-start into a full calendar date
    /// breakdown. Walks forward from
    /// (yearStart, startMonth, startDay, startHour:startMinute) by the given
    /// number of minutes, distributing across days/months/years. Handles
    /// variable-length months and year rollover correctly.
    ///
    /// Port of <c>breakDownClock</c> from <c>calendar.ts</c>.
    /// </summary>
    public static ClockBreakdown BreakDownClock(CalendarSpec cal, long clockMinutes)
    {
        // Absolute minutes from the start-of-day anchor (00:00 of startDay).
        int startDayMinutes = cal.StartHour * 60 + cal.StartMinute;
        long totalMinutes = startDayMinutes + Math.Max(0, clockMinutes);

        long remainingDays = totalMinutes / 1440;
        long dayMinute = totalMinutes % 1440;
        int hour = (int)(dayMinute / 60);
        int minute = (int)(dayMinute % 60);

        int year = cal.YearStart;
        int monthIdx = Math.Max(0, Math.Min(cal.Months.Count - 1, cal.StartMonth));
        int day = cal.StartDay;

        // Walk forward through months/years until remainingDays is absorbed.
        while (remainingDays > 0)
        {
            int monthLen = cal.Months[monthIdx].Days;
            int daysLeftInMonth = monthLen - day;
            if (remainingDays < daysLeftInMonth)
            {
                day += (int)remainingDays;
                remainingDays = 0;
            }
            else
            {
                remainingDays -= daysLeftInMonth;
                monthIdx += 1;
                if (monthIdx >= cal.Months.Count)
                {
                    monthIdx = 0;
                    year += 1;
                }
                day = 1;
                // Consume full months/years as long as remainingDays >= monthLen.
                while (remainingDays > 0)
                {
                    int len = cal.Months[monthIdx].Days;
                    if (remainingDays >= len)
                    {
                        remainingDays -= len;
                        monthIdx += 1;
                        if (monthIdx >= cal.Months.Count)
                        {
                            monthIdx = 0;
                            year += 1;
                        }
                    }
                    else
                    {
                        day += (int)remainingDays;
                        remainingDays = 0;
                    }
                }
            }
        }

        // Season lookup.
        string? seasonName = null;
        if (cal.Seasons is { } seasons)
        {
            foreach (var s in seasons)
            {
                if (s.Months.Contains(monthIdx))
                {
                    seasonName = s.Name;
                    break;
                }
            }
        }

        // Weekday.
        long totalDaysElapsed = (startDayMinutes + Math.Max(0, clockMinutes)) / 1440;
        string? weekDayName = null;
        if (cal.WeekDays is { } weekDays && weekDays.Count > 0)
        {
            int anchor = cal.StartWeekDayIndex ?? 0;
            int idx = (int)(((anchor + totalDaysElapsed) % weekDays.Count + weekDays.Count) % weekDays.Count);
            weekDayName = weekDays[idx];
        }

        return new ClockBreakdown(
            Year: year,
            MonthIndex: monthIdx,
            MonthName: cal.Months[monthIdx]?.Name ?? $"Месяц {monthIdx + 1}",
            Day: day,
            Hour: hour,
            Minute: minute,
            SeasonName: seasonName,
            WeekDayName: weekDayName,
            Era: cal.Era,
            TotalDays: totalDaysElapsed);
    }

    /// <summary>
    /// Render a clock instant as a compact, locale-flavoured string:
    /// <c>"1 Месяц Снегов 600 г., 08:00"</c>. Falls back to
    /// <see cref="DefaultFantasyCalendar"/> when <paramref name="cal"/> is null.
    /// </summary>
    public static string FormatClock(CalendarSpec? cal, long clockMinutes)
    {
        var c = cal ?? DefaultFantasyCalendar;
        var b = BreakDownClock(c, clockMinutes);
        return $"{b.Day} {b.MonthName} {b.Year} г., {b.Hour:D2}:{b.Minute:D2}";
    }

    /// <summary>Long form with weekday + era + season — for prompts / tooltips.</summary>
    public static string FormatClockLong(CalendarSpec? cal, long clockMinutes)
    {
        var c = cal ?? DefaultFantasyCalendar;
        var b = BreakDownClock(c, clockMinutes);
        var parts = new List<string>();
        if (b.WeekDayName is { } wd) parts.Add(wd + ",");
        parts.Add($"{b.Day} {b.MonthName} {b.Year} г.");
        if (b.Era is { } era) parts.Add($"({era})");
        parts.Add($"{b.Hour:D2}:{b.Minute:D2}");
        if (b.SeasonName is { } sn) parts.Add($"· {sn}");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Format a time-advancement divider label:
    /// <c>"⏱ прошло 30 мин · 14:30, Месяц Туманов, 600 г."</c>.
    /// </summary>
    public static string FormatTimeLabel(CalendarSpec? cal, long deltaMinutes, long totalMinutes)
    {
        var c = cal ?? DefaultFantasyCalendar;
        var b = BreakDownClock(c, totalMinutes);
        string dur;
        if (deltaMinutes >= 60)
        {
            long h = deltaMinutes / 60;
            long m = deltaMinutes % 60;
            dur = m > 0 ? $"{h} ч {m} мин" : $"{h} ч";
        }
        else
        {
            dur = $"{deltaMinutes} мин";
        }
        return $"⏱ прошло {dur} · {b.Hour:D2}:{b.Minute:D2}, {b.Day} {b.MonthName} {b.Year} г.";
    }

    /// <summary>
    /// Build a Gregorian-flavoured calendar for worlds set in the real /
    /// near-future world (Stalker, modern, post-apoc). Year start and era
    /// are caller-supplied. Port of <c>makeRealWorldCalendar</c>.
    /// </summary>
    public static CalendarSpec MakeRealWorldCalendar(
        int yearStart,
        string? era = null,
        int startMonth = 0,
        int startDay = 1,
        int startHour = 8,
        int startMinute = 0)
    {
        return new CalendarSpec
        {
            YearStart = yearStart,
            Era = era,
            Months = new List<CalendarMonth>
            {
                new("Январь", 31),
                new("Февраль", 28),
                new("Март", 31),
                new("Апрель", 30),
                new("Май", 31),
                new("Июнь", 30),
                new("Июль", 31),
                new("Август", 31),
                new("Сентябрь", 30),
                new("Октябрь", 31),
                new("Ноябрь", 30),
                new("Декабрь", 31),
            },
            Seasons = new List<CalendarSeason>
            {
                new("Зима", new[] { 11, 0, 1 }),
                new("Весна", new[] { 2, 3, 4 }),
                new("Лето", new[] { 5, 6, 7 }),
                new("Осень", new[] { 8, 9, 10 }),
            },
            WeekDays = new List<string>
            {
                "Понедельник", "Вторник", "Среда", "Четверг",
                "Пятница", "Суббота", "Воскресенье",
            },
            StartMonth = startMonth,
            StartDay = startDay,
            StartHour = startHour,
            StartMinute = startMinute,
            StartWeekDayIndex = 0,
        };
    }

    /// <summary>
    /// Default fantasy calendar: 12 months × 30 days = 360 days/year,
    /// year 600, era "от Основания". Used by the default world and as a
    /// fallback for any custom world that doesn't install its own calendar.
    /// </summary>
    public static CalendarSpec DefaultFantasyCalendar { get; } = new CalendarSpec
    {
        YearStart = 600,
        Era = "от Основания",
        Months = new List<CalendarMonth>
        {
            new("Месяц Снегов", 30),
            new("Месяц Таяния", 30),
            new("Месяц Бурь", 30),
            new("Месяц Цветения", 30),
            new("Месяц Посева", 30),
            new("Месяц Солнцестояния", 30),
            new("Месяц Урожая", 30),
            new("Месяц Сбора", 30),
            new("Месяц Туманов", 30),
            new("Месяц Листопада", 30),
            new("Месяц Морозов", 30),
            new("Месяц Тьмы", 30),
        },
        Seasons = new List<CalendarSeason>
        {
            new("Зима", new[] { 11, 0, 1 }),
            new("Весна", new[] { 2, 3, 4 }),
            new("Лето", new[] { 5, 6, 7 }),
            new("Осень", new[] { 8, 9, 10 }),
        },
        WeekDays = new List<string>
        {
            "День Солнца", "День Луны", "День Войны", "День Воды",
            "День Древа", "День Золота", "День Покоя",
        },
        StartMonth = 0,
        StartDay = 1,
        StartHour = 8,
        StartMinute = 0,
        StartWeekDayIndex = 0,
    };
}

/// <summary>One month of a calendar: a display name + how many days it has.</summary>
public sealed record CalendarMonth(string Name, int Days);

/// <summary>A season groups months (by 0-based index) under a display name.</summary>
public sealed record CalendarSeason(string Name, IReadOnlyList<int> Months);

/// <summary>
/// Full calendar definition. Lives on the World, persisted in state.json,
/// installed (or overwritten) by the world-builder via <c>set_calendar</c>.
/// </summary>
public sealed record CalendarSpec
{
    /// <summary>Year number at game start (e.g. 2014 for Stalker, 600 for fantasy).</summary>
    public int YearStart { get; init; }

    /// <summary>Era label, e.g. "от Катастрофы", "Третья эпоха". Optional flavor.</summary>
    public string? Era { get; init; }

    /// <summary>Months in calendar order, starting from the first month of yearStart.</summary>
    public required IReadOnlyList<CalendarMonth> Months { get; init; }

    /// <summary>Seasons (optional; if absent, no season display).</summary>
    public IReadOnlyList<CalendarSeason>? Seasons { get; init; }

    /// <summary>Names of weekdays (optional). If absent, weekday isn't shown.</summary>
    public IReadOnlyList<string>? WeekDays { get; init; }

    /// <summary>Start month (0-based).</summary>
    public int StartMonth { get; init; }

    /// <summary>Start day-of-month (1-based).</summary>
    public int StartDay { get; init; } = 1;

    /// <summary>Start hour (0-23).</summary>
    public int StartHour { get; init; } = 8;

    /// <summary>Start minute (0-59).</summary>
    public int StartMinute { get; init; }

    /// <summary>
    /// Anchor index (0-based into WeekDays) for the weekday of
    /// (yearStart, startMonth, startDay). Lets the builder pin "day 1 was a
    /// Monday". Only meaningful when WeekDays is set. Defaults to 0.
    /// </summary>
    public int? StartWeekDayIndex { get; init; }
}

/// <summary>Rich, calendar-aware breakdown of an in-world instant.</summary>
public sealed record ClockBreakdown(
    int Year,
    int MonthIndex,
    string MonthName,
    int Day,
    int Hour,
    int Minute,
    string? SeasonName,
    string? WeekDayName,
    string? Era,
    long TotalDays);

/// <summary>
/// SIMPLIFIED in-game clock — the desktop engine's primary time surface.
///
/// Immutable: <see cref="Advance(int)"/> returns a new <see cref="GameTime"/>.
/// The world holds one of these and replaces it on time advances (so the
/// clock is purely functional from the consumer's POV).
///
/// Display format: <c>"День N, HH:MM"</c> (e.g. <c>"День 3, 14:30"</c>).
/// </summary>
public readonly record struct GameTime(int Day, int Hour, int Minute)
{
    /// <summary>Minutes per day — used by <see cref="Advance"/>.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>The game's canonical start: day 1, 08:00.</summary>
    public static readonly GameTime Start = new(1, 8, 0);

    /// <summary>
    /// Return a new <see cref="GameTime"/> advanced by <paramref name="minutes"/>.
    /// Negative or zero deltas are clamped to no-op (the clock never moves
    /// backward; the GM only ever advances time forward).
    /// </summary>
    public GameTime Advance(int minutes)
    {
        if (minutes <= 0) return this;
        // Day is 1-based; convert to 0-based internally for arithmetic.
        long total = ((long)(Day - 1)) * MinutesPerDay + Hour * 60 + Minute + minutes;
        long day = total / MinutesPerDay;
        long remainder = total % MinutesPerDay;
        return new GameTime((int)day + 1, (int)(remainder / 60), (int)(remainder % 60));
    }

    /// <summary>Localized display: <c>"День N, HH:MM"</c>.</summary>
    public override string ToString() => $"День {Day}, {Hour:D2}:{Minute:D2}";
}
