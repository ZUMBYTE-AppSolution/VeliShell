using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

internal static partial class Program
{
    private static int VerifyWpfFrames()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        app.Dispatcher.BeginInvoke(async () => {
            var fixtures = new List<Process>(); Process? manager = null; Window? scene = null;
            try {
                scene = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, Left = 80, Top = 100, Width = 1100, Height = 740, Background = new LinearGradientBrush(Colors.DodgerBlue,Colors.MediumPurple,0) }; scene.Show();
                foreach (bool dark in new[] { false, true }) {
                    async Task<Process> Fixture(string kind) {
                        var process = NativePrototypeSession.Start([NativeFramesUtility.WpfFixture,kind]); fixtures.Add(process);
                        await Until(() => { process.Refresh(); return process.MainWindowHandle != 0; },"WPF fixture start"); return process;
                    }
                    var standard = await Fixture("standard"); var custom = await Fixture("custom");
                    nint one = standard.MainWindowHandle, two = custom.MainWindowHandle;
                    NativeMethods.SetWindowPos(two,0,1040,320,700,440,0x14);
                    int[] attributes = [20,33,34,35,36,38];
                    var original = attributes.Select(attribute => (Attribute:attribute,Supported:NativeMethods.DwmGetWindowAttribute(two,attribute,out var value,4)>=0,Value:value)).Where(x=>x.Supported).ToArray();
                    manager = NativePrototypeSession.Start([NativeFramesController.Preview,dark?"dark":"light"]);
                    await Until(() => { manager.Refresh(); return manager.MainWindowHandle != 0; },"WPF supervisor"); nint ui=manager.MainWindowHandle;
                    await EnableAutomaticUi(ui);
                    await Until(()=>NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Glass")==1,"WPF standard title bar admission");
                    Require(NativePrototypeMethods.GetProp(two,NativePrototypeMethods.ClassicActive)==0,"App-owned WPF title bar was replaced.");
                    Require(NativePrototypeMethods.VeliClassicReason(two,1) is 3 or 10,"Custom WPF layout was not identified.");
                    var render = Path.Combine(AppContext.BaseDirectory,"automatic-frame-renders"); Directory.CreateDirectory(render);
                    NativeMethods.SetWindowPos(one,(nint)(-1),120,160,800,480,0x40); await Task.Delay(500);
                    NativeMethods.GetWindowRect(one,out var bounds);
                    await Until(()=> { var visible=NativeMethods.WindowFromPoint(new NativeMethods.Point { X=bounds.Left+150,Y=bounds.Top+15 });NativeMethods.GetWindowThreadProcessId(visible,out var visiblePid);return visiblePid==standard.Id; },"Visible WPF QA surface");
                    using(var bitmap=new System.Drawing.Bitmap(800,480))using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) {
                        graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,new System.Drawing.Size(800,480)); bitmap.Save(Path.Combine(render,$"wpf-{(dark?"dark":"light")}.png"));
                    }
                    await SetMaterialsUi(ui,true);
                    await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")!=0,"Custom WPF native material");
                    Require(NativePrototypeMethods.GetProp(two,NativePrototypeMethods.ClassicActive)==0,"Material adaptation loaded a frame DLL into the custom WPF app.");
                    await Task.Run(()=> {
                        var edit=AutomationElement.FromHandle(two).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                        Require(edit is not null,"Custom caption search field disappeared.");
                        ((ValuePattern)edit!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Search remains usable");
                    });
                    void CheckOriginal() { foreach(var item in original) Require(NativeMethods.DwmGetWindowAttribute(two,item.Attribute,out var actual,4)>=0 && actual==item.Value,$"DWM attribute {item.Attribute} was not restored: expected {item.Value}, actual {actual}"); }
                    await SetMaterialsUi(ui,false); await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")==0,"Material opt-out rollback"); CheckOriginal();
                    Require(NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Glass")==1,"Material opt-out stopped unrelated classic frames.");
                    await SetMaterialsUi(ui,true); await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")!=0,"Material re-enable");
                    var recovery=FindAutomaticGuardian(manager.Id); NativeMethods.GetWindowThreadProcessId(recovery,out var guardPid);
                    using(var guard=Process.GetProcessById((int)guardPid)) {
                        int bridgePid=(int)NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active");
                        using(var bridge=Process.GetProcessById(bridgePid)) {
                            Require(string.Equals(bridge.MainModule?.FileName,NativeFrameRuntimeCache.Shared.Bridge,StringComparison.OrdinalIgnoreCase),"Wrong verified material bridge identity");
                            bridge.Kill(); await bridge.WaitForExitAsync();
                        }
                        await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")==0 && NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Glass")==0,"Independent bridge failure rollback");
                        await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); CheckOriginal();
                    }
                    await Task.Delay(1500); await EnableAutomaticUi(ui);
                    await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")!=0 && NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Glass")==1,"WPF explicit reactivation");
                    recovery=FindAutomaticGuardian(manager.Id); NativeMethods.GetWindowThreadProcessId(recovery,out guardPid);
                    using(var guard=Process.GetProcessById((int)guardPid)) { Require(guard.MainModule?.FileName==manager.MainModule?.FileName,"Wrong guard identity"); guard.Kill(); await guard.WaitForExitAsync(); }
                    await Until(()=>NativePrototypeMethods.GetProp(two,"VeliShell.Material.Active")==0 && NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Glass")==0,"WPF guard crash rollback"); CheckOriginal();
                    NativePrototypeMethods.PostMessage(ui,0x10,0,0); await manager.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); manager.Dispose(); manager=null;
                    foreach(var fixture in fixtures) { NativePrototypeMethods.PostMessage(fixture.MainWindowHandle,0x10,0,0); await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); fixture.Dispose(); } fixtures.Clear();
                    Console.WriteLine($"PASS WPF {(dark?"dark":"light")}: standard glass frame, preserved custom search/title bar, opt-in material, opt-out, bridge crash and guard crash recovery");
                }
                result=0;
            } catch(Exception error) { Console.WriteLine("FAIL WPF frame integration: "+error); }
            finally {
                if(manager is not null) { if(!manager.HasExited)manager.Kill();manager.Dispose(); }
                foreach(var fixture in fixtures) { if(!fixture.HasExited)fixture.Kill();fixture.Dispose(); }
                scene?.Close(); app.Shutdown(result);
            }
        }); app.Run(); return result;
    }
    private static async Task SetMaterialsUi(nint window,bool enable)
    {
        await Task.Run(()=> {
            var toggle=AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"NativeFramesMaterials"));
            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        });
        if(enable) {
            nint consent=0; await Until(()=> { consent=NativeMethods.GetWindow(window,6);return consent!=0&&consent!=window; },"Separate material opt-in");
            await Task.Run(()=> { var yes=AutomationElement.FromHandle(consent).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"6"));((InvokePattern)yes.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); });
        }
    }
}
