using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>Explicit opt-in, actual Explorer integration with only our hidden test icons.</summary>
internal static class TrayRelayDiagnostics
{
    internal const string Option = "--tray-bridge-check";
    internal static void Run(App app)
    {
        TrayRelayController.DiagnosticMode = true;
        Directory.CreateDirectory(TrayRelayController.Folder);
        var report = Path.Combine(TrayRelayController.Folder, "check.json");
        var results = new List<string>();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var frame = new DispatcherFrame(); app.Exit += (_, _) => frame.Continue = false;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var exit = 1;
            var settingsPath = Path.Combine(App.DataDirectory, "settings.json");
            var settingsHash = File.Exists(settingsPath) ? SHA256.HashData(File.ReadAllBytes(settingsPath)) : [];
            var tray = NativeMethods.FindWindowEx(0, 0, "Shell_TrayWnd", null);
            var visible = tray != 0 && NativeMethods.IsWindowVisible(tray);
            using var source = new HwndSource(new HwndSourceParameters("VeliShell hidden tray fixture")
                { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
            var received = new List<(nuint Wparam, long Lparam)>();
            const int callback = 0x8b01;
            source.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            { if (message == callback) { received.Add(((nuint)wp, lp.ToInt64())); handled = true; } return 0; });
            NativeMethods.NotifyIconData Icon(uint id, string guid) => new()
            {
                Size = checked((uint)Marshal.SizeOf<NativeMethods.NotifyIconData>()), Window = source.Handle, Id = id,
                Flags = 1 | 2 | 4 | 8 | 0x20, Callback = callback, Icon = NativeMethods.LoadSharedIcon(0, (nint)32512),
                Tip = "VeliShell hidden callback check", Info = "", InfoTitle = "", State = 1, StateMask = 1, Guid = Guid.Parse(guid)
            };
            var legacy = Icon(0x12345678, "9a6809d3-f8bd-44d5-97a0-8af344baf101");
            var modern = Icon(0x4567, "9a6809d3-f8bd-44d5-97a0-8af344baf102");
            using var controller = new TrayRelayController();
            void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException(label); results.Add(label); }
            async Task Until(Func<bool> test)
            {
                var watch = Stopwatch.StartNew();
                while (!test() && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(100);
                Check(test(), "Condition confirmed within eight seconds");
            }
            try
            {
                await controller.EnableAsync(true);
                Check(controller.Active, "Targeted Explorer bridge enabled with independent guard");
                Check(NativeMethods.ShellNotifyIcon(0, ref legacy), "Legacy test icon registered, hidden with NIS_HIDDEN");
                Check(NativeMethods.ShellNotifyIcon(0, ref modern), "Modern test icon registered, hidden with NIS_HIDDEN");
                modern.Version = 4;
                Check(NativeMethods.ShellNotifyIcon(4, ref modern), "Version-four registration accepted by real Explorer");
                var ownPath = Environment.ProcessPath!;
                await Until(() => controller.FindTarget(ownPath, legacy.Guid) is not null && controller.FindTarget(ownPath, modern.Guid) is not null);
                var first = controller.FindTarget(ownPath, legacy.Guid)!;
                var second = controller.FindTarget(ownPath, modern.Guid)!;
                Check(first.Id == legacy.Id && first.Version == 0 && second.Id == modern.Id && second.Version == 4,
                    "Captured real icon IDs, message IDs and protocol versions");
                Check(!controller.Show(first, 10, 20, false) && received.Count == 0, "Background discovery never invokes a menu");
                Check(controller.Show(first, 10, 20, true), "Legacy callback dispatch succeeded");
                await Until(() => received.Count >= 2);
                Check(received.All(item => item.Wparam == legacy.Id) && received.Any(item => item.Lparam == 0x205),
                    "Legacy callback preserved full 32-bit icon ID and right-button message");
                received.Clear();
                Check(controller.Show(second, -100, 150, true), "Version-four callback dispatch succeeded");
                await Until(() => received.Count >= 3);
                var context = received.Single(item => (item.Lparam & 0xffff) == 0x7b);
                Check(((context.Lparam >> 16) & 0xffff) == modern.Id && (short)(context.Wparam & 0xffff) == -100 &&
                    (short)((context.Wparam >> 16) & 0xffff) == 150, "Version-four packed ID and negative screen coordinates are correct");
                Check(!controller.Show(first with { Started = first.Started + 1 }, 0, 0, true), "Stale process identity cannot dispatch");
                NativeMethods.ShellNotifyIcon(2, ref legacy); NativeMethods.ShellNotifyIcon(2, ref modern);
                using (var recovery = NativePrototypeSession.Start([TrayRelayController.Recover, "qa"])) await recovery.WaitForExitAsync();
                await Until(() => !controller.Active && controller.Failed);
                Check(File.Exists(TrayRelayController.Latch), "Independent emergency command stopped the bridge and latched reactivation");
                var resumed = false;
                try { await controller.EnableAsync(false); resumed = true; } catch (InvalidOperationException) { }
                Check(!resumed, "Emergency recovery prevents automatic reactivation");
                await controller.EnableAsync(true);
                Check(controller.Active, "Explicit reactivation after emergency succeeds");
                Check(await controller.StopAsync(), "Normal shutdown removes the bridge and exits its helper");
                await controller.EnableAsync(true);
                var crashedSession = controller.DiagnosticSession;
                controller.FailDiagnosticGuard();
                await Until(() => !controller.Active && controller.Failed);
                await Task.Delay(4200);
                Check(TrayRelayController.ReadCallbacks(crashedSession).Count == 0,
                    "Guard termination invalidates callbacks without restarting Explorer");
                Check(File.Exists(TrayRelayController.Latch), "Guard failure requires explicit reactivation");
                Check((File.Exists(settingsPath) ? SHA256.HashData(File.ReadAllBytes(settingsPath)) : []).SequenceEqual(settingsHash),
                    "Real user preferences are unchanged");
                Check((tray != 0 && NativeMethods.IsWindowVisible(tray)) == visible, "Windows taskbar visibility is unchanged");
                exit = 0;
            }
            catch (Exception error) { results.Add("FAIL: " + error.Message); App.Log("Tray integration check failed", error); }
            finally
            {
                NativeMethods.ShellNotifyIcon(2, ref legacy); NativeMethods.ShellNotifyIcon(2, ref modern);
                await controller.StopAsync();
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = exit == 0, Checks = results,
                    TaskbarWasVisible = visible, NoForeignMenusInvoked = true }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(exit);
            }
        }), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
    }
}
