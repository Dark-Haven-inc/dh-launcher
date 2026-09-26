using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DarkHaven.App.ViewModels;

/// <summary>One color in the theme's color editor: hue 0–360, saturation and value 0–1, each on a
/// slider whose track shows where it leads. <paramref name="shown"/> turns the pick into the color
/// the theme really uses, for the swatch (the accent moves off the background).</summary>
public sealed partial class HsvSetting(string title, Func<HsvColor, Color>? shown = null) : ObservableObject
{
    [ObservableProperty] private double _hue;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _value;

    private bool _quiet;

    public string Title { get; } = title;

    /// <summary>The player moved a slider (not raised by <see cref="Set"/>).</summary>
    public event Action? Changed;

    public HsvColor Hsv => new(1, Hue, Saturation, Value);
    /// <summary>What the theme makes of the pick — the swatch and the hex show this.</summary>
    public Color Color => shown?.Invoke(Hsv) ?? Hsv.ToRgb();
    public IBrush Swatch => new SolidColorBrush(Color);
    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    public string HueText => $"{Hue:0}";
    public string SaturationText => $"{Saturation * 100:0}";
    public string ValueText => $"{Value * 100:0}";

    private static readonly IBrush Rainbow = Gradient(
        Enumerable.Range(0, 7).Select(i => HsvColor.ToRgb(i * 60, 1, 1)).ToArray());

    public IBrush HueTrack => Rainbow;
    public IBrush SaturationTrack => Gradient(HsvColor.ToRgb(Hue, 0, 1), HsvColor.ToRgb(Hue, 1, 1));
    public IBrush ValueTrack => Gradient(Colors.Black, HsvColor.ToRgb(Hue, Saturation, 1));

    /// <summary>Shows a color without it counting as the player's edit.</summary>
    public void Set(HsvColor c)
    {
        _quiet = true;
        Hue = c.H;
        Saturation = c.S;
        Value = c.V;
        _quiet = false;
    }

    /// <summary>The swatch again, when what it depends on outside the pick has changed (the background).</summary>
    public void RefreshShown()
    {
        OnPropertyChanged(nameof(Swatch));
        OnPropertyChanged(nameof(Hex));
    }

    partial void OnHueChanged(double value) => Update();
    partial void OnSaturationChanged(double value) => Update();
    partial void OnValueChanged(double value) => Update();

    private void Update()
    {
        RefreshShown();
        OnPropertyChanged(nameof(HueText));
        OnPropertyChanged(nameof(SaturationText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(SaturationTrack));
        OnPropertyChanged(nameof(ValueTrack));
        if (!_quiet)
            Changed?.Invoke();
    }

    private static IBrush Gradient(params Color[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        for (var i = 0; i < stops.Length; i++)
            brush.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
        return brush;
    }
}
