using System.ComponentModel;
using DarkHaven.Launcher.Models;
using DarkHaven.Launcher.Servers;

namespace DarkHaven.App.ViewModels;

// The wording the legacy views (Views/Legacy) show. The monochrome redesign rewrote these texts
// shorter, and the old views read wrong with them; the legacy design keeps its own here. Each
// Legacy… property follows the one it replaces: raised whenever that one is.

public partial class MainWindowViewModel
{
    public string LegacyVersionLine => $"ЛАУНЧЕР {LauncherVersion}   ·   ДВИЖОК Robust {EngineVersion}";

    public string LegacyRegionBannerText => _regionBannerIsSlot
        ? $"🟢  На {RegionOnlineName} освободилось место"
        : $"🟢  {RegionOnlineName} снова онлайн";

    public string LegacySlotWaitBannerText => $"⏳  Ждём место на {SlotWaitName} — подключим сами, как только освободится";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(RegionBannerText)) OnPropertyChanged(nameof(LegacyRegionBannerText));
        else if (e.PropertyName == nameof(SlotWaitBannerText)) OnPropertyChanged(nameof(LegacySlotWaitBannerText));
    }
}

public partial class HomeViewModel
{
    // ГЛАВНАЯ had a "continue" card with the last server, and the rest of the history under it.
    public string Greeting =>
        _services.Accounts.Active is { } a ? $"С возвращением, {a.Username}" : "Frontier 15";

    public HomeServerRow? ContinueServer => Recent.FirstOrDefault();
    public bool HasContinue => ContinueServer is not null;
    public IReadOnlyList<HomeServerRow> EarlierRecent => Recent.Skip(1).ToList();
    public bool HasEarlierRecent => Recent.Count > 1;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(HasRecent))
            return;
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(ContinueServer));
        OnPropertyChanged(nameof(HasContinue));
        OnPropertyChanged(nameof(EarlierRecent));
        OnPropertyChanged(nameof(HasEarlierRecent));
    }
}

public partial class RegionNodeViewModel
{
    public string LegacyStateText => IsQuarantine ? "НА КАРАНТИНЕ" : Entry.Reachability switch
    {
        ServerReachability.Online => "ОНЛАЙН",
        ServerReachability.Offline => "офлайн",
        _ => "…",
    };

    public string LegacyRoundText => Entry.RunLevel switch
    {
        RunLevel.InRound => "раунд идёт",
        RunLevel.PreRoundLobby => "лобби",
        RunLevel.PostRound => "конец раунда",
        _ => "",
    };

    public string LegacyWatchLabel => IsWatched ? "🔔 Уведомлю, когда поднимется" : "Уведомить, когда поднимется";
    public string LegacySlotLabel => IsWaitingForSlot ? "⏳ Ждём место — отменить" : "Ждать свободного места";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(StateText): OnPropertyChanged(nameof(LegacyStateText)); break;
            case nameof(RoundText): OnPropertyChanged(nameof(LegacyRoundText)); break;
            case nameof(WatchLabel): OnPropertyChanged(nameof(LegacyWatchLabel)); break;
            case nameof(SlotLabel): OnPropertyChanged(nameof(LegacySlotLabel)); break;
        }
    }
}

public partial class ServerRowViewModel
{
    public string LegacyStateText => IsOnline ? "ОНЛАЙН" : "офлайн";
    public string LegacySlotLabel => IsWaitingForSlot ? "⏳ Ждём место — отменить" : "Ждать свободного места";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(StateText)) OnPropertyChanged(nameof(LegacyStateText));
        else if (e.PropertyName == nameof(SlotLabel)) OnPropertyChanged(nameof(LegacySlotLabel));
    }
}

public sealed partial class ServerGroupViewModel
{
    public string LegacySummaryLine => TotalPlayers > 0 ? $"{Count} · {TotalPlayers} игроков" : $"{Count}";
}

public partial class MonitoringViewModel
{
    public string LegacyStateText => StateText == "…" ? "…"
        : !IsOnline ? "Сервер не отвечает"
        : LiveRunLevel switch
        {
            RunLevel.PreRoundLobby => "Лобби — сервер принимает игроков",
            RunLevel.InRound => "Раунд идёт — можно заходить",
            RunLevel.PostRound => "Конец раунда",
            _ => "Сервер онлайн",
        };

    public string LegacyPlayersText => PlayersText.Replace("/", " / ");
    public string LegacyCopyLabel => CopyLabel == "" ? "Скопировано" : "Скопировать адрес";
    public string LegacyTableLabel => ShowTable ? "Скрыть таблицу" : "Показать таблицей";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(StateText) or nameof(IsOnline) or nameof(LiveRunLevel): OnPropertyChanged(nameof(LegacyStateText)); break;
            case nameof(PlayersText): OnPropertyChanged(nameof(LegacyPlayersText)); break;
            case nameof(CopyLabel): OnPropertyChanged(nameof(LegacyCopyLabel)); break;
            case nameof(TableLabel): OnPropertyChanged(nameof(LegacyTableLabel)); break;
        }
    }
}

public sealed partial record LauncherRegionRow
{
    public string LegacyText => $"{Name} — {Players}";
}

public partial class ConnectingViewModel
{
    public string LegacySubtitle => _server.IsDarkHavenRegion ? "Переход в регион" : "Подключение к серверу";
}

public partial class FriendsViewModel
{
    public string LegacyOnlineSummary => Friends.Count(f => f.Online) is var n and > 0 ? $"{n} в сети" : "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(OnlineSummary)) OnPropertyChanged(nameof(LegacyOnlineSummary));
    }
}

public partial class FriendItemViewModel
{
    public string LegacyRemoveLabel => ConfirmingRemove ? "Удалить?" : "✕";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(RemoveLabel)) OnPropertyChanged(nameof(LegacyRemoveLabel));
    }
}

public partial class BanListViewModel
{
    public string LegacyTotalText => Total == 0 ? "" : $"Найдено: {Total}";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(TotalText)) OnPropertyChanged(nameof(LegacyTotalText));
    }
}

public sealed partial class AdminServerRowViewModel
{
    public string LegacyProbeText => (Server.ProbedName is null ? "✖ " : "✔ ") + ProbeText;
}
