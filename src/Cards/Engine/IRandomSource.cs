namespace Cards.Engine;

/// <summary>
/// Source of randomness for everything the engine does that is not deterministic:
/// shuffling, initial dealer selection, and AI tie-breaking.
///
/// Exists so a game can be replayed exactly.  Reproducibility is what lets the
/// golden-master tests detect an accidental behaviour change, and it is what makes
/// a bug report reproducible from a seed alone.
///
/// Note that the networked game does NOT rely on determinism for correctness — the
/// host is authoritative and ships masked snapshots.  The seed is host-side state
/// and must never be sent to a client: it leaks every future shuffle.
/// </summary>
public interface IRandomSource
{
    /// <summary>Returns a non-negative value less than <paramref name="maxExclusive"/>.</summary>
    int Next(int maxExclusive);
}

/// <summary>
/// Non-deterministic default, backed by <see cref="Random.Shared"/>.
/// Used when nothing has asked for a specific seed.
/// </summary>
public sealed class SharedRandomSource : IRandomSource
{
    public static readonly SharedRandomSource Instance = new();

    private SharedRandomSource() { }

    public int Next(int maxExclusive) => Random.Shared.Next(maxExclusive);
}

/// <summary>
/// Deterministic source: the same seed always produces the same sequence.
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
    private readonly Random _rng;

    public ulong Seed { get; }

    public SeededRandomSource(ulong seed)
    {
        Seed = seed;
        // Fold the 64-bit seed into the 32 bits Random accepts.
        _rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
    }

    public int Next(int maxExclusive) => _rng.Next(maxExclusive);
}
