using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record NativeTrayElement(uint ExplorerPid, ulong ExplorerStarted, string Name, int[] RuntimeId);

internal interface INativeTrayMenuBackend
{
    IReadOnlyList<NativeTrayElement> Read();
    bool Show(NativeTrayElement target);
}

/// <summary>Public UIA context-menu invocation; no guessed WM messages or input.</summary>
internal sealed class NativeTrayMenuBridge(INativeTrayMenuBackend backend)
{
    internal static NativeTrayMenuBridge Current { get; } = new(new WindowsBackend());
    private IReadOnlyList<NativeTrayElement> _targets = [];
    private readonly object _refreshGate = new();
    private long _refreshAfter;

    internal void Refresh()
    {
        lock (_refreshGate)
        {
            if (Environment.TickCount64 < _refreshAfter) return;
            Volatile.Write(ref _targets, backend.Read().Take(256).ToArray());
            _refreshAfter = Environment.TickCount64 + 8000;
        }
    }

    internal NativeTrayElement? FindTarget(string name, string executable, string? originalTooltip = null) =>
        SelectTarget(Volatile.Read(ref _targets), name, executable, originalTooltip);

    internal static NativeTrayElement? SelectTarget(IReadOnlyList<NativeTrayElement> targets, string name,
        string executable, string? originalTooltip = null)
    {
        // Executable basenames are not application identity (e.g. ChatGPT and
        // Codex can both use ChatGPT.exe). Prefer the actual app/tooltip labels.
        var names = new[] { name, originalTooltip ?? "" }
            .Select(NormalizeCaption).Where(caption => caption.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) names.Add(NormalizeCaption(Path.GetFileNameWithoutExtension(executable)));
        names.Remove("");
        var candidates = targets.Where(target => target.ExplorerPid > 0 && target.ExplorerStarted > 0 &&
                target.RuntimeId is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(target.Name) &&
                target.Name.Length <= 512 && !target.Name.Contains('\0') && names.Contains(NormalizeCaption(target.Name)))
            .DistinctBy(target => target.ExplorerPid + ":" + target.ExplorerStarted + ":" + string.Join(",", target.RuntimeId))
            .Take(2).ToArray();
        // Cache captions are not proof when two original icons share a name.
        // Never open another app's menu just because its label looks similar.
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string NormalizeCaption(string value) => value.Split(['\r', '\n'], 2)[0].Trim();

    internal bool TryShow(NativeTrayElement? target, bool userRequested) =>
        userRequested && target is { ExplorerPid: > 0, ExplorerStarted: > 0, Name.Length: > 0 and <= 512,
            RuntimeId.Length: > 0 and <= 64 } && !target.Name.Contains('\0') &&
        Volatile.Read(ref _targets).Any(current => current.ExplorerPid == target.ExplorerPid &&
            current.ExplorerStarted == target.ExplorerStarted && current.Name == target.Name &&
            current.RuntimeId.SequenceEqual(target.RuntimeId)) && backend.Show(target);

    private sealed class WindowsBackend : INativeTrayMenuBackend
    {
        public IReadOnlyList<NativeTrayElement> Read()
        {
            try
            {
                if (NativeMethods.VeliShellTrayProtocol() != 1) return [];
                var text = new StringBuilder(262144);
                return NativeMethods.VeliShellReadTrayElements(text, text.Capacity) == 0
                    ? JsonSerializer.Deserialize<NativeTrayElement[]>(text.ToString())?.Take(256).ToArray() ?? [] : [];
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or JsonException or COMException or ArgumentException) { return []; }
        }

        public bool Show(NativeTrayElement target)
        {
            try
            {
                return NativeMethods.VeliShellShowTrayContextMenu(target.ExplorerPid, target.ExplorerStarted,
                    target.RuntimeId, target.RuntimeId.Length, target.Name) == 0;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or COMException or ArgumentException) { return false; }
        }
    }
}
