using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;

namespace DarkHaven.App.ViewModels;

/// <summary>One day on the period calendar.</summary>
public sealed partial class CalendarDayViewModel(DateTime date, bool inMonth, bool isToday, bool isEnabled) : ObservableObject
{
    public DateTime Date { get; } = date;
    public string Text { get; } = date.Day.ToString(CultureInfo.InvariantCulture);
    public bool InMonth { get; } = inMonth;
    public bool IsToday { get; } = isToday;
    /// <summary>Days still to come can't be picked.</summary>
    public bool IsEnabled { get; } = isEnabled;

    /// <summary>The period's first or last day.</summary>
    [ObservableProperty] private bool _isEdge;
    /// <summary>A day between them.</summary>
    [ObservableProperty] private bool _isInRange;
}

/// <summary>
/// A period of days on a month calendar, Monday first: a click picks a day, a second click makes it
/// the other end of the period (in either order); presets cover the usual spans. It reads and writes
/// its owner's from/to, so the owner stays the one place the filter lives.
/// </summary>
public sealed partial class PeriodPickerViewModel : ObservableObject
{
    private static readonly string[] Months =
        ["ЯНВАРЬ", "ФЕВРАЛЬ", "МАРТ", "АПРЕЛЬ", "МАЙ", "ИЮНЬ", "ИЮЛЬ", "АВГУСТ", "СЕНТЯБРЬ", "ОКТЯБРЬ", "НОЯБРЬ", "ДЕКАБРЬ"];

    private readonly Func<(DateTime? From, DateTime? To)> _get;
    private readonly Action<DateTime?, DateTime?> _set;
    // After a first click the period is that one day, and the next click sets its other end.
    private bool _ending;

    /// <summary>The first day of the month on show.</summary>
    [ObservableProperty] private DateTime _month;

    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];

    public PeriodPickerViewModel(Func<(DateTime? From, DateTime? To)> get, Action<DateTime?, DateTime?> set)
    {
        _get = get;
        _set = set;
        _month = FirstOf(Today);
        BuildDays();
    }

    private static DateTime Today => DateTime.Today;
    private static DateTime FirstOf(DateTime d) => new(d.Year, d.Month, 1);

    public string MonthTitle => $"{Months[Month.Month - 1]} {Month.Year}";
    public bool CanGoNext => Month < FirstOf(Today);

    public bool IsSet => _get() is not (null, null);

    /// <summary>"всё время", "12 сен — 27 сен", "с 12 сен", "по 27 сен"; the year only when it isn't this one.</summary>
    public string Summary => _get() switch
    {
        (null, null) => "всё время",
        ({ } f, null) => $"с {Day(f)}",
        (null, { } t) => $"по {Day(t)}",
        ({ } f, { } t) when f.Date == t.Date => Day(f),
        ({ } f, { } t) => $"{Day(f)} — {Day(t)}",
    };

    /// <summary>The field's text: a calendar glyph (Nerd Font, DhMono) and <see cref="Summary"/>.</summary>
    public string FieldText => $"\uEAB0  {Summary}";

    private static string Day(DateTime d) => d.Year == Today.Year ? RuText.DayMonth(d) : RuText.ShortDate(d);

    /// <summary>"7", "30", "365" or "all" when the period is exactly one of the presets.</summary>
    public string? ActivePreset => _get() switch
    {
        (null, null) => "all",
        ({ } f, { } t) when t.Date == Today => (Today - f.Date).Days switch { 6 => "7", 29 => "30", 364 => "365", _ => null },
        _ => null,
    };

    partial void OnMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(CanGoNext));
        BuildDays();
    }

    [RelayCommand] private void PrevMonth() => Month = Month.AddMonths(-1);

    [RelayCommand]
    private void NextMonth()
    {
        if (CanGoNext)
            Month = Month.AddMonths(1);
    }

    [RelayCommand]
    private void Pick(CalendarDayViewModel? day)
    {
        if (day is not { IsEnabled: true })
            return;

        var (from, _) = _get();
        if (_ending && from is { } start)
        {
            _ending = false;
            Set(day.Date < start ? day.Date : start, day.Date < start ? start : day.Date);
        }
        else
        {
            _ending = true;
            Set(day.Date, day.Date);
        }
    }

    /// <summary>"7", "30", "365" — that many days up to today; "all" — no period.</summary>
    [RelayCommand]
    private void Preset(string span)
    {
        _ending = false;
        if (int.TryParse(span, out var days))
        {
            Set(Today.AddDays(1 - days), Today);
            Month = FirstOf(Today);
        }
        else
        {
            Set(null, null);
        }
    }

    /// <summary>The owner changed the period itself (filters reset): show it.</summary>
    public void Refresh()
    {
        _ending = false;
        Mark();
    }

    private void Set(DateTime? from, DateTime? to)
    {
        _set(from, to);
        Mark();
    }

    private void BuildDays()
    {
        Days.Clear();
        // Six weeks from the Monday on or before the 1st: the grid never changes height.
        var first = Month.AddDays(-(((int)Month.DayOfWeek + 6) % 7));
        for (var i = 0; i < 42; i++)
        {
            var d = first.AddDays(i);
            Days.Add(new CalendarDayViewModel(d, d.Month == Month.Month, d == Today, d <= Today));
        }
        Mark();
    }

    private void Mark()
    {
        var (from, to) = _get();
        foreach (var d in Days)
        {
            d.IsEdge = d.Date == from?.Date || d.Date == to?.Date;
            d.IsInRange = from is { } f && to is { } t && d.Date > f.Date && d.Date < t.Date;
        }
        OnPropertyChanged(nameof(IsSet));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FieldText));
        OnPropertyChanged(nameof(ActivePreset));
    }
}
