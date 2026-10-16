using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using Microsoft.Win32;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

// A user-operated, exact-window VM check. Never launches or closes other apps,
// sends input, switches desktops, or grants the normal automatic module consent.
internal sealed class NativeFramesVmCheckWindow : VeliShellWindow
{
    private readonly ThemeService _theme;
    private NativeFramesController? _controller;
    private ClassicFrameSession.Candidate? _target;
    private Geometry? _before;
    private bool _busy,_wasActive;
    private Settings? _settings;
    private readonly ListBox _targets=new() { Height=160 };
    private readonly TextBlock _status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12) };
    private readonly CheckBox _visual=new(),_tabs=new(),_buttons=new();
    private readonly Button _start=new(),_rollback=new(),_recovery=new(),_crash=new();
    private readonly List<object> _events=[];
    private string _run="";
    private readonly string _report;
    private static string L(string key)=>LocalizationService.Current.Get("VmCheck."+key);
    private sealed record Geometry(int Width,int Height,int ClientWidth,int ClientHeight,int InsetX,int InsetY,int Dpi,bool Maximized,int[] Dwm);
    internal static bool IsVirtualMachine()
    {
        try {
            using var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string value=(key?.GetValue("SystemManufacturer")+" "+key?.GetValue("SystemProductName")).ToLowerInvariant();
            return new[]{"vmware","virtualbox","virtual machine","qemu","kvm","parallels"}.Any(value.Contains);
        } catch(System.Security.SecurityException) { return false; }
    }
    internal NativeFramesVmCheckWindow(ThemeService theme)
    {
        _theme=theme; Title=L("Title"); Width=860; Height=810; MinWidth=700; MinHeight=590;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _report=Path.Combine(App.DataDirectory,"native-vm-checks",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".json");
        var layout=new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(50) });layout.RowDefinitions.Add(new RowDefinition());
        layout.SetResourceReference(Panel.BackgroundProperty,"WindowBackdropGradient");
        var header=new Grid(); var lights=new TrafficLights { HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,0,0) };
        WindowChrome.SetIsHitTestVisibleInChrome(lights,true);header.Children.Add(lights);
        header.Children.Add(new TextBlock { Text=Title,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold });layout.Children.Add(header);
        var body=new StackPanel { Margin=new Thickness(26,12,26,20) };
        body.Children.Add(new TextBlock { Text=L("Heading"),FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10) });
        body.Children.Add(Paragraph("Hint"));
        _targets.SetResourceReference(StyleProperty,"NativeFramesList");body.Children.Add(_targets);
        _targets.SelectionChanged+=(_,_)=>UpdateButtons();
        var actions=new WrapPanel { Margin=new Thickness(0,10,0,0) };
        Add(actions,L("Refresh"),()=>RefreshTargets());
        _start.Content=L("Start"); _start.Click+=async(_,_)=>await StartAsync();actions.Children.Add(_start);body.Children.Add(actions);body.Children.Add(_status);
        var recovery=new WrapPanel();
        _rollback.Content=L("Rollback");_rollback.Click+=async(_,_)=>await RestoreAsync("normal");
        _recovery.Content=L("Recovery");_recovery.Click+=async(_,_)=>await RestoreAsync("independent-signal");
        _crash.Content=L("Crash");_crash.Click+=async(_,_)=>await RestoreAsync("helper-crash");
        foreach(var button in new[]{_rollback,_recovery,_crash}) { button.Margin=new Thickness(0,0,8,8);recovery.Children.Add(button); }
        body.Children.Add(recovery);body.Children.Add(Paragraph("RecoveryHint"));
        foreach(var (box,key) in new[]{(_visual,"Visual"),(_tabs,"Tabs"),(_buttons,"Buttons")}) {
            box.Content=L(key);box.Margin=new Thickness(0,4,0,7);box.Checked+=(_,_)=>SaveReport();box.Unchecked+=(_,_)=>SaveReport();body.Children.Add(box);
        }
        body.Children.Add(Paragraph("NoAutomaticVisualPass"));
        var export=new WrapPanel();Add(export,L("Save"),()=> { SaveReport();_status.Text=L("Saved")+"\n"+_report; });
        Add(export,L("Theme"),()=>_theme.Apply(_theme.IsDark?Appearance.Light:Appearance.Dark,80,65));body.Children.Add(export);
        var scroll=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };Grid.SetRow(scroll,1);layout.Children.Add(scroll);
        var plate=new Border { Child=layout,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1) };plate.SetResourceReference(Border.BackgroundProperty,"WindowSurface");plate.SetResourceReference(Border.BorderBrushProperty,"GlassPanelStroke");RoundedClip.SetRadius(layout,13);Content=plate;
        Loaded+=(_,_)=> { RefreshTargets(); UpdateButtons(); };
        _theme.Changed+=ThemeChanged;
        Closed+=(_,_)=> { _theme.Changed-=ThemeChanged;_controller?.Dispose();Record("checker-closed",new { RecoveryPending=_target is not null && NativePrototypeMethods.GetProp(_target.Window,NativePrototypeMethods.ClassicActive)!=0 }); };
    }
    private static TextBlock Paragraph(string key) {
        var text=new TextBlock { Text=L(key),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10),FontSize=12 };
        text.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondary");return text;
    }
    private static void Add(Panel panel,string text,Action action) {
        var button=new Button { Content=text,Margin=new Thickness(0,0,8,8) };button.Click+=(_,_)=>action();panel.Children.Add(button);
    }
    private void RefreshTargets()
    {
        if(_busy || _controller is { Active:true } || _controller is { Restoring:true })return;
        var windows=new List<nint>();NativeMethods.EnumWindows((window,_)=> { windows.Add(window);return true; },0);
        _targets.ItemsSource=windows.Take(512).Select(window=>ClassicFrameSession.Inspect(window,includeHash:false,crossBitness:true,modern:true)).OfType<ClassicFrameSession.Candidate>().ToArray();
    }
    private void UpdateButtons()
    {
        bool active=_controller is { Active:true };
        _start.IsEnabled=!_busy && !active && _controller is not { Restoring:true } && _targets.SelectedItem is not null && IsVirtualMachine();
        _targets.IsEnabled=!_busy && !active;
        foreach(var button in new[]{_rollback,_recovery,_crash})button.IsEnabled=!_busy && active;
    }
    private async Task StartAsync()
    {
        if(_busy || !IsVirtualMachine() || _targets.SelectedItem is not ClassicFrameSession.Candidate selected)return;
        if(MessageBox.Show(this,L("Consent"),Title,MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _busy=true;UpdateButtons();
        try {
            var current=ClassicFrameSession.Inspect(selected.Window,crossBitness:true,modern:true);
            if(current is null || current.Pid!=selected.Pid || current.Started!=selected.Started)throw new InvalidOperationException("TargetChanged");
            if(ClassicFrameSession.HasOtherAdapterVersion(current)) { _status.Text=LocalizationService.Current.Get("NativeFrames.Reason.RestartApp");return; }
            if(_controller is not null)_controller.Changed-=Changed;
            _controller?.Dispose(); _target=current; _before=ReadGeometry(current.Window); _run=Guid.NewGuid().ToString("N");_wasActive=false;
            _visual.IsChecked=false;_tabs.IsChecked=false;_buttons.IsChecked=false;
            Record("baseline",new { current.Adapter,Executable=Path.GetFileName(current.Path),Version=System.Diagnostics.FileVersionInfo.GetVersionInfo(current.Path).FileVersion,Geometry=_before });
            _settings=new Settings { NativeFramesEnabled=true,NativeFramesConsentVersion=1,NativeModernFramesEnabled=true,NativeModernFramesConsentVersion=1,GlassGloss=80,GlassTransparency=65 };
            _controller=new NativeFramesController(preview:true,targets:[new(current.Window.ToInt64(),current.Pid,current.Started)]);
            _controller.Changed+=Changed;
            await _controller.EnableAsync(_settings,_theme.IsDark,explicitConsent:true);
            _status.Text=L("Preparing");
        } catch(Exception error) { Record("start-error",new { Type=error.GetType().Name,error.HResult });_status.Text=L("Failed")+" · "+error.GetType().Name;_controller?.Stop(); }
        finally { _busy=false;UpdateButtons(); }
    }
    private void ThemeChanged() { if(_settings is not null)_controller?.Update(_settings,_theme.IsDark); }
    private void Changed()
    {
        if(_controller is null)return;
        var rows=_controller.Status.Windows;
        if(!_wasActive && _target is not null && rows.Any(row=>row.State=="Active") && NativePrototypeMethods.GetProp(_target.Window,"VeliShell.ClassicFrame.Glass")==1) {
            _wasActive=true;Record("activation-confirmed",new { Adapter=_target.Adapter,Geometry=ReadGeometry(_target.Window) });
        }
        if(!SameTarget() || rows.Any(row=>row.State=="Failed")) { _visual.IsChecked=false;_tabs.IsChecked=false;_buttons.IsChecked=false; }
        if(rows.Length==0 && !_busy && !_controller.Active && !_controller.Restoring) { UpdateButtons();return; }
        _status.Text=rows.Length>0?string.Join("\n",rows.Select(row=>row.ToString())):L("Preparing");
        if(rows.Any(row=>row.State=="Active"))_status.Text+="\n"+L("VisualPending");
        UpdateButtons();
    }
    private async Task RestoreAsync(string kind)
    {
        if(_busy || _controller is null || _target is null || _before is null)return;
        if(kind=="helper-crash" && MessageBox.Show(this,L("CrashConsent"),Title,MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _busy=true;UpdateButtons();
        try {
            var controller=_controller;
            Record(kind+"-requested",new { State=controller.Status.Windows.Select(row=>new { row.State,row.Reason,row.Error,row.Adapter }).ToArray(),Geometry=ReadGeometry(_target.Window) });
            if(kind=="helper-crash")controller.TestIsolatedHelperFailure();
            else if(kind=="independent-signal") { if(!controller.RequestIsolatedRecovery())throw new InvalidOperationException("RecoverySignalUnavailable"); }
            else controller.Stop();
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(DateTime.UtcNow<deadline && (NativePrototypeMethods.GetProp(_target.Window,NativePrototypeMethods.ClassicActive)!=0 || controller.Active || controller.Restoring))await Task.Delay(200);
            bool exists=SameTarget();
            bool nativeRestored=exists && NativePrototypeMethods.GetProp(_target.Window,NativePrototypeMethods.ClassicActive)==0 && NativePrototypeMethods.GetProp(_target.Window,"VeliShell.ClassicFrame.Caption")==0;
            var after=exists?ReadGeometry(_target.Window):null;
            bool sameSize=after is not null && after.Width==_before.Width && after.Height==_before.Height && after.Dpi==_before.Dpi && after.Maximized==_before.Maximized;
            bool? geometryMatches=sameSize?after!.ClientWidth==_before.ClientWidth && after.ClientHeight==_before.ClientHeight && after.InsetX==_before.InsetX && after.InsetY==_before.InsetY:null;
            bool? dwmMatches=after is not null?after.Dwm.SequenceEqual(_before.Dwm):null;
            bool passed=_wasActive && nativeRestored && geometryMatches==true && dwmMatches==true && !controller.Active && !controller.Restoring;
            Record(kind+"-result",new { RollbackComparisonPassed=passed,ActivationWasConfirmed=_wasActive,TargetStillExists=exists,NativeRestored=nativeRestored,GeometryMatches=geometryMatches,DwmMatches=dwmMatches,Geometry=after,
                UserObservations=new { GlassAndLeftButtons=_visual.IsChecked==true,TabsAndMenusUsable=_tabs.IsChecked==true,WindowButtonsUsable=_buttons.IsChecked==true } });
            _status.Text=L(passed?"Restored":"PendingOrMismatch");
        } catch(Exception error) { Record(kind+"-error",new { Type=error.GetType().Name,error.HResult });_status.Text=L("Failed")+" · "+error.GetType().Name; }
        finally { _busy=false;UpdateButtons();SaveReport(); }
    }
    private bool SameTarget()
    {
        if(_target is null || !NativeMethods.IsWindow(_target.Window))return false;
        NativeMethods.GetWindowThreadProcessId(_target.Window,out var pid);
        try { using var process=System.Diagnostics.Process.GetProcessById((int)pid);return pid==_target.Pid && process.StartTime.ToUniversalTime().ToFileTimeUtc()==_target.Started; }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static Geometry ReadGeometry(nint window)
    {
        NativeMethods.GetWindowRect(window,out var bounds);NativePrototypeMethods.GetClientRect(window,out var client);
        var origin=new NativeMethods.Point();NativePrototypeMethods.ClientToScreen(window,ref origin);
        int[] attributes=[1,20,33];var values=attributes.Select(attribute=>NativeMethods.DwmGetWindowAttribute(window,attribute,out var value,4)>=0?value:int.MinValue).ToArray();
        return new(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top,client.Right,client.Bottom,origin.X-bounds.Left,origin.Y-bounds.Top,(int)NativePrototypeMethods.GetDpiForWindow(window),NativeMethods.IsZoomed(window),values);
    }
    private void Record(string stage,object detail) { if(_events.Count<128)_events.Add(new { AtUtc=DateTime.UtcNow,Run=_run,Stage=stage,Detail=detail });SaveReport(); }
    private void SaveReport()
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_report)!);
            var temporary=_report+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new {
                Version=GitHubReleaseUpdateService.InstalledVersion.ToString(),Windows=Environment.OSVersion.Version.ToString(),VirtualMachine=IsVirtualMachine(),
                UserConfirmed=new { GlassAndLeftButtons=_visual.IsChecked==true,TabsAndMenusUsable=_tabs.IsChecked==true,WindowButtonsUsable=_buttons.IsChecked==true },Events=_events,
                Note="User-operated real-app check. Visual checkboxes are user observations, not automatically certified results. No screenshot, document title, full target path, credentials or settings are exported. No real global emergency-hotkey test is claimed."
            },new JsonSerializerOptions { WriteIndented=true }));
            File.Move(temporary,_report,true);
        } catch(Exception error) when(error is IOException or UnauthorizedAccessException) { _status.Text=L("Failed")+" · "+error.GetType().Name; }
    }
}
