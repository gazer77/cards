namespace Cards.Engine.Shared;

/// <summary>
/// The rules as a client at a shared table sees them: every question answered from the
/// server's latest <see cref="TableView"/>, and every move sent to the server rather than
/// applied. The table view model drives this exactly as it drives the real rules, so a
/// shared game is played through the same taps, buttons and double taps as a local one.
///
/// Apply never changes the state here. The server applies the move and sends a new view,
/// which is the only way the table ever changes.
/// </summary>
public sealed class RemoteGameLogic(TableView view, Func<GameAction, Task> send) : IGameLogic
{
    public TableView View { get; } = view;

    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules) { }

    public IReadOnlyList<GameAction> GetValidActions(GameState state) => View.Actions;

    public void Apply(GameState state, GameAction action) => _ = send(action);

    public bool IsGameOver(GameState state) => View.IsGameOver;

    public string GetStatusText(GameState state) => View.Status;

    public string? GetStatusSubject(GameState state, string? viewerId) => View.StatusSubject;

    /// <summary>The server plays the automatic turns; a client never waits to play one.</summary>
    public TimeSpan? GetAutoAdvanceDelay(GameState state) => null;

    public IReadOnlyList<string> GetSelectableCardIds(GameState state) => View.SelectableCardIds;

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
        => View.DropZones.TryGetValue(cardId, out var zones) ? zones : [];

    public GameAction? GetDefaultCardAction(GameState state, string cardId, int? uid)
        => View.DefaultCardActions.GetValueOrDefault(cardId);
}
