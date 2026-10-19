using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Services;

// Only target-side bootstrap/lease operations use this copy. Ordinary eligibility
// and drawing probes keep their existing imports. Never unload a live callback DLL.
internal sealed class NativeFrameRuntime
{
    internal static NativeFrameRuntime X64 => Runtime.Value;
    private static readonly Lazy<NativeFrameRuntime> Runtime = new(() => {
        using var pins = NativeFrameRuntimeCache.Shared.PinBundle(false);
        return new(NativeFrameRuntimeCache.Shared.Library(false));
    });
    private readonly nint _module;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate nint BeginCall(nint window, string session, uint pid, ulong started, int dark, int gloss, int transparency, uint tint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollCall(nint lease, int stop);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LeaseCall(nint lease);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ConfigureCall(nint lease, int dark, int gloss, int transparency, uint tint);
    private readonly BeginCall _classic, _modern;
    private readonly PollCall _poll;
    private readonly LeaseCall _end, _error;
    private readonly ErrorCall _beginError;
    private readonly ConfigureCall _configure;
    internal NativeFrameRuntime(string path)
    {
        _module = NativeLibrary.Load(path, typeof(NativeFrameRuntime).Assembly, DllImportSearchPath.System32);
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));
        _classic = Export<BeginCall>("VeliClassicBegin"); _modern = Export<BeginCall>("VeliModernBegin");
        _poll = Export<PollCall>("VeliClassicPoll"); _end = Export<LeaseCall>("VeliClassicEnd");
        _error = Export<LeaseCall>("VeliClassicError"); _beginError = Export<ErrorCall>("VeliClassicBeginError");
        _configure = Export<ConfigureCall>("VeliClassicConfigure");
    }
    internal nint Begin(ClassicFrameSession.Candidate app, Guid id, NativeFramesConfig config) =>
        (app.Adapter == 0 ? _classic : _modern)(app.Window, id.ToString("N"), (uint)app.Pid, (ulong)app.Started,
            config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
    internal int BeginError => _beginError();
    internal int Poll(nint lease, bool stop) => _poll(lease, stop ? 1 : 0);
    internal int End(nint lease) => _end(lease);
    internal int Error(nint lease) => _error(lease);
    internal void Configure(nint lease, NativeFramesConfig config) =>
        _configure(lease, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
}
