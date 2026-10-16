using System.Diagnostics;
using System.IO;
using System.Text;

namespace VeliShell.Desktop.Services;

internal static class UpdateRestartService
{
    internal static void Start(VerifiedUpdatePackage package)
    {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
            throw new FileNotFoundException("The running VeliShell executable was not found.");

        // The watcher is independent of the executable being replaced. It waits
        // for VeliShell to release shell state, then runs the visible Windows
        // Installer and relaunches the installed app only after MSI has exited.
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var msi = Quote(package.FilePath);
        var expectedHash = Quote(package.Verification.Sha256.ToUpperInvariant());
        var expectedVersion = Quote($"{package.Release.Version.Major}.{package.Release.Version.Minor}.{package.Release.Version.Patch}");
        var expectedProductVersion = Quote(package.Release.Version.ToString());
        var original = Quote(currentExecutable);
        var sameChannel = package.Release.Version.IsPrerelease == BuildIdentity.IsTest;
        var launchCandidates = sameChannel ? "@($original, $installed)" : "@($installed)";
        var installed = Quote(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Zumbyte", BuildIdentity.ProductFolder(package.Release.Version.IsPrerelease ? VeliShell.Core.UpdateChannel.Test : VeliShell.Core.UpdateChannel.Stable), "VeliShell.exe"));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
              if (Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue) {
                Wait-Process -Id {{Environment.ProcessId}} -Timeout 120 -ErrorAction Stop
              }
              $installer = {{msi}}
              $expectedHash = {{expectedHash}}
              $expectedVersion = {{expectedVersion}}
              $expectedProductVersion = {{expectedProductVersion}}
              $original = {{original}}
              $installed = {{installed}}
              if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) {
                throw 'The installer changed after download verification.'
              }
              $run = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList ('/i "' + $installer + '"') -PassThru -Wait
              Start-Sleep -Milliseconds 900
              $updated = $null
              if ($run.ExitCode -eq 0 -or $run.ExitCode -eq 3010) {
                foreach ($candidate in {{launchCandidates}}) {
                  if (-not (Test-Path -LiteralPath $candidate)) { continue }
                  $version = (Get-Item -LiteralPath $candidate).VersionInfo.FileVersion
                  $productVersion = (Get-Item -LiteralPath $candidate).VersionInfo.ProductVersion
                  if (($version -eq $expectedVersion -or $version.StartsWith($expectedVersion + '.')) -and ($productVersion -eq $expectedProductVersion -or $productVersion.StartsWith($expectedProductVersion + '+'))) {
                    $updated = $candidate
                    break
                  }
                }
              }
              if ($updated) {
                Start-Process -FilePath $updated
              } elseif (Test-Path -LiteralPath $original) {
                Start-Process -FilePath $original
              }
            } catch {
              if (Test-Path -LiteralPath {{original}}) { Start-Process -FilePath {{original}} }
            }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell is unavailable.", powershell);
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-WindowStyle");
        info.ArgumentList.Add("Hidden");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(encoded);
        using var process = Process.Start(info) ??
                            throw new InvalidOperationException("The update watcher did not start.");
    }
}
