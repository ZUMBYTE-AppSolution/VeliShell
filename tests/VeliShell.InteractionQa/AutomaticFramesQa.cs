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
    private static int VerifyAutomaticFrames()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int code = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            var fixtures = new List<Process>(); Process? manager = null; Window? scene = null;
            try
            {
                if (!NativeFramesController.Supported) throw new InvalidOperationException("Windows 11 graphics desktop required for native frame QA.");
                string canonical = Path.Combine(AppContext.BaseDirectory,"VeliShell.ClassicSample.exe");
                string excludedPath = Path.Combine(AppContext.BaseDirectory,"VeliShell.ClassicSample.Excluded.exe");
                File.Copy(canonical,excludedPath,true);
                string renders = Path.Combine(AppContext.BaseDirectory,"automatic-frame-renders"); Directory.CreateDirectory(renders);
                scene = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = 80, Top = 100, Width = 1100, Height = 740,
                    Background = new LinearGradientBrush(Color.FromRgb(15,95,190),Color.FromRgb(122,45,165),0) }; scene.Show();
                async Task<Process> Spawn(string path)
                {
                    var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute=false })!; fixtures.Add(process);
                    await Until(()=> { process.Refresh(); return process.MainWindowHandle!=0; },"Sample app start"); return process;
                }
                foreach(bool dark in new[]{false,true})
                {
                    var first=await Spawn(canonical); var second=await Spawn(canonical); var excluded=await Spawn(excludedPath);
                    manager=NativePrototypeSession.Start([NativeFramesController.Preview,dark?"dark":"light"]);
                    await Until(()=> { manager.Refresh(); return manager.MainWindowHandle!=0; },"Automatic styling settings");
                    nint ui=manager.MainWindowHandle;
                    await Task.Run(()=> {
                        var root=AutomationElement.FromHandle(ui);
                        var input=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"NativeFramesException"));
                        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(Path.GetFileName(excludedPath));
                        InvokeNamed(root,LocalizationService.Current.Get("NativeFrames.Add"));
                    });
                    await EnableAutomaticUi(ui);
                    var one=first.MainWindowHandle; var two=second.MainWindowHandle;
                    bool Glass(nint w)=>NativePrototypeMethods.GetProp(w,"VeliShell.ClassicFrame.Glass")==1;
                    await Until(()=>Glass(one)&&Glass(two),"Automatic admission of two existing windows");
                    Require(!Glass(excluded.MainWindowHandle),"Excluded app was modified.");
                    var late=await Spawn(canonical); nint three=late.MainWindowHandle;
                    await Until(()=>Glass(three),"Automatic admission of a new app without another approval");
                    foreach(var other in new[]{second,excluded,late}) NativeMethods.SetWindowPos(other.MainWindowHandle,0,1040,320,700,440,0x14);
                    NativeMethods.SetWindowPos(one,(nint)(-1),120,160,800,480,0x40); NativeMethods.SetForegroundWindow(one); await Task.Delay(600);
                    var caption=NativePrototypeMethods.GetProp(one,"VeliShell.ClassicFrame.Caption");
                    NativeMethods.GetWindowRect(one,out var ownerBounds); NativeMethods.GetWindowRect(caption,out var header);
                    Require(header.Left>=ownerBounds.Left && header.Right<=ownerBounds.Right && header.Top==ownerBounds.Top,"Caption protrudes outside the original window.");
                    Require((NativeMethods.GetWindowLongPtr(caption,-20).ToInt64()&8)==(NativeMethods.GetWindowLongPtr(one,-20).ToInt64()&8),"Caption has a different topmost state than its owner.");
                    using(var bitmap=new System.Drawing.Bitmap(800,480))using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) {
                        graphics.CopyFromScreen(ownerBounds.Left,ownerBounds.Top,0,0,new System.Drawing.Size(800,480)); bitmap.Save(Path.Combine(renders,dark?"automatic-dark.png":"automatic-light.png"));
                    }
                    System.Drawing.Color Pixel() { using var b=new System.Drawing.Bitmap(1,1);using var g=System.Drawing.Graphics.FromImage(b);g.CopyFromScreen(header.Left+150,header.Top+15,0,0,new System.Drawing.Size(1,1));return b.GetPixel(0,0); }
                    var before=Pixel(); scene.Background=new SolidColorBrush(Color.FromRgb(205,95,20)); await Task.Delay(600); var after=Pixel();
                    Require(Math.Abs(before.R-after.R)+Math.Abs(before.G-after.G)+Math.Abs(before.B-after.B)>30,$"Native caption did not transmit a live backdrop change: {before} -> {after}.");
                    var cover=new Window { WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Left=header.Left,Top=header.Top,Width=800,Height=80,Background=Brushes.Lime };
                    cover.Show(); await Task.Delay(900); var covered=Pixel(); Require(covered.G>220 && covered.R<40,"An inactive title bar floated over an unrelated foreground window."); cover.Close(); await Task.Delay(300);
                    // Exercise the actual native caption host, not the owner's legacy NC handler.
                    static async Task ClickCaption(nint owner,int index) {
                        var host=NativePrototypeMethods.GetProp(owner,"VeliShell.ClassicFrame.Caption"); NativePrototypeMethods.GetClientRect(host,out var r);
                        nint point=(nint)(((r.Bottom/2)<<16)|(24+22*index)); NativePrototypeMethods.PostMessage(host,0x201,1,point); NativePrototypeMethods.PostMessage(host,0x202,0,point); await Task.Delay(300);
                    }
                    await ClickCaption(one,1); await Until(()=>NativeMethods.IsIconic(one),"Glass host minimize button"); NativeMethods.ShowWindowAsync(one,9); await Task.Delay(400);
                    await ClickCaption(one,2); await Until(()=>NativeMethods.IsZoomed(one),"Glass host maximize button"); await ClickCaption(one,2); await Until(()=>!NativeMethods.IsZoomed(one),"Glass host restore button");
                    var recovery=FindAutomaticGuardian(manager.Id); Require(recovery!=0,"Independent automatic recovery helper missing.");
                    NativePrototypeMethods.PostMessage(one,0x111,202,0); await Task.Delay(300);
                    NativePrototypeMethods.PostMessage(recovery,0x312,0x5650,0);
                    await Until(()=>!Glass(two)&&!Glass(three),"Recovery must restore responsive windows while one app is hung");
                    Require(Glass(one),"Hung app must remain pending until its UI resumes.");
                    await Until(()=>!Glass(one),"Recovery after hung app resumes"); await Task.Delay(1400);
                    Require(!Glass(one)&&!Glass(two)&&!Glass(three),"Emergency recovery automatically reapplied styling.");
                    NativeMethods.SetWindowPos(one,(nint)(-2),120,160,800,480,0x10);
                    await EnableAutomaticUi(ui); await Until(()=>Glass(one)&&Glass(two)&&Glass(three),"Explicit reactivation after emergency");
                    recovery=FindAutomaticGuardian(manager.Id); NativeMethods.GetWindowThreadProcessId(recovery,out var guardPid);
                    using(var guard=Process.GetProcessById((int)guardPid)) { Require(guard.MainModule?.FileName==manager.MainModule?.FileName,"Wrong guardian identity"); guard.Kill(); await guard.WaitForExitAsync(); }
                    await Until(()=>!Glass(one)&&!Glass(two)&&!Glass(three),"Target-side rollback after independent helper crash"); await Task.Delay(1200);
                    Require(!Glass(excluded.MainWindowHandle),"Excluded app changed during recovery.");
                    NativePrototypeMethods.PostMessage(ui,0x10,0,0); await manager.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); manager.Dispose(); manager=null;
                    foreach(var process in new[]{first,second,excluded,late}) { NativePrototypeMethods.PostMessage(process.MainWindowHandle,0x10,0,0); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    fixtures.ForEach(p=>p.Dispose());fixtures.Clear(); scene.Background=new LinearGradientBrush(Color.FromRgb(15,95,190),Color.FromRgb(122,45,165),0);
                    Console.WriteLine("PASS automatic "+(dark?"dark":"light")+": one consent, existing/new apps, exclusion, flush real live glass, native host buttons, multi-window hung recovery, helper-crash rollback");
                }
                code=0;
            }
            catch(Exception error) { Console.WriteLine("FAIL automatic native frames: "+error); }
            finally {
                if(manager is not null) { if(!manager.HasExited)manager.Kill();manager.Dispose(); }
                foreach(var process in fixtures) { if(!process.HasExited)process.Kill();process.Dispose(); }
                scene?.Close();app.Shutdown(code);
            }
        });app.Run();return code;
    }
    private static async Task EnableAutomaticUi(nint window)
    {
        var action=Task.Run(()=> {
            var toggle=AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"NativeFramesEnable"));
            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        });
        nint consent=0; await Until(()=> { consent=NativeMethods.GetWindow(window,6);return consent!=0&&consent!=window; },"One-time automatic styling consent");
        await Task.Run(()=> { var yes=AutomationElement.FromHandle(consent).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"6"));((InvokePattern)yes.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); });
        await action;
    }
    private static nint FindAutomaticGuardian(int ownerPid)
    {
        foreach(var file in Directory.EnumerateFiles(NativeFramesController.Folder,"*.session")) {
            if(File.ReadAllText(file)!=ownerPid.ToString())continue;
            var window=FindWindowExForQa((nint)(-3),0,null,"VeliShell Native Windows Recovery "+Path.GetFileNameWithoutExtension(file)); if(window!=0)return window;
        }return 0;
    }
}
