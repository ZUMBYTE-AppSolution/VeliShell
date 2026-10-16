using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

internal static partial class Program
{
    private static int VerifyClassicFrame()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            Process? fixture = null, manager = null; Window? scene = null;
            try
            {
                var renders = Path.Combine(AppContext.BaseDirectory, "classic-renders"); Directory.CreateDirectory(renders);
                scene = new Window { WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = 80, Top = 100, Width = 1080, Height = 730,
                    Background = new LinearGradientBrush(Color.FromRgb(23,86,142), Color.FromRgb(112,62,130), 25) };
                scene.Show();
                foreach (bool dark in new[] { false, true })
                {
                    fixture = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VeliShell.ClassicSample.exe")) { UseShellExecute = false })!;
                    await Until(() => { fixture.Refresh(); return fixture.MainWindowHandle != 0; }, "Fixture window");
                    var hwnd = fixture.MainWindowHandle;
                    NativeMethods.GetWindowRect(hwnd, out var originalRect); NativePrototypeMethods.GetClientRect(hwnd, out var originalClient);
                    var originalOrigin = new NativeMethods.Point(); NativePrototypeMethods.ClientToScreen(hwnd, ref originalOrigin);
                    var style = NativeMethods.GetWindowLongPtr(hwnd, -16);
                    var originalEdit = FindWindowExForQa(hwnd,0,"EDIT",null);
                    var candidate = ClassicFrameSession.Inspect(hwnd);
                    Require(candidate is not null, "Standalone classic app was not admitted.");
                    Require(NativePrototypeMethods.VeliClassicBegin(hwnd,Guid.NewGuid().ToString("N"),(uint)fixture.Id,0,0,50,50,0)==0,"Mismatched process creation time must be rejected.");
                    GetDwmForClassicQa(hwnd,1,out int originalNc,sizeof(int));
                    Require(NativePrototypeMethods.VeliClassicEligible(new WindowInteropHelper(scene).Handle) == 0, "Custom WPF window must be rejected.");
                    manager = NativePrototypeSession.Start([ClassicFrameSession.Manager, dark ? "dark" : "light", "80", "70"]);
                    await Until(() => { manager.Refresh(); return manager.MainWindowHandle != 0; }, "Manager window");
                    nint managerWindow = manager.MainWindowHandle;
                    await Task.Run(async () =>
                    {
                        var root = AutomationElement.FromHandle(managerWindow);
                        AutomationElement? item = null;
                        for (int n=0;n<50 && item is null;n++)
                        {
                            item = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.Contains("Independent Classic Fixture", StringComparison.Ordinal));
                            if(item is null) await Task.Delay(100);
                        }
                        Require(item is not null, "Fixture not listed by opt-in UI.");
                        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                        InvokeNamed(root, LocalizationService.Current.Get("Classic.Apply"));
                    });
                    nint consent = 0;
                    await Until(() => { consent = NativeMethods.GetWindow(managerWindow, 6); return consent != 0; }, "Explicit consent dialog");
                    await Task.Run(() =>
                    {
                        var yes = AutomationElement.FromHandle(consent).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                        Require(yes is not null, "Explicit consent Yes button missing.");
                        ((InvokePattern)yes!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    });
                    await Until(() => NativePrototypeMethods.GetProp(hwnd, "VeliShell.ClassicFrame.Glass") == 1, "Targeted DLL glass attach");
                    Require(NativeMethods.GetWindowLongPtr(hwnd,-16)==style, "App style must remain unchanged.");
                    NativePrototypeMethods.GetClientRect(hwnd, out var client); var origin = new NativeMethods.Point(); NativePrototypeMethods.ClientToScreen(hwnd, ref origin);
                    Require(client.Right==originalClient.Right && client.Bottom==originalClient.Bottom && origin.X==originalOrigin.X && origin.Y==originalOrigin.Y, "App content/menu geometry changed.");
                    Require(GetMenuForQa(hwnd)!=0, "Native app menu disappeared.");
                    NativeMethods.SetWindowPos(hwnd,(nint)(-1),120,160,800,480,0x0040); NativeMethods.SetForegroundWindow(hwnd); await Task.Delay(500);
                    NativeMethods.GetWindowRect(hwnd,out var bounds);
                    using(var image = new System.Drawing.Bitmap(800,480)) using(var g = System.Drawing.Graphics.FromImage(image))
                    { g.CopyFromScreen(bounds.Left,bounds.Top,0,0,new System.Drawing.Size(800,480)); image.Save(Path.Combine(renders,dark?"classic-dark.png":"classic-light.png")); }
                    static async Task Click(nint w,int button)
                    {
                        NativeMethods.GetWindowRect(w,out var r); int x=r.Left+23+button*22, y=r.Top+16;
                        nint point=(nint)((y<<16)|(x&65535)); NativePrototypeMethods.PostMessage(w,0xA1,2,point); NativePrototypeMethods.PostMessage(w,0xA2,2,point); await Task.Delay(300);
                    }
                    await Click(hwnd,1); await Until(()=>NativeMethods.IsIconic(hwnd),"Native left minimize button"); NativeMethods.ShowWindowAsync(hwnd,9); await Task.Delay(300);
                    await Click(hwnd,2); await Until(()=>NativeMethods.IsZoomed(hwnd),"Native left maximize button"); await Click(hwnd,2); await Until(()=>!NativeMethods.IsZoomed(hwnd),"Native left restore button");
                    await Task.Run(()=>InvokeNamed(AutomationElement.FromHandle(managerWindow),LocalizationService.Current.Get("Prototype.Restore")));
                    await Until(()=>NativePrototypeMethods.GetProp(hwnd,NativePrototypeMethods.ClassicActive)==0,"Normal rollback");
                    NativeMethods.SetWindowPos(hwnd,(nint)(-2),120,160,800,480,0x0040); await Task.Delay(500);
                    await ApproveAgain(managerWindow,hwnd);
                    var guardianWindow = FindClassicGuardian(fixture.Id);
                    Require(guardianWindow!=0,"Independent classic guardian not found.");
                    NativePrototypeMethods.PostMessage(hwnd,0x111,202,0); await Task.Delay(500);
                    NativePrototypeMethods.PostMessage(guardianWindow,0x312,0x5650,0); await Task.Delay(800);
                    Require(NativePrototypeMethods.GetProp(hwnd,NativePrototypeMethods.ClassicActive)!=0,"Hung-window rollback must remain pending, not claim success.");
                    await Until(()=>NativePrototypeMethods.GetProp(hwnd,NativePrototypeMethods.ClassicActive)==0,"Independent hotkey rollback after hung UI resumes");
                    await Task.Delay(500); await ApproveAgain(managerWindow,hwnd);
                    guardianWindow=FindClassicGuardian(fixture.Id); Require(guardianWindow!=0,"Heartbeat guardian not found.");
                    NativeMethods.GetWindowThreadProcessId(guardianWindow,out var guardPid);
                    using(var ownGuard=Process.GetProcessById((int)guardPid)) {
                        Require(string.Equals(ownGuard.MainModule?.FileName,manager.MainModule?.FileName,StringComparison.OrdinalIgnoreCase),"Unexpected recovery process identity.");
                        ownGuard.Kill(); await ownGuard.WaitForExitAsync();
                    }
                    await Until(()=>NativePrototypeMethods.GetProp(hwnd,NativePrototypeMethods.ClassicActive)==0,"Target-side heartbeat rollback after guardian crash");
                    await Task.Delay(500); await ApproveAgain(managerWindow,hwnd);
                    // Kill only our own disposable controller to prove the independent guardian path.
                    manager.Kill(); await manager.WaitForExitAsync(); manager.Dispose(); manager=null;
                    await Until(()=>NativePrototypeMethods.GetProp(hwnd,NativePrototypeMethods.ClassicActive)==0,"Rollback after controller exit");
                    Require(!fixture.HasExited && NativeMethods.GetWindowLongPtr(hwnd,-16)==style,"Rollback closed or changed the original app.");
                    GetDwmForClassicQa(hwnd,1,out int restoredNc,sizeof(int)); Require(restoredNc==originalNc,"Effective DWM non-client state was not restored.");
                    var content = new System.Text.StringBuilder(1024); GetTextForClassicQa(originalEdit,0xD,1024,content);
                    Require(NativeMethods.IsWindow(originalEdit) && content.ToString().StartsWith("Original editable",StringComparison.Ordinal),"Original app content lost: "+content);
                    NativePrototypeMethods.PostMessage(hwnd,0x10,0,0); await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); fixture.Dispose(); fixture=null;
                    await Task.Delay(500); Console.WriteLine("PASS classic "+(dark?"dark":"light")+": scoped DLL, original geometry/menu, left buttons, normal rollback, independent hotkey with hung target, controller AND guardian crash rollback, content/DWM state preserved");
                }
                result=0;
            }
            catch(Exception error) { Console.WriteLine("FAIL classic: "+error); }
            finally
            {
                if(manager is not null) { if(!manager.HasExited)manager.Kill(); manager.Dispose(); }
                if(fixture is not null) { if(!fixture.HasExited)fixture.Kill(); fixture.Dispose(); }
                scene?.Close(); app.Shutdown(result);
            }
        }); app.Run(); return result;
    }
    private static async Task Until(Func<bool> check,string what)
    { for(int n=0;n<120;n++) { if(check())return; await Task.Delay(100); } throw new InvalidOperationException("Timed out: "+what); }
    private static void InvokeNamed(AutomationElement root,string name)
    {
        var button=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,name));
        Require(button is not null,"Missing button: "+name); ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static async Task ApproveAgain(nint window,nint target)
    {
        await Task.Run(()=>InvokeNamed(AutomationElement.FromHandle(window),LocalizationService.Current.Get("Classic.Apply")));
        nint consent=0;
        await Until(()=> { consent=NativeMethods.GetWindow(window,6); return consent!=0 && consent!=window; },"Second explicit consent");
        await Task.Run(()=> {
            var yes=AutomationElement.FromHandle(consent).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"6"));
            Require(yes is not null,"Explicit consent missing."); ((InvokePattern)yes!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        });
        await Until(()=>NativePrototypeMethods.GetProp(target,NativePrototypeMethods.ClassicActive)!=0,"Reactivation with explicit consent");
        await Task.Delay(400);
    }
    private static nint FindClassicGuardian(int fixturePid)
    {
        foreach(var file in Directory.EnumerateFiles(ClassicFrameSession.Folder,"*.session")) {
            if(File.ReadLines(file).FirstOrDefault()!=fixturePid.ToString())continue;
            var handle=FindWindowExForQa((nint)(-3),0,null,"VeliShell Classic Recovery "+Path.GetFileNameWithoutExtension(file));
            if(handle!=0)return handle;
        }
        return 0;
    }
    [DllImport("user32.dll",EntryPoint="GetMenu")] private static extern nint GetMenuForQa(nint window);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] private static extern int GetDwmForClassicQa(nint window,int attribute,out int value,int size);
    [DllImport("user32.dll",EntryPoint="SendMessageW",CharSet=CharSet.Unicode)] private static extern nint GetTextForClassicQa(nint window,int message,nint wp,System.Text.StringBuilder text);
}
