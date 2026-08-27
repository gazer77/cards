using Cards.Engine;
using Cards.Models;

namespace Cards.Pages;

[QueryProperty(nameof(GameId),   "GameId")]
[QueryProperty(nameof(GameName), "GameName")]
public partial class HelpPage : ContentPage
{
    private readonly GameLoader _loader;
    private readonly IGameAssetSource _assets;

    public HelpPage(GameLoader loader, IGameAssetSource assets)
    {
        InitializeComponent();
        _loader = loader;
        _assets = assets;
    }

    public string GameId   { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        GameTitleLabel.Text = GameName;
        await LoadHelpAsync();
    }

    /// <summary>
    /// Joins hard-wrapped lines within each paragraph into a single line so text
    /// flows naturally on narrow screens.  Blank-line paragraph boundaries and
    /// lines that start a new markdown block (headers, list items) are preserved.
    /// </summary>
    private static string ReflowMarkdown(string text)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // Split on one or more blank lines to get paragraphs
        var paragraphs = System.Text.RegularExpressions.Regex.Split(text, @"\n{2,}");

        var output = new System.Text.StringBuilder();
        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length == 0) continue;

            if (output.Length > 0) output.Append("\n\n");

            // Lines that are structural: headers, list items, horizontal rules.
            // Keep each line separate so the block structure is intact.
            if (trimmed.StartsWith('#') ||
                trimmed.StartsWith('-') ||
                trimmed.StartsWith('*') ||
                trimmed.StartsWith('>') ||
                trimmed.StartsWith("---"))
            {
                output.Append(trimmed);
            }
            else
            {
                // Ordinary paragraph: join hard-wrapped lines with a space.
                var lines = trimmed.Split('\n');
                output.Append(string.Join(" ", lines.Select(l => l.Trim())));
            }
        }

        return output.ToString();
    }

    private async Task LoadHelpAsync()
    {
        LoadingIndicator.IsVisible = true;
        ContentLabel.IsVisible     = false;

        try
        {
            var definition = await _loader.LoadAsync(GameId);
            string? helpFile = definition?.Help;
            string content   = string.Empty;

            if (helpFile is not null)
            {
                try
                {
                    using var stream = await _assets.OpenAsync($"games/help/{helpFile}");
                    using var reader = new StreamReader(stream);
                    content = await reader.ReadToEndAsync();
                }
                catch
                {
                    content = "Help file not yet available for this game.";
                }
            }
            else
            {
                content = "No help file defined for this game.";
            }

            ContentLabel.Text = ReflowMarkdown(content);
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            ContentLabel.IsVisible     = true;
        }
    }
}
