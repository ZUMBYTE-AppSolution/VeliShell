using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VeliShell.Desktop.Services;

// Immutable, content-addressed copies outside the MSI payload. A target may pin
// its inactive adapter until it exits without locking files the installer replaces.
internal sealed class NativeFrameRuntimeCache(string sourceDirectory, string cacheDirectory)
{
    internal static NativeFrameRuntimeCache Shared { get; } = new(AppContext.BaseDirectory, Path.Combine(App.DataDirectory, "native-runtime"));
    private readonly string _source = Path.GetFullPath(sourceDirectory);
    private readonly string _cache = Path.GetFullPath(cacheDirectory);
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private string? _x64, _x86;

    internal string Library(bool x86) => Path.Combine(Bundle(x86), x86 ? "VeliShell.NativeFrame.x86.dll" : "VeliShell.NativeFrame.dll");
    internal string Bridge => Path.Combine(Bundle(true), "VeliShell.FrameBridge.x86.exe");

    internal IDisposable PinBundle(bool x86)
    {
        lock (_gate)
        {
            var directory = Bundle(x86);
            var streams = new List<FileStream>();
            try
            {
                foreach (var item in _hashes.Where(item => string.Equals(Path.GetDirectoryName(item.Key), directory, StringComparison.OrdinalIgnoreCase)))
                {
                    if ((File.GetAttributes(item.Key) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native runtime cache contains a reparse point.");
                    var input = new FileStream(item.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(input);
                    if (Convert.ToHexString(SHA256.HashData(input)) != item.Value) throw new IOException("Native runtime cache changed before loading.");
                }
                return new Pins(streams);
            }
            catch { foreach (var stream in streams) stream.Dispose(); throw; }
        }
    }
    private sealed class Pins(List<FileStream> streams) : IDisposable
    { public void Dispose() { foreach (var stream in streams) stream.Dispose(); } }

    private string Bundle(bool x86)
    {
        lock (_gate)
        {
            if ((x86 ? _x86 : _x64) is { } staged) return staged;
            if (IsWithin(_cache, _source)) throw new IOException("Native runtime cache must be outside the installation directory.");
            string[] names = x86 ? ["VeliShell.NativeFrame.x86.dll", "VeliShell.FrameBridge.x86.exe"] : ["VeliShell.NativeFrame.dll"];
            var sources = new List<(string Name, FileStream Stream, string Hash)>();
            try
            {
                foreach (var name in names)
                {
                    var path = Path.Combine(_source, name);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native source is a reparse point.");
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        if (stream.Length is < 128 or > 32 * 1024 * 1024) throw new IOException("Invalid native runtime payload size.");
                        sources.Add((name, stream, Convert.ToHexString(SHA256.HashData(stream))));
                    }
                    catch { stream.Dispose(); throw; }
                }
                var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sources.Select(s => s.Name + ":" + s.Hash)))));
                CheckDirectory(_cache);
                var directory = Path.Combine(_cache, identity);
                CheckDirectory(directory);
                foreach (var source in sources)
                {
                    var destination = Path.Combine(directory, source.Name);
                    if (!File.Exists(destination))
                    {
                        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            { source.Stream.Position = 0; source.Stream.CopyTo(output); output.Flush(true); }
                            try { File.Move(temporary, destination); }
                            catch (IOException) when (File.Exists(destination)) { /* Another own process staged the same bundle. Verify below. */ }
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native runtime cache contains a reparse point.");
                    using var cached = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (Convert.ToHexString(SHA256.HashData(cached)) != source.Hash) throw new IOException("Native runtime cache does not match the packaged binary.");
                    _hashes[destination] = source.Hash;
                }
                if (x86) _x86 = directory; else _x64 = directory;
                return directory;
            }
            finally { foreach (var source in sources) source.Stream.Dispose(); }
        }
    }

    internal static bool IsWithin(string path, string directory)
    {
        var parent = Path.GetFullPath(directory);
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native runtime cache path contains a reparse point.");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native runtime cache directory is a reparse point.");
    }
}
