namespace Cards.Tests;

/// <summary>
/// Serialises tests that touch <see cref="Cards.Rendering.CardRenderer"/>'s image cache.
///
/// The cache is static — one per process, shared by every renderer — which is right for
/// the app, where a card at a given size is the same pixels everywhere and caching it
/// once is the entire point. It does mean any test that measures or clears it is
/// sharing global state with every other test, and xunit runs classes in parallel by
/// default, so without this a cache-timing test can be reset mid-measurement by an
/// unrelated one. That showed up as failures only in the full suite, never in isolation.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CardCacheCollection
{
    public const string Name = "card-image-cache";
}
