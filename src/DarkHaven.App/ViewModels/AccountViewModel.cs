using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkHaven.Launcher.Accounts;
using DarkHaven.Launcher.Api;

namespace DarkHaven.App.ViewModels;

public partial class AccountRowViewModel(Account account, bool isActive) : ViewModelBase
{
    public Account Account { get; } = account;
    public string Username => Account.Username;
    public bool IsActive { get; } = isActive;
    public string StatusText => Account.Status.ToString();
    public string StatusHint => Account.Status switch
    {
        AccountStatus.Available => IsActive ? "вход выполнен" : "готов",
        AccountStatus.Expired => "нужен пароль",
        _ => "проверка…",
    };
}

public partial class AccountViewModel : ViewModelBase
{
    private readonly AppServices _services;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _tfaCode = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _needsTfa;

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    public string AuthServerLine => $"Сервер авторизации: {_services.Auth.BaseUrl}";

    public AccountViewModel(AppServices services)
    {
        _services = services;
        RefreshList();
        _ = RefreshTokensAsync();
    }

    [RelayCommand] private void OpenRegister() => OpenUrl("https://account.spacestation14.com/Identity/Account/Register");
    [RelayCommand] private void OpenForgot() => OpenUrl("https://account.spacestation14.com/Identity/Account/ForgotPassword");

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (Busy || Username.Length == 0 || Password.Length == 0)
            return;

        Busy = true;
        Error = null;
        try
        {
            var result = await _services.Accounts.LoginAsync(Username, Password, NeedsTfa ? TfaCode : null);
            if (result.IsSuccess)
            {
                Password = "";
                TfaCode = "";
                NeedsTfa = false;
                RefreshList();
            }
            else
            {
                NeedsTfa = result.DenyCode is AuthDenyCode.TfaRequired or AuthDenyCode.TfaInvalid;
                Error = string.Join(" ", result.Errors);
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void SetActive(AccountRowViewModel row)
    {
        _services.Accounts.Active = row.Account;
        RefreshList();
    }

    [RelayCommand]
    private async Task LogoutAsync(AccountRowViewModel row)
    {
        await _services.Accounts.LogoutAsync(row.Account);
        RefreshList();
    }

    private async Task RefreshTokensAsync()
    {
        await _services.Accounts.RefreshAllAsync();
        RefreshList();
    }

    private void RefreshList()
    {
        Accounts.Clear();
        var active = _services.Accounts.Active;
        foreach (var a in _services.Accounts.Accounts)
            Accounts.Add(new AccountRowViewModel(a, active?.UserId == a.UserId));
    }
}
