using System.Text;
using System.Text.RegularExpressions;

// ── Configuration ─────────────────────────────────────────────────────────────

const string SourceUrl = "https://www.gutenberg.org/files/53881/53881-0.txt";

// Maps ALL-CAPS section headings found in the Gutenberg text to our help file names.
// Key   = substring to match in the heading (case-insensitive after normalizing)
// Value = output filename under games/help/
var sectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["HEARTS"]         = "hearts.md",
    ["EUCHRE"]         = "euchre.md",
    ["BLACK JACK"]     = "blackjack.md",
    ["BINOCLE"]        = "pinochle.md",
    ["PINOCHLE"]       = "pinochle.md",
    ["POKER"]          = "poker-stud.md",
    ["CRIBBAGE"]       = "cribbage.md",
    ["WHIST"]          = "whist.md",
};

// Resolve output path relative to this tool's location: ../../games/help/
string toolDir  = AppContext.BaseDirectory;
string repoRoot = Path.GetFullPath(Path.Combine(toolDir, "..", "..", "..", "..", ".."));
string helpDir  = Path.Combine(repoRoot, "games", "help");

// ── Fetch ─────────────────────────────────────────────────────────────────────

Console.WriteLine($"Fetching {SourceUrl} ...");
string rawText;
using (var http = new HttpClient())
{
    http.Timeout = TimeSpan.FromSeconds(120);
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; cards-hoyle-extractor/1.0)");
    rawText = await http.GetStringAsync(SourceUrl);
}
Console.WriteLine($"  Downloaded {rawText.Length / 1024} KB");

// ── Parse ─────────────────────────────────────────────────────────────────────

// A major section heading is a line that is entirely UPPERCASE letters, spaces,
// hyphens, and an optional trailing period — with nothing else on the line.
var headingRegex = new Regex(@"^[A-Z][A-Z\s\-,()]+\.?\s*$", RegexOptions.Compiled);

string[] lines = rawText.Split('\n');

// Find all headings with their line indices
var headings = new List<(int lineIndex, string heading)>();
for (int i = 0; i < lines.Length; i++)
{
    string trimmed = lines[i].Trim();
    if (trimmed.Length >= 3 && headingRegex.IsMatch(trimmed))
        headings.Add((i, trimmed.TrimEnd('.')));
}

Console.WriteLine($"  Found {headings.Count} candidate headings");

// ── Extract sections ──────────────────────────────────────────────────────────

// Track which output files we've already written so we don't overwrite a longer
// section with a shorter sub-section (e.g. "POKER" found before "POKER FAMILY").
var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

Directory.CreateDirectory(helpDir);

int extracted = 0;

for (int h = 0; h < headings.Count; h++)
{
    var (startLine, heading) = headings[h];
    string headingUpper = heading.ToUpperInvariant();

    // Find which output file this heading maps to
    string? outputFile = null;
    foreach (var (key, file) in sectionMap)
    {
        if (headingUpper.Contains(key.ToUpperInvariant()))
        {
            outputFile = file;
            break;
        }
    }

    if (outputFile is null) continue;
    if (written.Contains(outputFile)) continue;

    // Section runs from this heading to the next heading
    int endLine = h + 1 < headings.Count ? headings[h + 1].lineIndex : lines.Length;

    var sectionLines = lines[startLine..endLine];
    string sectionText = string.Join('\n', sectionLines);

    string cleaned = CleanText(sectionText, heading);

    string outPath = Path.Combine(helpDir, outputFile);
    await File.WriteAllTextAsync(outPath, cleaned, Encoding.UTF8);
    written.Add(outputFile);
    extracted++;

    Console.WriteLine($"  → {outputFile,-20} ({cleaned.Length / 1024} KB, lines {startLine}–{endLine})");
}

Console.WriteLine();
Console.WriteLine($"Done. Extracted {extracted} sections to: {helpDir}");

if (extracted < sectionMap.Count)
{
    var missed = sectionMap.Values.Distinct().Except(written, StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine();
    Console.WriteLine("Not found in source (these games postdate the 1917 book):");
    foreach (var m in missed)
        Console.WriteLine($"  {m}");
}

// ── Text cleanup ──────────────────────────────────────────────────────────────

static string CleanText(string raw, string gameTitle)
{
    var sb = new StringBuilder();

    // Markdown title from the ALL-CAPS heading
    sb.AppendLine($"# {ToTitleCase(gameTitle)}");
    sb.AppendLine();
    sb.AppendLine("*Rules from Foster's Complete Hoyle (1917), via Project Gutenberg.*");
    sb.AppendLine();

    // Strip Gutenberg header/footer boilerplate (shouldn't appear mid-text, but be safe)
    raw = Regex.Replace(raw, @"\*\*\* START OF.*?\*\*\*", "", RegexOptions.Singleline);
    raw = Regex.Replace(raw, @"\*\*\* END OF.*?\*\*\*",   "", RegexOptions.Singleline);

    // Convert _=HEADING=_ markup → ## Markdown heading
    raw = Regex.Replace(raw, @"_=(.+?)=_", m => $"## {ToTitleCase(m.Groups[1].Value)}");

    // Convert _italic_ → *italic*
    raw = Regex.Replace(raw, @"\b_([^_\n]+)_\b", "*$1*");

    // Remove page numbers like [Pg 207]
    raw = Regex.Replace(raw, @"\[Pg\s*\d+\]", "");

    // Collapse 3+ blank lines to 2
    raw = Regex.Replace(raw, @"\n{3,}", "\n\n");

    // Trim leading whitespace from each line (Gutenberg uses indentation for emphasis)
    var lines = raw.Split('\n').Select(l => l.TrimEnd()).ToArray();
    sb.Append(string.Join('\n', lines).Trim());

    return sb.ToString();
}

static string ToTitleCase(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return s;
    return string.Join(' ', s.Trim().TrimEnd('.').Split(' ')
        .Select(word => word.Length == 0 ? word
            : char.ToUpper(word[0]) + word[1..].ToLower()));
}
