using System.Diagnostics;
using DarkHaven.Launcher.Accounts;
using DarkHaven.Launcher.Api;
using DarkHaven.Launcher.Content;
using DarkHaven.Launcher.Models;
using Serilog;

namespace DarkHaven.Launcher.Update;

/// <summary>The four checklist rows shown while connecting (mirrors the mockup).</summary>
public enum LaunchStep { Engine, Content, Verify, Start }

public enum StepState { Pending, Active, Done, Failed }

public sealed record LaunchProgress(
    LaunchStep Step,
    StepState State,
    string Detail,
    double? Fraction = null,
    double? BytesPerSecond = null,
    TimeSpan? Eta = null);

/// <summary>
/// End-to-end connect: resolve <c>/info</c> → engine → content → account → launch the client.
/// The one place the CLI and the GUI share for "connect to a server".
/// </summary>
public sealed class LaunchCoordinator(
    ServerInfoApi serverInfo,
    ContentUpdater content,
    AccountManager accounts,
    Engine.EngineManager engines,
    GameLauncher game)
{
    public async Task<Process> ConnectAsync(
        string address,
        bool allowGuest,
        bool compatMode,
        IProgress<LaunchProgress>? progress = null,
        Action<ResolvedServerInfo>? onResolved = null,
        CancellationToken cancel = default)
    {
        void Step(LaunchStep s, StepState st, string d, double? f = null, double? bps = null, TimeSpan? eta = null)
            => progress?.Report(new LaunchProgress(s, st, d, f, bps, eta));

        var resolved = await serverInfo.GetAsync(address, cancel);
        onResolved?.Invoke(resolved);
        var build = resolved.Info.Build
                    ?? throw new InvalidOperationException("Сервер не сообщил информацию о сборке");

        // 1 — engine
        Step(LaunchStep.Engine, StepState.Active, "проверка…");
        var beforeInstalled = engines.IsEngineInstalled(build.EngineVersion);
        var engineVersion = await engines.EnsureEngineAsync(
            build.EngineVersion,
            (done, total, _) => Step(LaunchStep.Engine, StepState.Active, "загрузка движка…",
                total > 0 ? (double)done / total : null),
            cancel);
        Step(LaunchStep.Engine, StepState.Done, $"Robust {engineVersion} · {(beforeInstalled ? "из кэша" : "загружен")}");

        // 2 — content
        Step(LaunchStep.Content, StepState.Active, "получение манифеста…");
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastTick = TimeSpan.Zero;
        double bps = 0;

        void OnDownload(long done, long total, string unit)
        {
            if (unit == "bytes")
            {
                var now = sw.Elapsed;
                var dt = (now - lastTick).TotalSeconds;
                if (dt >= 0.4)
                {
                    bps = (done - lastBytes) / dt;
                    lastBytes = done;
                    lastTick = now;
                }
                return;
            }

            // "files"
            if (total <= 0) return;
            var frac = (double)done / total;
            var remaining = bps > 1 && done > 0
                ? TimeSpan.FromSeconds((total - done) * ((sw.Elapsed.TotalSeconds) / done))
                : (TimeSpan?)null;
            Step(LaunchStep.Content, StepState.Active, $"{done:N0} / {total:N0} файлов", frac, bps > 1 ? bps : null, remaining);
        }

        var launch = await content.UpdateAsync(build, OnDownload, cancel);
        Step(LaunchStep.Content, StepState.Done, $"сборка {Short(build.Version)}");

        // 3 — verify (content update already verified per-blob + the manifest hash; this is a beat)
        Step(LaunchStep.Verify, StepState.Active, "");
        Step(LaunchStep.Verify, StepState.Done, "целостность подтверждена");

        // account
        GameAccount? account = null;
        var activeAcc = accounts.Active ?? accounts.Accounts.FirstOrDefault();
        if (activeAcc is not null)
            account = await accounts.ToGameAccountAsync(activeAcc, cancel);

        if (account is null && resolved.Info.Auth.Mode == AuthMode.Required)
            throw new InvalidOperationException("Сервер требует аккаунт Space Station 14. Сначала войдите.");
        if (account is null && !allowGuest && resolved.Info.Auth.Mode != AuthMode.Disabled)
            Log.Warning("Подключение к {Server} гостем", address);

        // 4 — start
        Step(LaunchStep.Start, StepState.Active, "");
        var proc = game.Start(resolved, launch, account, compatMode);
        Step(LaunchStep.Start, StepState.Done, "клиент запущен");

        return proc;
    }

    private static string Short(string? s) => s is { Length: > 8 } ? s[..8] : s ?? "";
}
