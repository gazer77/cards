using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cards.Services;

namespace Cards.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settings;

    // Available skins/themes for the pickers
    public List<string> CardSkinNames   { get; } = ["Simple (Phone)", "Classic"];
    public List<string> TableThemeNames { get; } = ["Casino Green"];

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        _selectedSkinIndex  = CardSkinNames.IndexOf(IdToSkinName(_settings.CardSkinId));
        _selectedThemeIndex = TableThemeNames.IndexOf(IdToThemeName(_settings.TableThemeId));
        if (_selectedSkinIndex  < 0) _selectedSkinIndex  = 0;
        if (_selectedThemeIndex < 0) _selectedThemeIndex = 0;
    }

    private int _selectedSkinIndex;
    public int SelectedSkinIndex
    {
        get => _selectedSkinIndex;
        set
        {
            _selectedSkinIndex = value;
            _settings.CardSkinId = SkinNameToId(CardSkinNames[value]);
            OnPropertyChanged();
        }
    }

    private int _selectedThemeIndex;
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            _selectedThemeIndex = value;
            _settings.TableThemeId = ThemeNameToId(TableThemeNames[value]);
            OnPropertyChanged();
        }
    }


    // ── Size and card points ──────────────────────────────────────────────────
    // One picker per element, sharing the table in Cards.Services.UiSizes so the
    // phone and the browser offer the same steps and write the same settings.

    public List<string> SizeNames { get; } = [.. UiSizes.All.Select(s => s.Label)];

    public int CardSizeIndex
    {
        get => IndexOfSize("cards");
        set { _settings.SetUiSize("cards", UiSizes.All[value].Id); OnPropertyChanged(); }
    }

    public int BubbleSizeIndex
    {
        get => IndexOfSize("bubbles");
        set { _settings.SetUiSize("bubbles", UiSizes.All[value].Id); OnPropertyChanged(); }
    }

    public int TooltipSizeIndex
    {
        get => IndexOfSize("tooltip");
        set { _settings.SetUiSize("tooltip", UiSizes.All[value].Id); OnPropertyChanged(); }
    }

    public int StatusSizeIndex
    {
        get => IndexOfSize("status");
        set { _settings.SetUiSize("status", UiSizes.All[value].Id); OnPropertyChanged(); }
    }

    public bool ShowCardValues
    {
        get => _settings.ShowCardValues;
        set { _settings.ShowCardValues = value; OnPropertyChanged(); }
    }

    private int IndexOfSize(string target)
    {
        string id = _settings.GetUiSize(target);
        for (int i = 0; i < UiSizes.All.Length; i++)
            if (UiSizes.All[i].Id == id) return i;
        return 0;
    }
    public bool ShowGameMessages
    {
        get => _settings.ShowGameMessages;
        set
        {
            _settings.ShowGameMessages = value;
            OnPropertyChanged();
        }
    }

    public bool AutoReady
    {
        get => _settings.AutoReady;
        set
        {
            _settings.AutoReady = value;
            OnPropertyChanged();
        }
    }

    public string AppVersion => AppInfo.VersionString;

    private static string IdToSkinName(string id) => id switch
    {
        "simple"  => "Simple (Phone)",
        "classic" => "Classic",
        _         => "Simple (Phone)",
    };
    private static string SkinNameToId(string name) => name switch
    {
        "Classic"       => "classic",
        "Simple (Phone)" => "simple",
        _               => "simple",
    };
    private static string IdToThemeName(string id)  => id switch { "casino-green" => "Casino Green", _ => "Casino Green" };
    private static string ThemeNameToId(string name) => name switch { "Casino Green" => "casino-green", _ => "casino-green" };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
