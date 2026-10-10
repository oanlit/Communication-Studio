using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Data;

namespace Communication.App.Presentation;

public sealed record AppearancePreferences(string Theme = "Dark", string Language = "zh-CN");

/// <summary>Changes resources in place; active sessions and view models are never recreated.</summary>
public sealed class Appearance : INotifyPropertyChanged
{
    public static Appearance Current { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Dictionary<string, PaletteColor> _colors = [];
    public string Theme { get; private set; } = "Dark";
    public string Language { get; private set; } = "zh-CN";
    public int Revision
    {
        get; private set;
    }
    public bool IsDark => Theme == "Dark";
    public bool IsLight => !IsDark;
    public bool IsChinese => Language == "zh-CN";
    public bool IsEnglish => !IsChinese;
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommunicationStudio", "appearance.json");

    public static AppearancePreferences ReadPreferences(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path));
            return new(value?.Theme == "Light" ? "Light" : "Dark", value?.Language == "en-US" ? "en-US" : "zh-CN");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }

    }

    public static void WritePreferences(string path, AppearancePreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, true);
    }

    public void Initialize()
    {
        var value = ReadPreferences(SettingsPath);
        Apply(value.Theme, value.Language, false);
    }

    public void Apply(string theme, string language, bool save = true)
    {
        Application.Current.Dispatcher.VerifyAccess();
        theme = theme == "Light" ? "Light" : "Dark";
        language = language == "en-US" ? "en-US" : "zh-CN";
        var styles = Application.Current.Resources.MergedDictionaries.First(dictionary => dictionary.Source?.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        // Load first so a bad resource cannot leave the UI half-switched.
        var palette = new ResourceDictionary { Source = new Uri($"/CommunicationStudio;component/Themes/{theme}Palette.xaml", UriKind.Relative) };
        var labels = new ResourceDictionary { Source = new Uri($"/CommunicationStudio;component/Localization/{language}.xaml", UriKind.Relative) };
        // Keep mutable brush identity across switches, including cached templates,
        // virtualized rows and popup trees that aren't currently attached to a window.
        foreach (string key in palette.Keys)
        {
            var color = ((SolidColorBrush)palette[key]).Color;
            if (!_colors.TryGetValue(key, out var token))
            {
                token = new PaletteColor(color);
                var brush = new SolidColorBrush();
                // An expression prevents WPF from freezing shared template brushes.
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(PaletteColor.Color)) { Source = token });
                _colors.Add(key, token);
                styles[key] = brush;
                Application.Current.Resources[key] = brush;
            }
            else
                token.Color = color;
        }
        foreach (string key in labels.Keys)
        {
            styles[key] = labels[key];
            Application.Current.Resources[key] = labels[key];
        }
        Theme = theme;
        Language = language;
        Revision++;
        PropertyChanged?.Invoke(this, new(null));
        if (save)
            WritePreferences(SettingsPath, new(Theme, Language));
    }
}

internal sealed class PaletteColor(Color color) : INotifyPropertyChanged
{
    private Color _color = color;
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value)
                return;
            _color = value;
            PropertyChanged?.Invoke(this, new(nameof(Color)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
