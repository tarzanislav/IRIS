using System.Windows.Interop;

namespace Iris.App.Platform;

/// <summary>
/// An invisible window that only receives messages: the global shortcut and the clicks on the tray icon arrive here,
/// whether the main window is open, hidden or not created yet.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private static readonly nint MessageOnly = -3;
    private readonly HwndSource _source;
    private readonly List<Func<int, nint, nint, bool>> _handlers = [];

    public MessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("IRIS messages") { ParentWindow = MessageOnly, Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    public nint Handle => _source.Handle;

    /// <summary>Adds a handler: it returns true when it handled the message.</summary>
    public void Listen(Func<int, nint, nint, bool> handler) => _handlers.Add(handler);

    public void Dispose() => _source.Dispose();

    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        foreach (var handler in _handlers)
        {
            if (handler(message, wParam, lParam))
            {
                handled = true;
                break;
            }
        }

        return 0;
    }
}
