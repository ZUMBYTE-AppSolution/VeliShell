using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class ShellLinkService
{
    private static readonly object ArtifactGate = new();
    private static readonly HashSet<string> OwnedArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<string> DragRoot = new(CreateSessionDirectory, true);

    internal static string? ResolveTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath) || !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        object? instance = null;
        try
        {
            instance = new ShellLinkComObject();
            ((IPersistFile)instance).Load(shortcutPath, 0);
            var path = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(path, path.Capacity, 0, 0);
            return path.Length == 0 ? null : Environment.ExpandEnvironmentVariables(path.ToString());
        }
        catch (Exception ex)
        {
            App.Log("Could not resolve a shell link", ex);
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    internal static string? CreateDragArtifact(Pin pin)
    {
        try
        {
            var dragRoot = EnsureSafeSessionDirectory();
            var name = SafeName(pin.Name);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            if (File.Exists(pin.Target) && pin.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var copy = Path.Combine(dragRoot, $"{name}-{suffix}.lnk");
                File.Copy(pin.Target, copy, overwrite: false);
                return RegisterArtifact(copy);
            }

            if (!File.Exists(pin.Target) && !Directory.Exists(pin.Target) && Uri.TryCreate(pin.Target, UriKind.Absolute, out var uri))
            {
                var shortcut = Path.Combine(dragRoot, $"{name}-{suffix}.url");
                var safeUri = uri.AbsoluteUri.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=" + safeUri + "\r\n", new UTF8Encoding(false));
                return RegisterArtifact(shortcut);
            }

            var linkPath = Path.Combine(dragRoot, $"{name}-{suffix}.lnk");
            CreateShortcut(linkPath, pin.Target, pin.Name);
            return RegisterArtifact(linkPath);
        }
        catch (Exception ex)
        {
            App.Log("Could not create drag-out shortcut", ex);
            return null;
        }
    }

    internal static void CleanupDragArtifact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string candidate;
        try
        {
            candidate = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            App.Log("Could not validate drag-out shortcut", ex);
            return;
        }

        lock (ArtifactGate)
        {
            if (!OwnedArtifacts.Remove(candidate)) return;
        }

        try
        {
            var root = EnsureSafeSessionDirectory();
            if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(candidate)) File.Delete(candidate);
            TryRemoveEmptySessionDirectory(root);
        }
        catch (Exception ex) { App.Log("Could not remove drag-out shortcut", ex); }
    }

    private static void CreateShortcut(string outputPath, string target, string description)
    {
        object? instance = null;
        try
        {
            instance = new ShellLinkComObject();
            var link = (IShellLinkW)instance;
            link.SetPath(Environment.ExpandEnvironmentVariables(target));
            link.SetDescription(description.Length > 200 ? description[..200] : description);
            var workingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(workingDirectory)) link.SetWorkingDirectory(workingDirectory);
            if (File.Exists(target)) link.SetIconLocation(target, 0);
            ((IPersistFile)instance).Save(outputPath, true);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string((value ?? LocalizationService.Current.Get("ShellLink.Application")).Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        if (text.Length == 0) text = LocalizationService.Current.Get("ShellLink.Application");
        return text.Length > 60 ? text[..60] : text;
    }

    private static string CreateSessionDirectory()
    {
        var velishellRoot = Path.Combine(Path.GetTempPath(), "VeliShell");
        EnsurePlainDirectory(velishellRoot);
        var dragParent = Path.Combine(velishellRoot, "DragOut");
        EnsurePlainDirectory(dragParent);
        var session = Path.Combine(dragParent, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        EnsurePlainDirectory(session);
        return Path.GetFullPath(session).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string EnsureSafeSessionDirectory()
    {
        var root = DragRoot.Value;
        EnsurePlainDirectory(root);
        return root;
    }

    private static void EnsurePlainDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(LocalizationService.Current.Get("ShellLink.UnsafeTemporaryPath"));
    }

    private static string RegisterArtifact(string path)
    {
        var candidate = Path.GetFullPath(path);
        var root = EnsureSafeSessionDirectory();
        if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException(LocalizationService.Current.Get("ShellLink.InvalidTemporaryPath"));
        lock (ArtifactGate) OwnedArtifacts.Add(candidate);
        return candidate;
    }

    private static void TryRemoveEmptySessionDirectory(string root)
    {
        lock (ArtifactGate)
        {
            if (OwnedArtifacts.Count != 0) return;
        }

        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(root, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Explorer can retain the dropped file briefly. Leaving an empty random
            // session directory is safer than broad cleanup of a shared temp path.
        }
    }
}
