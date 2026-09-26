using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.ViewModels;

/// <summary>One ban, laid out for reading.</summary>
public sealed class BanRowViewModel(PlatformPublicBan b)
{
    public string Player => b.Player;
    public string Reason => b.Reason;
    public string WhenText => RuText.ShortDateTime(b.At.ToLocalTime());
    public string? AdminText => b.Admin is { } a ? $"выдал {a}" : null;

    public string KindText => b.IsRoleBan
        ? "бан на роли" + (b.Roles is { } r ? $": {r}" : "")
        : "бан на сервере";

    public string DurationText => b.ExpiresAt is not { } until
        ? "навсегда"
        : $"{RuText.Span(until - b.At)} · до {RuText.ShortDateTime(until.ToLocalTime())}";

    /// <summary>"active" | "expired" | "lifted" — the view colours the state by it.</summary>
    public string State => b.LiftedAt is not null ? "lifted"
        : b.ExpiresAt is { } until && until <= DateTimeOffset.UtcNow ? "expired"
        : "active";

    public string StateText => State switch
    {
        "lifted" => b.LiftedBy is { } by
            ? $"снят · {by}, {RuText.ShortDate(b.LiftedAt!.Value.ToLocalTime())}"
            : $"снят · {RuText.ShortDate(b.LiftedAt!.Value.ToLocalTime())}",
        "expired" => "истёк",
        _ => "действует",
    };

    public bool IsActive => State == "active";

    // --- as the monochrome list lays it out ---

    /// <summary>"СЕРВЕР" or "РОЛИ", a tag at the start of the bottom line.</summary>
    public string KindTag => b.IsRoleBan ? "РОЛИ" : "СЕРВЕР";

    /// <summary>"Капитан, ГСБ  ·  26 сен 21:44 → 4 окт 02:44  ·  7 дней", or "… → навсегда".</summary>
    public string MetaText
    {
        get
        {
            var period = b.ExpiresAt is { } until
                ? $"{Moment(b.At)} → {Moment(until)}  ·  {RuText.Span(until - b.At)}"
                : $"{Moment(b.At)} → навсегда";
            return b.IsRoleBan && b.Roles is { } roles ? $"{roles}  ·  {period}" : period;
        }
    }

    /// <summary>On the right: how long an active ban has left, or how it ended.</summary>
    public string StateLine => State switch
    {
        "lifted" => $"снят {Moment(b.LiftedAt!.Value)}",
        "expired" => "истёк",
        _ => b.ExpiresAt is { } until ? $"ещё {RuText.Span(until - DateTimeOffset.UtcNow)}" : "навсегда",
    };

    /// <summary>Under the state: who gave the ban, and who lifted it.</summary>
    public string? PeopleText => (b.Admin, b.LiftedBy) switch
    {
        ({ } a, { } l) => $"выдал {a} · снял {l}",
        ({ } a, null) => $"выдал {a}",
        (null, { } l) => $"снял {l}",
        _ => null,
    };

    /// <summary>"26 сен 21:44"; with the year when it isn't this one.</summary>
    private static string Moment(DateTimeOffset d)
    {
        var local = d.ToLocalTime();
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Year == DateTimeOffset.Now.Year ? $"{RuText.DayMonth(local)} {time}" : $"{RuText.ShortDate(local)} {time}";
    }
}

/// <summary>
/// "Баны" — the public ban list of the DH servers, opened from a region's card: search by nickname,
/// filters by state, type and dates, a page at a time. Read from the platform's /api/banlist.
/// </summary>
public partial class BanListViewModel(AppServices services, Action back) : ViewModelBase
{
    private const int PageSize = 30;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int _page;
    private int _generation;

    public ObservableCollection<BanRowViewModel> Items { get; } = [];

    [ObservableProperty] private string _title = "Баны";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "all";
    [ObservableProperty] private string _type = "all";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _total;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _canLoadMore;

    public string TotalText => Total == 0 ? "" : RuText.Plural((int)Math.Min(Total, int.MaxValue), "бан", "бана", "банов");
    partial void OnTotalChanged(long value) => OnPropertyChanged(nameof(TotalText));

    /// <summary>The date filter as a calendar (the monochrome view); it sets <see cref="From"/> and <see cref="To"/>.</summary>
    public PeriodPickerViewModel Period => _period ??= new PeriodPickerViewModel(() => (From, To), SetPeriod);
    private PeriodPickerViewModel? _period;
    private bool _settingPeriod;

    /// <summary>Anything narrowed down — "сбросить" shows only then.</summary>
    public bool HasFilters => Search.Length > 0 || Status != "all" || Type != "all" || From is not null || To is not null;

    /// <summary>Both ends at once, one reload.</summary>
    private void SetPeriod(DateTime? from, DateTime? to)
    {
        _settingPeriod = true;
        From = from;
        To = to;
        _settingPeriod = false;
        _ = ReloadAsync();
    }

    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(HasFilters));
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnDebounced;
        _searchDebounce.Tick += OnDebounced;
        _searchDebounce.Start();
    }

    private void OnDebounced(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _ = ReloadAsync();
    }

    partial void OnStatusChanged(string value) => FiltersChanged();
    partial void OnTypeChanged(string value) => FiltersChanged();
    partial void OnFromChanged(DateTime? value) => DatesChanged();
    partial void OnToChanged(DateTime? value) => DatesChanged();

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        _ = ReloadAsync();
    }

    private void DatesChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        if (_settingPeriod)
            return;
        _period?.Refresh();
        _ = ReloadAsync();
    }

    public void Open(string title)
    {
        Title = $"Баны · {title}";
        _ = ReloadAsync();
    }

    [RelayCommand] private void Back() => back();
    [RelayCommand] private void SetStatus(string status) => Status = status;
    [RelayCommand] private void SetType(string type) => Type = type;

    [RelayCommand]
    private void ResetFilters()
    {
        Search = "";
        Status = "all";
        Type = "all";
        From = null;
        To = null;
    }

    [RelayCommand]
    private Task LoadMore() => LoadAsync(_page + 1, append: true);

    public Task ReloadAsync() => LoadAsync(0, append: false);

    private async Task LoadAsync(int page, bool append)
    {
        var generation = ++_generation;
        IsLoading = true;
        try
        {
            // Dates are picked as local days; "to" includes the whole day it names.
            DateTimeOffset? from = From is { } f ? new DateTimeOffset(f.Date, TimeZoneInfo.Local.GetUtcOffset(f.Date)) : null;
            DateTimeOffset? to = To is { } t ? new DateTimeOffset(t.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(t.Date)) : null;

            var result = await services.Platform.GetBanListAsync(Search.Trim(), from, to, Status, Type, page, PageSize);
            if (generation != _generation)
                return; // a newer search or filter already replaced this one

            if (!append)
                Items.Clear();

            if (result is null)
            {
                Message = "Платформа сейчас недоступна — список банов не загрузился.";
                CanLoadMore = false;
                return;
            }
            if (!result.Available)
            {
                Message = "Список банов появится, когда платформа будет подключена к базе игрового сервера.";
                CanLoadMore = false;
                Total = 0;
                return;
            }

            foreach (var b in result.Items)
                Items.Add(new BanRowViewModel(b));
            _page = page;
            Total = result.Total;
            CanLoadMore = Items.Count < result.Total;
            Message = Items.Count == 0 ? "Ничего не найдено — попробуйте другой ник или снимите фильтры." : null;
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }
}
