namespace Cards.Engine.Multiplayer;

/// <summary>
/// Converts a host address (IP:port) to/from a human-readable room code.
/// Format: 4 groups of 3 uppercase letters, e.g. "CARD-GAME-HOST-7777".
/// The address is base-26 encoded; the port is appended verbatim after the last dash.
/// </summary>
public static class RoomCode
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Encode an "ip:port" address string into a room code like "XKCD-RFCN-ABCD-7777".
    /// </summary>
    public static string Encode(string address)
    {
        var parts = address.Split(':');
        if (parts.Length != 2)
            throw new ArgumentException("Address must be in 'host:port' format.", nameof(address));

        string host = parts[0];
        string port = parts[1];

        // Encode host bytes as base-26 letter groups
        byte[] bytes = System.Net.IPAddress.TryParse(host, out var ip)
            ? ip.GetAddressBytes()
            : System.Text.Encoding.UTF8.GetBytes(host);

        // Pad to 4 bytes (IPv4) or take first 4 bytes
        if (bytes.Length < 4)
            bytes = [.. bytes, .. new byte[4 - bytes.Length]];
        bytes = bytes[..4];

        // Each byte pair → 3-letter group (0-675 in base-26, byte pair 0-65535 → needs 4 letters; use 3 for brevity by modding)
        var groups = new string[2];
        for (int i = 0; i < 2; i++)
        {
            int val = bytes[i * 2] * 256 + bytes[i * 2 + 1];
            groups[i] = new string([
                Alphabet[val / (26 * 26)    % 26],
                Alphabet[val / 26           % 26],
                Alphabet[val                % 26],
            ]);
        }

        return $"{groups[0]}-{groups[1]}-{port}";
    }

    /// <summary>
    /// Decode a room code back to "ip:port".  Returns null if the code is invalid.
    /// </summary>
    public static string? Decode(string code)
    {
        try
        {
            var parts = code.ToUpperInvariant().Split('-');
            if (parts.Length != 3) return null;

            byte[] bytes = new byte[4];
            for (int i = 0; i < 2; i++)
            {
                string g = parts[i];
                if (g.Length != 3) return null;
                int val = 0;
                foreach (char c in g)
                {
                    int idx = Alphabet.IndexOf(c);
                    if (idx < 0) return null;
                    val = val * 26 + idx;
                }
                bytes[i * 2]     = (byte)(val >> 8);
                bytes[i * 2 + 1] = (byte)(val & 0xFF);
            }

            string host = new System.Net.IPAddress(bytes).ToString();
            string port = parts[2];
            return $"{host}:{port}";
        }
        catch
        {
            return null;
        }
    }
}
