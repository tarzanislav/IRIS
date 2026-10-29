using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Iris.App.Platform;

/// <summary>Whether Windows is in dark mode, and a dark title bar for a window in the dark theme.</summary>
internal static unsafe class WindowsTheme
{
    private const int UseImmersiveDarkMode = 20;

    public static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void TitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var value = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(handle, UseImmersiveDarkMode, &value, sizeof(int));
    }
}
