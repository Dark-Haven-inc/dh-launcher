using DarkHaven.Launcher.Data;

namespace DarkHaven.App.Themes;

/// <summary>
/// The cyberpunk theme's effects (НАСТРОЙКИ → Вид), saved as config <c>Cyberpunk.Glitch</c> and
/// <c>Cyberpunk.Glow</c>: the title's ghosts jumping apart now and then, and the neon glow round the
/// main buttons. Both on unless switched off.
/// </summary>
public static class CyberEffects
{
    public static bool Glitch { get; private set; } = true;
    public static bool Glow { get; private set; } = true;

    /// <summary>Raised on the UI thread when one is switched.</summary>
    public static event Action? Changed;

    public static void Load(SettingsDatabase settings)
    {
        Glitch = settings.GetConfig("Cyberpunk.Glitch") != "false";
        Glow = settings.GetConfig("Cyberpunk.Glow") != "false";
    }

    public static void Set(SettingsDatabase settings, bool glitch, bool glow)
    {
        (Glitch, Glow) = (glitch, glow);
        settings.SetConfig("Cyberpunk.Glitch", glitch ? "true" : "false");
        settings.SetConfig("Cyberpunk.Glow", glow ? "true" : "false");
        Changed?.Invoke();
    }
}
