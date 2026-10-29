namespace Iris.App.Platform;

/// <summary>
/// The IRIS icon in the notification area: a left click opens the window, a right click shows the menu. It is added
/// again when Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint Id = 1;
    private const int CallbackMessage = Native.WmApp + 1;
    private readonly MessageWindow _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly string _tip;
    private bool _added;

    public TrayIcon(MessageWindow window, string tip)
    {
        _window = window;
        _tip = tip;
        nint small = 0;
        Native.ExtractIconExW(Environment.ProcessPath ?? "", 0, null, &small, 1);
        _icon = small;
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        window.Listen(OnMessage);
        Add();
    }

    public event Action? Open;

    public event Action? Menu;

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Native.Shell_NotifyIconW(Native.NimDelete, &data);
            _added = false;
        }

        if (_icon != 0)
        {
            Native.DestroyIcon(_icon);
        }
    }

    private void Add()
    {
        var data = Data();
        _added = Native.Shell_NotifyIconW(Native.NimAdd, &data);
        data.Version = Native.NotifyIconVersion4;
        Native.Shell_NotifyIconW(Native.NimSetVersion, &data);
    }

    private Native.NotifyIconData Data()
    {
        var data = new Native.NotifyIconData
        {
            Size = (uint)sizeof(Native.NotifyIconData),
            Window = _window.Handle,
            Id = Id,
            Flags = Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifShowTip,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        var tip = _tip.AsSpan(0, Math.Min(_tip.Length, 127));
        tip.CopyTo(new Span<char>(data.Tip, 128));
        return data;
    }

    private bool OnMessage(int message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            Add();
            return true;
        }

        if (message != CallbackMessage)
        {
            return false;
        }

        // version 4: the event is in the low word of lParam
        switch ((int)(lParam & 0xFFFF))
        {
            case Native.WmLButtonUp:
                Open?.Invoke();
                break;
            case Native.WmRButtonUp or Native.WmContextMenu:
                Native.SetForegroundWindow(_window.Handle);
                Menu?.Invoke();
                break;
        }

        return true;
    }
}
