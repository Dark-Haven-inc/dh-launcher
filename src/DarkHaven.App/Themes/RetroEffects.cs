using DarkHaven.Launcher.Data;

namespace DarkHaven.App.Themes;

/// <summary>
/// Which of the retro theme's tube effects are on (НАСТРОЙКИ → Вид), saved as config <c>Retro.Scanlines</c>,
/// <c>.Glow</c>, <c>.Noise</c>, <c>.Band</c>, <c>.Flicker</c>. Flicker is off unless asked for: some people
/// can't look at a flickering screen.
/// </summary>
public static class RetroEffects
{
    public static bool Scanlines { get; private set; } = true;
    public static bool Glow { get; private set; } = true;
    public static bool Noise { get; private set; } = true;
    public static bool Band { get; private set; } = true;
    public static bool Flicker { get; private set; }

    /// <summary>Raised on the UI thread when one is switched.</summary>
    public static event Action? Changed;

    public static void Load(SettingsDatabase settings)
    {
        Scanlines = settings.GetConfig("Retro.Scanlines") != "false";
        Glow = settings.GetConfig("Retro.Glow") != "false";
        Noise = settings.GetConfig("Retro.Noise") != "false";
        Band = settings.GetConfig("Retro.Band") != "false";
        Flicker = settings.GetConfig("Retro.Flicker") == "true";
    }

    public static void Set(SettingsDatabase settings, bool scanlines, bool glow, bool noise, bool band, bool flicker)
    {
        (Scanlines, Glow, Noise, Band, Flicker) = (scanlines, glow, noise, band, flicker);
        settings.SetConfig("Retro.Scanlines", scanlines ? "true" : "false");
        settings.SetConfig("Retro.Glow", glow ? "true" : "false");
        settings.SetConfig("Retro.Noise", noise ? "true" : "false");
        settings.SetConfig("Retro.Band", band ? "true" : "false");
        settings.SetConfig("Retro.Flicker", flicker ? "true" : "false");
        Changed?.Invoke();
    }
}
