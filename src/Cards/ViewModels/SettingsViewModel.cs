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
