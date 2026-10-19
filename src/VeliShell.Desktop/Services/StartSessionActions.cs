namespace VeliShell.Desktop.Services;

internal sealed record StartSessionCommand(string Id, string LabelKey, string? Arguments);

internal static class StartSessionActions
{
    internal static IReadOnlyList<StartSessionCommand> Commands { get; } = Array.AsReadOnly(new[]
    {
        new StartSessionCommand("lock", "MenuBar.Lock", null),
        new StartSessionCommand("signout", "MenuBar.SignOut", "/l"),
        new StartSessionCommand("restart", "MenuBar.Restart", "/r /t 0"),
        new StartSessionCommand("shutdown", "MenuBar.Shutdown", "/s /t 0")
    });

    // Injected boundaries allow verification without logging out or shutting down
    // the machine. Production uses the existing shell-restoration-aware app path.
    internal static async Task ExecuteAsync(string id, Func<StartSessionCommand, bool> confirm,
        Func<string, Task> session, Action lockScreen)
    {
        var command = Commands.FirstOrDefault(value => value.Id == id);
        if (command is null) return;
        if (command.Arguments is null) { lockScreen(); return; }
        if (confirm(command)) await session(command.Arguments);
    }
}
