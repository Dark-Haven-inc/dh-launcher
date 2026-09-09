namespace DarkHaven.App.Controls;

/// <summary>What <see cref="SectorMap"/> needs from each region to draw it.</summary>
public interface IMapNode
{
    string Name { get; }
    /// <summary>Position on the map, 0..1 in both axes.</summary>
    double X { get; }
    double Y { get; }
    bool IsCentral { get; }
    bool IsOnline { get; }
    bool IsQuarantine { get; }
    bool IsOffline { get; }
    string Population { get; }
    IReadOnlyList<string> Neighbours { get; }
}
