using System.Runtime.InteropServices;

namespace VeliShell.UpdateService;

/// <summary>
/// Minimal Service Control Manager host. It deliberately has no interactive-session,
/// process-launch, service-registration, installation, or elevation functionality.
/// </summary>
public sealed class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorFailedServiceControllerConnect = 1063;

    private readonly object _statusLock = new();
    private readonly string _serviceName;
    private readonly Func<CancellationToken, Task> _runAsync;
    private readonly CancellationTokenSource _stop = new();
    private readonly ServiceMainDelegate _serviceMainDelegate;
    private readonly ServiceControlHandlerDelegate _controlHandlerDelegate;
    private nint _statusHandle;
    private uint _state = ServiceStopped;

    public WindowsServiceHost(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName;
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        _serviceMainDelegate = ServiceMain;
        _controlHandlerDelegate = HandleControl;
    }

    public int Run()
    {
        if (!OperatingSystem.IsWindows())
            return (int)Win32Error.NotSupported;

        var serviceNamePointer = Marshal.StringToHGlobalUni(_serviceName);
        try
        {
            var serviceTable = new[]
            {
                new ServiceTableEntry(
                    serviceNamePointer,
                    Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate)),
                default
            };
            if (StartServiceCtrlDispatcherW(serviceTable)) return 0;

            var error = Marshal.GetLastWin32Error();
            return error == 0 ? ErrorFailedServiceControllerConnect : error;
        }
        finally
        {
            Marshal.FreeHGlobal(serviceNamePointer);
            GC.KeepAlive(_serviceMainDelegate);
            GC.KeepAlive(_controlHandlerDelegate);
        }
    }

    private void ServiceMain(uint argumentCount, nint arguments)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(
            _serviceName,
            _controlHandlerDelegate,
            nint.Zero);
        if (_statusHandle == nint.Zero) return;

        uint exitCode = 0;
        try
        {
            ReportStatus(ServiceStartPending, waitHintMilliseconds: 30_000);
            ReportStatus(ServiceRunning);
            _runAsync(_stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Normal Service Control Manager stop or shutdown request.
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            ReportStatus(ServiceStopped, win32ExitCode: exitCode);
        }
    }

    private uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        if (control is not (ServiceControlStop or ServiceControlShutdown)) return 0;

        var shouldCancel = false;
        lock (_statusLock)
        {
            if (_state is ServiceStopped or ServiceStopPending) return 0;
            ReportStatusCore(ServiceStopPending, waitHintMilliseconds: 30_000);
            shouldCancel = true;
        }
        if (shouldCancel) _stop.Cancel();

        return 0;
    }

    private void ReportStatus(uint state, uint waitHintMilliseconds = 0, uint win32ExitCode = 0)
    {
        lock (_statusLock)
        {
            ReportStatusCore(state, waitHintMilliseconds, win32ExitCode);
        }
    }

    private void ReportStatusCore(uint state, uint waitHintMilliseconds = 0, uint win32ExitCode = 0)
    {
        _state = state;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceRunning
                ? ServiceAcceptStop | ServiceAcceptShutdown
                : 0,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = state is ServiceStartPending or ServiceStopPending ? 1u : 0u,
            WaitHint = waitHintMilliseconds
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    private enum Win32Error
    {
        NotSupported = 50
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ServiceTableEntry
    {
        public ServiceTableEntry(nint serviceName, nint serviceMain)
        {
            ServiceName = serviceName;
            ServiceMain = serviceMain;
        }

        public readonly nint ServiceName;
        public readonly nint ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(uint argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlHandlerDelegate(
        uint control,
        uint eventType,
        nint eventData,
        nint context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW(
        [In] ServiceTableEntry[] serviceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerExW(
        string serviceName,
        ServiceControlHandlerDelegate handler,
        nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint serviceStatusHandle, ref ServiceStatus serviceStatus);
}
