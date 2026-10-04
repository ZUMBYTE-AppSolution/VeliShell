using System.Windows;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

public sealed class AppearancePreview : FrameworkElement
{
    public string Mode { get; set; } = "Light";
    public AppearancePreview() { Width = 148; Height = 94; }
    private static SolidColorBrush B(string value) => new((Color)ColorConverter.ConvertFromString(value));
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        d.PushClip(new RectangleGeometry(new Rect(0, 0, 148, 94), 7, 7));
        var dark = Mode == "Dark";
        d.DrawRectangle(new LinearGradientBrush((Color)ColorConverter.ConvertFromString(dark ? "#172959" : "#9CCEFF"),
            (Color)ColorConverter.ConvertFromString(dark ? "#8B416D" : "#8B92E9"), 40), null, new Rect(0, 0, 148, 94));
        d.DrawGeometry(B(dark ? "#704CA1" : "#C5BEFC"), null, Geometry.Parse("M 0,75 C 48,1 109,17 148,73 L 148,94 L 0,94 Z"));
        d.DrawRoundedRectangle(B(dark ? "#303038" : "#F9F9FC"), null, new Rect(28, 16, 99, 55), 5, 5);
        d.DrawRectangle(B(dark ? "#41414B" : "#E3E4EB"), null, new Rect(28, 26, 29, 40));
        if (Mode == "System")
        {
            d.PushClip(new RectangleGeometry(new Rect(82, 0, 66, 94)));
            d.DrawRoundedRectangle(B("#303038"), null, new Rect(28,16,99,55),5,5);
            d.Pop();
        }
        foreach (var dot in new[] { (35d, "#FF5F57"), (41d, "#FEBC2E"), (47d, "#28C840") })
            d.DrawEllipse(B(dot.Item2), null, new Point(dot.Item1,22), 1.7,1.7);
        d.DrawRoundedRectangle(B("#1686FF"), null, new Rect(33,36,20,5),2,2);
        d.DrawRoundedRectangle(B(dark ? "#626272" : "#C4C9D7"), null, new Rect(66,34,42,3),1.5,1.5);
        d.DrawRoundedRectangle(B(dark ? "#525262" : "#D8DBE5"), null, new Rect(66,42,50,3),1.5,1.5);
        d.DrawRoundedRectangle(B("#BFEBEDF6"), null, new Rect(42,79,65,11),4,4);
        var iconColors = new[] { "#269AF4", "#F4D35B", "#7B7CEA", "#79BB80", "#8DA1B8" };
        for (var i=0;i<iconColors.Length;i++) d.DrawRoundedRectangle(B(iconColors[i]),null,new Rect(48+i*11,81,7,7),2,2);
        d.Pop();
    }
}
