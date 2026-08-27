using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cards.Engine;

/// <summary>
/// Writes an unset <see cref="JsonElement"/> as null instead of throwing.
///
/// <see cref="Cards.Models.GameDefinition"/> holds several raw JsonElement properties
/// (deck, teams, phase.next, cards_per_player). A definition that omits one leaves it
/// at its default, whose ValueKind is <c>Undefined</c> — and the built-in writer throws
/// <see cref="InvalidOperationException"/> ("Operation is not valid due to the current
/// state of the object") when asked to serialize that.
///
/// Both places that deep-clone a definition via a JSON round-trip hit this:
/// GameLoader.MergeWithParent (used by every game with "extends") and
/// HouseRuleEngine.Apply (used whenever a house rule is switched on). The result was
/// silent: euchre-3p and poker-wilds simply never appeared, and enabling a house rule
/// could fail the same way.
///
/// Nothing in the engine distinguishes Undefined from Null — no code reads
/// JsonValueKind.Undefined — so mapping one to the other on write is safe.
/// </summary>
public sealed class SafeJsonElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonElement.ParseValue(ref reader);

    public override void Write(
        Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            writer.WriteNullValue();
        else
            value.WriteTo(writer);
    }
}
