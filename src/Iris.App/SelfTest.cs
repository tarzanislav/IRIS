using System.IO;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Pages;

namespace Iris.App;

/// <summary>
/// <c>iris-app --selftest</c>: builds every window and page in every interface language and both themes, lays them
/// out without showing them, and ends with 0 (or 1 and the error on the error output). Used by CI after publishing,
/// because XAML mistakes (a missing style or brush) only show when the window is built.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--selftest";

    public static int Run(App app)
    {
        try
        {
            foreach (var theme in new[] { Theme.Light, Theme.Dark })
            {
                foreach (var language in Assistant.Strings.Texts.Languages)
                {
                    app.Update(s => s with { Theme = theme, InterfaceLanguage = language });
                    Build(new MainWindow());
                    foreach (var page in new UserControl[]
                    {
                        new TranslatePage(), new FilesPage(), new MemoryPage(), new SettingsPage(), new SoonPage("nav.voice"),
                    })
                    {
                        var host = new Window { Content = page };
                        Build(host);
                        host.Close();
                    }

                    var mini = new MiniWindow("A short test sentence.");
                    Build(mini);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            using var error = new StreamWriter(Console.OpenStandardError());
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    // measuring and arranging applies every template and style, as showing the window would
    private static void Build(Window window)
    {
        if (window.Content is UIElement content)
        {
            content.Measure(new Size(1100, 720));
            content.Arrange(new Rect(0, 0, 1100, 720));
            content.UpdateLayout();
        }
    }
}
