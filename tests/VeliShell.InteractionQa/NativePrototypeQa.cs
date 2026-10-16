using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

internal static partial class Program
{
    private static int VerifyNativeFrame()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            Process? demo = null;
            Window? scene = null;
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "native-prototype-renders"); Directory.CreateDirectory(folder);
                scene = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, Left = 70, Top = 80, Width = 1020, Height = 760,
                    Background = new LinearGradientBrush(Color.FromRgb(23, 86, 142), Color.FromRgb(112, 62, 130), 25) };
                scene.Content = new TextBlock { Text = "VeliShell · controlled glass backdrop", FontSize = 36, Foreground = Brushes.White, Margin = new Thickness(40) };
                scene.Show();
                foreach (var dark in new[] { false, true })
                {
                    demo = NativePrototypeSession.Start([NativePrototypeSession.Demo, dark ? "dark" : "light", "75", "65"]);
                    nint hwnd = 0;
                    for (var retry = 0; retry < 100; retry++)
                    {
                        demo.Refresh(); hwnd = demo.MainWindowHandle;
                        if (hwnd != 0 && NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) != 0) break;
                        if (demo.HasExited) throw new InvalidOperationException("Native demo exited: " + demo.ExitCode);
                        await Task.Delay(100);
                    }
                    Require(hwnd != 0 && NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) != 0, "Native DLL was not activated after recovery readiness.");
                    Require(NativePrototypeMethods.VeliFrameAttach(hwnd, 0, 0) == 0, "DLL must reject a foreign process HWND.");
                    Require(NativePrototypeMethods.VeliFrameAttach(new WindowInteropHelper(scene).Handle, 0, 0) == 0, "DLL must reject an own-process window without an opt-in marker.");
                    NativeMethods.SetWindowPos(hwnd, (nint)(-1), 110, 150, 860, 590, 0x0040);
                    NativeMethods.SetForegroundWindow(hwnd);
                    await Task.Delay(600);
                    NativeMethods.GetWindowRect(hwnd, out var bounds);
                    NativePrototypeMethods.GetClientRect(hwnd, out var client);
                    var origin = new NativeMethods.Point(); NativePrototypeMethods.ClientToScreen(hwnd, ref origin);
                    Require(origin.X == bounds.Left && origin.Y == bounds.Top && client.Right == bounds.Right - bounds.Left && client.Bottom == bounds.Bottom - bounds.Top,
                        "Native titlebar and app must share exactly the same HWND/client bounds.");
                    using (var bitmap = new System.Drawing.Bitmap(860, 590))
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(860, 590));
                        bitmap.Save(Path.Combine(folder, "native-" + (dark ? "dark" : "light") + ".png"));
                    }
                    // Exercise the actual WPF traffic-light buttons via client mouse messages, not API stand-ins.
                    static async Task Invoke(nint target, string key)
                    {
                        await Task.Run(() =>
                        {
                            var element = AutomationElement.FromHandle(target).FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.NameProperty, LocalizationService.Current.Get(key)));
                            Require(element is not null && element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern), "Button not exposed to accessibility: " + key);
                            ((InvokePattern)element!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        });
                    }
                    await Invoke(hwnd, "Window.Minimize"); await Task.Delay(400);
                    Require(NativeMethods.IsIconic(hwnd), "Left minimize button did not minimize the real window.");
                    NativeMethods.ShowWindowAsync(hwnd, 9); await Task.Delay(300);
                    await Invoke(hwnd, "Window.MaximizeAutomation"); await Task.Delay(400);
                    Require(NativeMethods.IsZoomed(hwnd), "Left maximize button did not maximize the real window.");
                    await Invoke(hwnd, "Window.MaximizeAutomation"); await Task.Delay(400);
                    Require(!NativeMethods.IsZoomed(hwnd), "Left maximize button did not restore the real window.");
                    await Invoke(hwnd, "Prototype.Restore"); await Task.Delay(200);
                    Require(NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) == 0, "Normal frame rollback failed.");
                    await Invoke(hwnd, "Prototype.Apply"); await Task.Delay(300);
                    Require(NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) != 0, "Frame could not be enabled again after normal rollback.");
                    // Only locate this test's guardian, never any other user's prototype session.
                    nint recoveryWindow = 0;
                    var sessionFile = Directory.EnumerateFiles(Path.Combine(VeliShell.Desktop.App.DataDirectory, "native-prototype"), "*.session")
                        .Single(file => File.ReadAllText(file) == demo.Id.ToString());
                    var sessionId = Path.GetFileNameWithoutExtension(sessionFile);
                    recoveryWindow = FindWindowExForQa((nint)(-3), 0, null, "VeliShell Native Prototype Recovery " + sessionId);
                    Require(recoveryWindow != 0, "Independent emergency handler was not found.");
                    NativePrototypeMethods.PostMessage(recoveryWindow, 0x312, 0x5650, 0);
                    for (var retry = 0; retry < 40 && NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) != 0; retry++) await Task.Delay(100);
                    Require(NativePrototypeMethods.GetProp(hwnd, NativePrototypeMethods.Active) == 0 &&
                        (NativeMethods.GetWindowLongPtr(hwnd, -16).ToInt64() & 0x00C00000) == 0x00C00000, "Independent emergency handler did not restore Windows caption bits.");
                    await Task.Delay(100); Require(!demo.HasExited, "Restoration must preserve the test app and its content.");
                    await Task.Run(() =>
                    {
                        var edit = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        Require(edit is not null && ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == LocalizationService.Current.Get("Prototype.Sample"),
                            "Rollback must preserve the existing app content.");
                    });
                    NativePrototypeMethods.PostMessage(hwnd, 0x10, 0, 0);
                    await demo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Require(demo.ExitCode == 0, "Demo did not exit cleanly.");
                    demo.Dispose(); demo = null;
                    await Task.Delay(600);
                    Console.WriteLine("PASS native " + (dark ? "dark" : "light") + ": single flush HWND, foreign HWND rejected, left buttons, independent emergency restore, clean exit");
                }
                exitCode = 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL native prototype: " + error); }
            finally
            {
                // These are the exact disposable child processes created by this test, never user apps.
                if (demo is not null) { if (!demo.HasExited) demo.Kill(); demo.Dispose(); }
                scene?.Close(); app.Shutdown(exitCode);
            }
        });
        app.Run(); return exitCode;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowExForQa(nint parent, nint after, string? className, string? title);
}
