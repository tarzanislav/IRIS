using System.Runtime.InteropServices;
using System.Windows;

namespace Iris.App.Platform;

/// <summary>
/// The text selected in another application: IRIS waits until the keys of the shortcut are released, sends Ctrl+C and
/// reads the clipboard; what was in the clipboard before is put back.
/// </summary>
internal static class Selection
{
    public static async Task<string?> CopyAsync()
    {
        // the shortcut's keys still held would turn Ctrl+C into another shortcut
        for (var waited = 0; waited < 1500 && AnyHeld(); waited += 20)
        {
            await Task.Delay(20);
        }

        var before = Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        Try(() =>
        {
            Clipboard.Clear();
            return true;
        });
        SendCopy();
        string? text = null;
        for (var waited = 0; waited < 800 && text is null; waited += 40)
        {
            await Task.Delay(40);
            text = Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        }

        if (before is not null)
        {
            Try(() =>
            {
                Clipboard.SetText(before);
                return true;
            });
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool AnyHeld() =>
        new[] { Native.VkShift, Native.VkControl, Native.VkMenu, Native.VkLWin, Native.VkRWin }
            .Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0);

    private static unsafe void SendCopy()
    {
        var inputs = stackalloc Native.Input[4];
        inputs[0] = Key(Native.VkControl, false);
        inputs[1] = Key(Native.VkC, false);
        inputs[2] = Key(Native.VkC, true);
        inputs[3] = Key(Native.VkControl, true);
        Native.SendInput(4, inputs, sizeof(Native.Input));
    }

    private static Native.Input Key(ushort key, bool up) => new()
    {
        Type = Native.InputKeyboard,
        Union = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Key = key, Flags = up ? Native.KeyEventKeyUp : 0 } },
    };

    // the clipboard is shared: another program may hold it for a moment
    private static T? Try<T>(Func<T?> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(30);
            }
            catch (COMException)
            {
                return default;
            }
        }
    }
}
