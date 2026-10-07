namespace Packwright.Core.Builders;

/// <summary>Inner-image compression choice for a package build.</summary>
public enum Ps5InnerCompression
{
    /// <summary>Every inner block stored uncompressed (byte-identical to the historical default).</summary>
    Stored,

    /// <summary>Force Kraken compression on every block.</summary>
    Kraken,

    /// <summary>Kraken where it helps, stored otherwise (engine default).</summary>
    Auto
}
