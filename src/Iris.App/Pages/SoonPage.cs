using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Iris.App.Pages;

/// <summary>A page of a later phase (Voice, Learning): its title and a note that it is coming.</summary>
public sealed class SoonPage : UserControl
{
    public SoonPage(string titleKey)
    {
        var title = new TextBlock { Style = (Style)FindResource("PageTitle") };
        title.SetBinding(TextBlock.TextProperty, new Binding($"[{titleKey}]") { Source = Localizer.Instance });
        var note = new TextBlock { Style = (Style)FindResource("Muted"), FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
        note.SetBinding(TextBlock.TextProperty, new Binding("[common.soon]") { Source = Localizer.Instance });
        Content = new StackPanel { Margin = new Thickness(32, 24, 32, 24), Children = { title, note } };
    }
}
