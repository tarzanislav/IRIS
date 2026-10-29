namespace Iris.App.Platform;

/// <summary>A shortcut that works in every application, e.g. Ctrl+Shift+T; registered with Windows while IRIS runs.</summary>
internal sealed class HotKey : IDisposable
{
    private const int Id = 0x1215;
    private readonly MessageWindow _window;
    private bool _registered;

    private HotKey(MessageWindow window) => _window = window;

    public event Action? Pressed;

    /// <summary>The shortcut as written ("Ctrl+Shift+T"); null when the text is not a shortcut.</summary>
    public static (uint Modifiers, uint Key)? Parse(string text)
    {
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= Native.ModControl;
                    break;
                case "SHIFT":
                    modifiers |= Native.ModShift;
                    break;
                case "ALT":
                    modifiers |= Native.ModAlt;
                    break;
                case "WIN":
                    modifiers |= Native.ModWin;
                    break;
                case [var c] when char.IsAsciiLetterOrDigit(c):
                    key = char.ToUpperInvariant(c);
                    break;
                case ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12:
                    key = (uint)(0x70 + f - 1);
                    break;
                default:
                    return null;
            }
        }

        return key != 0 && modifiers != 0 ? (modifiers, key) : null;
    }

    /// <summary>Registers a shortcut; null when Windows refuses it (another program already uses it).</summary>
    public static HotKey? Register(MessageWindow window, string shortcut)
    {
        if (Parse(shortcut) is not { } parsed)
        {
            return null;
        }

        var (modifiers, key) = parsed;
        var hotKey = new HotKey(window);
        if (!Native.RegisterHotKey(window.Handle, Id, modifiers | Native.ModNoRepeat, key))
        {
            return null;
        }

        hotKey._registered = true;
        window.Listen((message, wParam, _) =>
        {
            if (message != Native.WmHotKey || wParam != Id || !hotKey._registered)
            {
                return false;
            }

            hotKey.Pressed?.Invoke();
            return true;
        });
        return hotKey;
    }

    public void Dispose()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_window.Handle, Id);
            _registered = false;
        }
    }
}
