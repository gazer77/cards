using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Every word the table says goes through the definition. A key it overrides is said
/// its way; a key it leaves alone keeps the default; a _you variant addresses the
/// person at this screen; a key nobody says fails the definition.
/// </summary>
public sealed class GameTextTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(2),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);
        return (state, logic);
    }

    [Fact]
    public void The_definition_names_its_own_buttons()
    {
        var (state, logic) = HandAndFoot();

        var draw = logic.GetValidActions(state).Single(a => a.Type == "draw_from_deck");
        Assert.Equal("Draw Two", draw.Label);
    }

    [Fact]
    public void Status_addresses_the_person_at_this_screen_and_names_everyone_else()
    {
        var (state, logic) = HandAndFoot();

        // Seat 0 is the person at this screen.
        state.CurrentPlayerIndex = 0;
        logic.GetValidActions(state);
        Assert.Equal("Your turn — draw two, or take the pile", state.Metadata["status"]);

        logic.Apply(state, new GameAction("draw_from_deck"));
        // A discard ends the turn; the status then names the next player.
        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        state.Metadata["selected_card"] = hand.Cards[0].Uid.ToString();
        logic.Apply(state, new GameAction("discard"));

        Assert.Equal($"{state.CurrentPlayer.Name} to draw", state.Metadata["status"]);
    }

    [Fact]
    public void A_key_the_definition_leaves_alone_keeps_its_default()
    {
        var (state, _) = HandAndFoot();
        // Hand and Foot does not override not_a_meld.
        Assert.Equal("That is not a meld — pick three or more of a rank.",
            GameText.Message(state, "not_a_meld", "That is not a meld — pick three or more of a rank."));
    }

    [Fact]
    public void Placeholders_are_filled()
    {
        var (state, _) = HandAndFoot();
        string text = GameText.Message(state, "opening_too_low", "ignored",
            state.Players[0].Id, ("required", 90), ("offered", 30));
        Assert.Equal("You need 90 to open this round; that is 30.", text);
    }

    [Fact]
    public void A_key_nobody_says_fails_the_definition()
    {
        var (state, _) = HandAndFoot();
        var definition = state.Definition;
        definition.Text!.Messages["turn_drawn"] = "typo";

        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("turn_drawn"));
        definition.Text.Messages.Remove("turn_drawn");
    }

    /// <summary>
    /// The key table is what the validator checks against and the schema documents; the
    /// call sites are what the table says. Read the engine's source and hold the two to
    /// each other: every key a handler says is in the table with the same default, and
    /// the table names nothing no handler says. A drift either way is a promise broken.
    /// </summary>
    [Fact]
    public void Every_key_the_engine_says_is_in_the_table_with_its_default()
    {
        string root = FileSystemGameAssetSource.FindRepoRoot();
        var files   = Directory.GetFiles(Path.Combine(root, "src", "Cards.Core", "Engine"), "*.cs");

        var said = new Dictionary<string, HashSet<string>>();
        // key, then the default string — which may hold \" escapes.
        var call = new System.Text.RegularExpressions.Regex(
            // Announce names the speaker before the key, so one argument may sit between.
            "GameText\\.(Message|TeamMessage|Action|Log|Announce|Between)\\(state,\\s*(?:[^\",]+,\\s*)?\"([a-z_{}]+)\",\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var file in files)
            foreach (System.Text.RegularExpressions.Match m in call.Matches(File.ReadAllText(file)))
            {
                string kind = m.Groups[1].Value == "Action" ? "action" : "message";
                string key  = m.Groups[2].Value;
                if (key.StartsWith("draw_from_")) continue;   // open-ended by design
                said.TryAdd(kind + ":" + key, []);
                said[kind + ":" + key].Add(m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\n", "\n"));
            }

        Assert.NotEmpty(said);

        foreach (var (kindKey, defaults) in said)
        {
            var (kind, key) = (kindKey.Split(':')[0], kindKey.Split(':')[1]);
            var table = kind == "action" ? GameText.ActionKeys : GameText.MessageKeys;

            Assert.True(table.ContainsKey(key), $"{kind} '{key}' is said by a handler but not in the table.");
            Assert.True(defaults.Count == 1, $"{kind} '{key}' has {defaults.Count} different defaults across call sites.");
            Assert.Equal(defaults.Single(), table[key]);
        }

        foreach (var key in GameText.MessageKeys.Keys)
            Assert.True(said.ContainsKey("message:" + key), $"message '{key}' is in the table but no handler says it.");
        foreach (var key in GameText.ActionKeys.Keys.Where(k => !k.Contains('{')))
            Assert.True(said.ContainsKey("action:" + key), $"action '{key}' is in the table but no handler says it.");
    }
}
