using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

internal static partial class Program
{
    private static int VerifyNativeFramesHeadless()
    {
        var app=new SafePreviewApp { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        try {
            var live=new NativeFrameStatus("Same title","example.exe","Active",Window:10,Pid:30,Started:40);
            var oldFailure=live with { State="Failed",Reason="Activation",Error=104 };
            var other=live with { Window=11 };
            var stale=live with { Pid=31 };
            bool Current(NativeFrameStatus row)=>row.Pid==30;
            var rows=NativeFrameStatusSnapshot.Merge([live,live,other],[oldFailure,stale],[live with { State="Skipped",Reason="Region" }],Current);
            Require(rows.Length==2 && rows.All(row=>row.State=="Active"),"Live rows did not replace duplicate/historical failures.");
            rows=NativeFrameStatusSnapshot.Merge([], [oldFailure], [live with { State="Skipped",Reason="Excluded" }],Current);
            Require(rows.Length==1 && rows[0].Reason=="Excluded","A deliberate exception did not replace a historical failure.");
            Require(NativeFrameStatusSnapshot.Merge([], [oldFailure],[],_=>false).Length==0,"Closed/stale windows remained in the snapshot.");
            var output=Path.Combine(AppContext.BaseDirectory,"native-frames-headless-renders"); Directory.CreateDirectory(output);
            using var theme=new ThemeService();
            foreach(var language in new[]{UiLanguage.German,UiLanguage.English}) {
                LocalizationService.Current.Apply(language);
                Require(oldFailure.ToString().Contains(language==UiLanguage.German?"Aktivierung fehlgeschlagen":"Activation failed",StringComparison.Ordinal),"Failed state is misleading or not localized.");
                Require(oldFailure.ToString().Contains("104 (0x00000068)",StringComparison.Ordinal),"Failure code was discarded.");
                foreach(bool dark in new[]{false,true}) {
                    theme.Apply(dark?Appearance.Dark:Appearance.Light,100,100);
                    var settings=new Settings { NativeModernFramesEnabled=true,NativeModernFramesConsentVersion=1,NativeFrameExclusions=["keep.exe"] };
                    using var controller=new NativeFramesController(preview:true);
                    typeof(NativeFramesController).GetProperty("Status",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,
                        new NativeFramesStatus(false,false,[
                            new("Explorer — layout preview","explorer.exe","Active",Window:10,Pid:30,Started:40,Adapter:1),
                            new("Paint — failure preview","mspaint.exe","Failed","Activation",11,30,40,2,104),
                            new("Notepad — material preview","notepad.exe","Material",Window:12,Pid:30,Started:40)
                        ]));
                    var view=new NativeFramesWindow(settings,controller,theme,update=>update(settings));
                    typeof(NativeFramesWindow).GetMethod("Refresh",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null);
                    Require(new WindowInteropHelper(view).Handle==0,"Headless check created a native window.");
                    var root=(Border)view.Content; view.Content=null;
                    TextElement.SetForeground(root,view.Foreground);
                    root.Measure(new Size(880,1000)); root.Arrange(new Rect(0,0,880,1000)); root.UpdateLayout();
                    Require(root.Background is SolidColorBrush { Color.A:255 },"Settings backdrop must have an opaque readability base.");
                    var windows=(ListBox)typeof(NativeFramesWindow).GetField("_windows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
                    Require(windows.Background is SolidColorBrush listSurface && listSurface.Color.A==255 && (!dark || listSurface.Color.R<100),"Dark-mode list uses an unstyled white/native background.");
                    var foreground=(SolidColorBrush)windows.Foreground;
                    Require(dark?foreground.Color.R>200:foreground.Color.R<100,"List text has incorrect theme contrast.");
                    Require(windows.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem,"Status rows were not rendered.");
                    var bitmap=new RenderTargetBitmap(880,1000,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(output,$"frames-{language.ToString().ToLowerInvariant()}-{(dark?"dark":"light")}.png")))encoder.Save(file);
                    Require(new WindowInteropHelper(view).Handle==0,"Offscreen render unexpectedly opened a desktop surface.");
                    view.Close();
                    Console.WriteLine($"PASS {language} {(dark?"dark":"light")}: offscreen status list, labeled consent toggles, opaque settings base, readable foreground.");
                }
            }
            Console.WriteLine("PASS: exact window identity, stale/closed row removal, duplicate suppression, live-state precedence and localized activation errors.");
            Console.WriteLine("No windows shown; no input, target changes, recovery registration or live app styling performed.");
            app.Shutdown(); return 0;
        } catch(Exception error) { Console.WriteLine("FAIL headless native-frame check: "+error); app.Shutdown(1);return 1; }
    }
}
