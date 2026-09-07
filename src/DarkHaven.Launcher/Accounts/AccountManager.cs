using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Data;
using DarkHaven.Launcher.Update;
using Serilog;

namespace DarkHaven.Launcher.Accounts;

public enum AccountStatus { Unknown, Available, Expired }

public sealed class Account(StoredLogin login, AccountStatus status)
{
    public Guid UserId => Stored.UserId;
    public string Username => Stored.UserName;
    public StoredLogin Stored { get; internal set; } = login;
    public AccountStatus Status { get; internal set; } = status;
}

/// <summary>
/// Owns the set of logged-in accounts: persistence, the active selection, token refresh/validation,
/// and turning an account into launch-ready <see cref="GameAccount"/> auth material.
/// </summary>
public sealed class AccountManager(SettingsDatabase settings, AuthApi auth)
{
    private const string ActiveKey = "SelectedLogin";
    private readonly Dictionary<Guid, Account> _accounts = new();

    public IReadOnlyCollection<Account> Accounts => _accounts.Values;

    public Account? Active
    {
        get
        {
            var id = settings.GetConfig(ActiveKey);
            return id is not null && Guid.TryParse(id, out var g) && _accounts.TryGetValue(g, out var a) ? a : null;
        }
        set => settings.SetConfig(ActiveKey, value?.UserId.ToString());
    }

    public void Load()
    {
        settings.Initialize();
        _accounts.Clear();
        foreach (var login in settings.GetLogins())
        {
            var status = login.AsToken().IsTimeExpired ? AccountStatus.Expired : AccountStatus.Unknown;
            _accounts[login.UserId] = new Account(login, status);
        }
        Log.Debug("Loaded {Count} account(s)", _accounts.Count);
    }

    public async Task<AuthResult> LoginAsync(string username, string password, string? tfaCode = null, CancellationToken cancel = default)
    {
        var result = await auth.AuthenticateAsync(username, null, password, tfaCode, cancel);
        if (!result.IsSuccess)
            return result;

        var login = result.Login!;
        var stored = new StoredLogin(login.UserId, login.Username, login.Token, login.ExpireTime);
        settings.UpsertLogin(stored);
        _accounts[login.UserId] = new Account(stored, AccountStatus.Available);
        Active ??= _accounts[login.UserId];
        Log.Information("Logged in as {User} ({Id})", login.Username, login.UserId);
        return result;
    }

    public async Task LogoutAsync(Account account, CancellationToken cancel = default)
    {
        await auth.LogoutAsync(account.Stored.Token, cancel);
        settings.DeleteLogin(account.UserId);
        _accounts.Remove(account.UserId);
        if (Active?.UserId == account.UserId)
            Active = _accounts.Values.FirstOrDefault();
    }

    /// <summary>Validates / refreshes every account's token, persisting new tokens.</summary>
    public async Task RefreshAllAsync(CancellationToken cancel = default)
    {
        foreach (var account in _accounts.Values.ToList())
            await RefreshAsync(account, cancel);
    }

    public async Task RefreshAsync(Account account, CancellationToken cancel = default)
    {
        var token = account.Stored.AsToken();

        if (token.IsTimeExpired)
        {
            account.Status = AccountStatus.Expired;
            return;
        }

        try
        {
            if (token.ShouldRefresh)
            {
                var fresh = await auth.RefreshTokenAsync(token.Token, cancel);
                if (fresh is null)
                {
                    account.Status = AccountStatus.Expired;
                    Log.Debug("Token for {User} expired on refresh", account.Username);
                    return;
                }
                Persist(account, fresh.Value);
                account.Status = AccountStatus.Available;
            }
            else if (account.Status == AccountStatus.Unknown)
            {
                var valid = await auth.PingAsync(token.Token, cancel);
                account.Status = valid ? AccountStatus.Available : AccountStatus.Expired;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not refresh token for {User}", account.Username);
        }
    }

    /// <summary>Auth material for a launch. Refreshes the token first if needed. Null if expired.</summary>
    public async Task<GameAccount?> ToGameAccountAsync(Account account, CancellationToken cancel = default)
    {
        await RefreshAsync(account, cancel);
        if (account.Status == AccountStatus.Expired)
            return null;
        return new GameAccount(account.Username, account.Stored.Token, account.UserId);
    }

    private void Persist(Account account, AuthToken token)
    {
        var stored = account.Stored with { Token = token.Token, Expires = token.ExpireTime };
        account.Stored = stored;
        settings.UpsertLogin(stored);
    }
}
