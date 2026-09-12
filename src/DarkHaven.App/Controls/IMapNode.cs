namespace DarkHaven.App.Controls;

/// <summary>What <see cref="SectorMap"/> needs from each region to draw it.</summary>
public interface IMapNode
{
    string Name { get; }
    string Blurb { get; }
    /// <summary>Position on the map, 0..1 in both axes.</summary>
    double X { get; }
    double Y { get; }
    bool IsCentral { get; }
    bool IsOnline { get; }
    bool IsQuarantine { get; }
    bool IsOffline { get; }
    /// <summary>The player is connected to this region right now.</summary>
    bool IsCurrent { get; }
    string Population { get; }
    IReadOnlyList<string> Neighbours { get; }

    /// <summary>Which named cluster this node belongs to (e.g. a network like "Corvax") — nodes
    /// sharing a label get one big floating title + a soft coloured boundary drawn behind them,
    /// like the region names on a galaxy map. Null/empty means "not part of any named cluster",
    /// e.g. СЕКТОР FRONTIER 15's regions, which are a single cluster already named by the map itself.</summary>
    string? RegionLabel { get; }
}
