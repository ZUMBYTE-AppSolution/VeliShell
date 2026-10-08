using VeliShell.Core;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Models;

internal sealed class DockItem
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Target { get; init; } = "";
    public string IconId { get; init; } = "app";
    public IconReference? Icon { get; init; }
    public string? Attribution { get; init; }
    public Pin? Pin { get; init; }
    public List<NativeWindow> Windows { get; set; } = [];
    public List<DockItem> Overflow { get; init; } = [];
    public bool IsUtility => Key is "start" or "velishell" or "trash" or "overflow";
}
