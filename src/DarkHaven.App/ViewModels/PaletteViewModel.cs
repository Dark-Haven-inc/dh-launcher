using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DarkHaven.App.ViewModels;

/// <summary>One hit in the quick switcher: the name split around the matched part, and what picking it does.</summary>
public sealed partial class PaletteItem : ObservableObject
{
    public required string Before { get; init; }
    public required string Match { get; init; }
    public required string After { get; init; }
    /// <summary>РЕГИОН / СЕРВЕР / РАЗДЕЛ.</summary>
    public required string KindLabel { get; init; }
    public string? Status { get; init; }
    public bool HasStatus => Status is not null;
    public required Action Invoke { get; init; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Ctrl+K: type a few letters, jump to a region, a server or a page. Enter picks the highlighted hit,
/// arrows move the highlight, Esc closes.
/// </summary>
public sealed partial class PaletteViewModel : ViewModelBase
{
    private const int MaxHits = 8;

    private static readonly (string Name, NavPage Page)[] Pages =
    [
        ("Главная", NavPage.Home), ("Регионы", NavPage.Regions), ("Серверы", NavPage.Servers),
        ("Мониторинг", NavPage.Monitoring), ("Новости", NavPage.News), ("Настройки", NavPage.Settings),
        ("Аккаунт", NavPage.Account),
    ];

    private readonly MainWindowViewModel _main;

    [ObservableProperty] private string _query = "";

    public ObservableCollection<PaletteItem> Hits { get; } = [];

    public PaletteViewModel(MainWindowViewModel main)
    {
        _main = main;
        // The server list loads when its page is first opened; the switcher needs it now.
        if (main.Servers.Servers.Count == 0)
            _ = main.Servers.RefreshAsync().ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(Search));
        Search();
    }

    partial void OnQueryChanged(string value) => Search();

    private void Search()
    {
        var q = Query.Trim();
        Hits.Clear();

        IEnumerable<(string Name, string Kind, string? Status, Action Go)> all =
            _main.Regions.Nodes.Select(n => (n.Name, "РЕГИОН", StatusOf(n.IsQuarantine, n.IsOffline, n.IsFull, n.IsOnline),
                (Action)(() => _main.ShowRegion(n))))
            .Concat(_main.Servers.Servers.Select(s => (s.Name, "СЕРВЕР", StatusOf(false, s.IsOffline, s.IsFull, s.IsOnline),
                (Action)(() => s.ConnectCommand.Execute(null)))))
            .Concat(Pages.Select(p => (p.Name, "РАЗДЕЛ", (string?)null, (Action)(() => _main.Page = p.Page))));

        if (q.Length == 0)
        {
            foreach (var (name, kind, status, go) in all.Where(a => a.Kind != "СЕРВЕР").Take(MaxHits))
                Hits.Add(Hit(name, 0, 0, kind, status, go));
        }
        else
        {
            var matches = all
                .Select(a => (a, At: a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase)))
                .Where(m => m.At >= 0)
                .OrderBy(m => m.At > 0) // prefix matches first
                .ThenBy(m => m.a.Name.Length)
                .Take(MaxHits);
            foreach (var (a, at) in matches)
                Hits.Add(Hit(a.Name, at, q.Length, a.Kind, a.Status, a.Go));
        }

        if (Hits.Count > 0)
            Hits[0].IsSelected = true;
    }

    private static string? StatusOf(bool quarantine, bool offline, bool full, bool online) =>
        quarantine ? "q" : offline ? "off" : full ? "full" : online ? "on" : "q";

    private static PaletteItem Hit(string name, int at, int length, string kind, string? status, Action go) => new()
    {
        Before = name[..at],
        Match = name.Substring(at, length),
        After = name[(at + length)..],
        KindLabel = kind,
        Status = status,
        Invoke = go,
    };

    public void Move(int delta)
    {
        if (Hits.Count == 0)
            return;
        var i = Hits.ToList().FindIndex(h => h.IsSelected);
        if (i >= 0)
            Hits[i].IsSelected = false;
        Hits[((i < 0 ? 0 : i + delta) % Hits.Count + Hits.Count) % Hits.Count].IsSelected = true;
    }

    public void Pick(PaletteItem? item = null)
    {
        item ??= Hits.FirstOrDefault(h => h.IsSelected);
        _main.ClosePalette();
        item?.Invoke();
    }

    public void Close() => _main.ClosePalette();
}
