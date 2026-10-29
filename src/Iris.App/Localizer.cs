using System.ComponentModel;
using Iris.Assistant.Strings;

namespace Iris.App;

/// <summary>
/// The interface texts for bindings: {Binding [nav.translate], Source={x:Static app:Localizer.Instance}}. Switching
/// the language updates every bound label at once.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    private Texts _texts = Texts.For(null);

    private Localizer()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Texts Texts => _texts;

    public string this[string key] => _texts[key];

    public string Format(string key, params object[] values) => _texts.Format(key, values);

    /// <summary>Switches the interface language ("sk", "cs", "en", or null = the system's).</summary>
    public void Use(string? language)
    {
        _texts = Texts.For(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Texts)));
    }
}
