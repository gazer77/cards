namespace Cards.Engine;

/// <summary>
/// Arithmetic a game definition can write where a number is expected: <c>"players + 1"</c>.
///
/// Exists because the alternative is tiers. Hand and Foot's deck is "one pack per player,
/// plus one", which took five <c>max_players</c> entries for copies and five more for
/// jokers — ten lines to say something a person says in six words, and ten places for the
/// two to disagree.
///
/// Deliberately tiny: integers, the named values a game knows about, the four operators,
/// parentheses, and min/max. No variables, no calls, no state. Every client must compute
/// the same deck from the same definition, so this evaluates the same way everywhere or
/// it is not fit for the job.
/// </summary>
public static class RuleExpression
{
    /// <summary>
    /// Evaluates <paramref name="text"/> against a set of named values.
    /// </summary>
    /// <exception cref="FormatException">
    /// The text is malformed or names a value the caller did not supply. Loud on purpose:
    /// a mistyped rule must fail rather than silently evaluate to something plausible.
    /// </exception>
    public static int Evaluate(string text, IReadOnlyDictionary<string, int> values)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Empty expression.");

        var parser = new Parser(text, values);
        int result = parser.ParseExpression();
        parser.ExpectEnd();
        return result;
    }

    /// <summary>
    /// Whether the text parses and uses only <paramref name="knownNames"/>. Used to check
    /// definitions at load, so a broken expression stops a game appearing rather than
    /// throwing mid-deal.
    /// </summary>
    public static bool IsValid(string text, IEnumerable<string> knownNames, out string error)
    {
        // Every name gets a value so parsing is exercised without needing a real game.
        var probe = knownNames.ToDictionary(n => n, _ => 1);

        try
        {
            Evaluate(text, probe);
            error = "";
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, int> values)
    {
        private int _pos;

        // expression := term (('+' | '-') term)*
        public int ParseExpression()
        {
            int value = ParseTerm();

            while (true)
            {
                SkipSpace();
                if (Peek() == '+')      { _pos++; value += ParseTerm(); }
                else if (Peek() == '-') { _pos++; value -= ParseTerm(); }
                else return value;
            }
        }

        // term := factor (('*' | '/') factor)*
        private int ParseTerm()
        {
            int value = ParseFactor();

            while (true)
            {
                SkipSpace();
                if (Peek() == '*') { _pos++; value *= ParseFactor(); }
                else if (Peek() == '/')
                {
                    _pos++;
                    int divisor = ParseFactor();
                    if (divisor == 0) throw new FormatException($"Division by zero in '{text}'.");
                    value /= divisor;
                }
                else return value;
            }
        }

        // factor := number | name | 'min'/'max' '(' expr ',' expr ')' | '(' expr ')' | '-' factor
        private int ParseFactor()
        {
            SkipSpace();

            if (Peek() == '-') { _pos++; return -ParseFactor(); }

            if (Peek() == '(')
            {
                _pos++;
                int inner = ParseExpression();
                Expect(')');
                return inner;
            }

            if (char.IsAsciiDigit(Peek()))
            {
                int start = _pos;
                while (char.IsAsciiDigit(Peek())) _pos++;
                return int.Parse(text.AsSpan(start, _pos - start));
            }

            if (char.IsAsciiLetter(Peek()) || Peek() == '_')
            {
                int start = _pos;
                while (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_') _pos++;
                string name = text[start.._pos];

                SkipSpace();
                if (Peek() == '(') return ParseCall(name);

                if (!values.TryGetValue(name, out int value))
                    throw new FormatException(
                        $"'{name}' is not something this rule can refer to. Known: " +
                        string.Join(", ", values.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");

                return value;
            }

            throw new FormatException(
                _pos >= text.Length
                    ? $"Expression '{text}' ends unexpectedly."
                    : $"Unexpected '{Peek()}' at position {_pos} in '{text}'.");
        }

        private int ParseCall(string name)
        {
            Expect('(');
            int first = ParseExpression();
            Expect(',');
            int second = ParseExpression();
            Expect(')');

            return name switch
            {
                "min" => Math.Min(first, second),
                "max" => Math.Max(first, second),
                _     => throw new FormatException($"'{name}' is not a function this rule can call."),
            };
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_pos < text.Length)
                throw new FormatException($"Unexpected '{text[_pos]}' after the end of '{text}'.");
        }

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (Peek() != c) throw new FormatException($"Expected '{c}' in '{text}'.");
            _pos++;
        }
    }
}
