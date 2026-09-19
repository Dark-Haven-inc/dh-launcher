using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DarkHaven.App.ViewModels;

/// <summary>
/// What the Discord-style profile card shows. One card control renders the player's own profile
/// (with its editor as a live preview) and, later, anyone else's — both just implement this.
/// </summary>
public interface IProfileCard : INotifyPropertyChanged
{
    string CardName { get; }
    string CardSubtitle { get; }
    string CardMemberSince { get; }

    string? CardAbout { get; }
    bool CardHasAbout { get; }

    Bitmap? CardAvatar { get; }
    bool CardHasAvatar { get; }

    /// <summary>The banner picture; without one the banner is filled with <see cref="CardAccentBrush"/>.</summary>
    Bitmap? CardBanner { get; }
    bool CardHasBanner { get; }

    IBrush CardAccentBrush { get; }

    /// <summary>What fills the banner when there's no picture — the accent, fading darker.</summary>
    IBrush CardBannerBrush { get; }
    IBrush CardRingBrush { get; }
}
