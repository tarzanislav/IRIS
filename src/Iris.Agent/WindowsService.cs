using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Iris.Agent;

/// <summary>
/// The agent as a Windows service, with own calls of the service manager (no package): the service starts with
/// Windows under the system account, a student – a standard user – cannot stop it, and when it fails the service
/// manager starts it again (set by the installer). Stop and shutdown of Windows end it cleanly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsService
{
    private const uint OwnProcess = 0x10;
    private const uint Stopped = 1;
    private const uint StartPending = 2;
    private const uint StopPending = 3;
    private const uint Running = 4;
    private const uint AcceptStop = 0x1;
    private const uint AcceptShutdown = 0x4;
    private const uint ControlStop = 1;
    private const uint ControlShutdown = 5;
    private const uint ControlInterrogate = 4;

    private static Func<CancellationToken, Task>? _work;
    private static CancellationTokenSource? _stop;
    private static nint _handle;
    private static string _name = "";

    /// <summary>Hands the process to the service manager; returns when the service has stopped. False when not started as a service.</summary>
    public static bool Run(string name, Func<CancellationToken, Task> work)
    {
        _name = name;
        _work = work;
        var namePointer = Marshal.StringToHGlobalUni(name);
        try
        {
            var table = stackalloc ServiceTableEntry[2];
            table[0] = new ServiceTableEntry { Name = namePointer, Main = &ServiceMain };
            table[1] = default;
            return StartServiceCtrlDispatcherW(table);
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
        }
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, nint argv)
    {
        _stop = new CancellationTokenSource();
        var name = Marshal.StringToHGlobalUni(_name);
        try
        {
            _handle = RegisterServiceCtrlHandlerExW(name, &Handler, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        if (_handle == 0)
        {
            return;
        }

        Report(StartPending, 0);
        Report(Running, AcceptStop | AcceptShutdown);
        try
        {
            _work!(_stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Report(Stopped, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint type, nint data, nint context)
    {
        switch (control)
        {
            case ControlStop or ControlShutdown:
                Report(StopPending, 0);
                _stop?.Cancel();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private static void Report(uint state, uint accepted)
    {
        var status = new ServiceStatus
        {
            Type = OwnProcess,
            CurrentState = state,
            ControlsAccepted = accepted,
            WaitHint = state is StartPending or StopPending ? 30_000u : 0,
        };
        SetServiceStatus(_handle, ref status);
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(ServiceTableEntry* table);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial nint RegisterServiceCtrlHandlerExW(nint name, delegate* unmanaged<uint, uint, nint, nint, uint> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, ref ServiceStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public nint Name;
        public delegate* unmanaged<uint, nint, void> Main;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }
}
