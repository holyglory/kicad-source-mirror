using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KiCad.Automation.Mcp;

namespace KiCad.Automation.Tests;

/// <summary>Test-only native UI interaction using observed controls and exact
/// process identities. No direct application handlers, policy or permission changes.</summary>
internal sealed class WindowsUiAutomation(WindowsUiObserver observer)
{
    public async Task WaitButtonAsync(WindowsProcessIdentity identity, string title, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        int delay = 50; string? previous = null;
        while (true)
        {
            var observation = await observer.InspectAsync(identity, deadline.Token);
            var buttons = observation.GetProperty("buttons");
            int count = buttons.EnumerateArray().Count(item => Ready(item, title));
            if (count > 1) throw new InvalidDataException("More than one native button matches the explicit UI target.");
            if (count == 1) return;
            string current = buttons.GetRawText(); delay = current == previous ? Math.Min(delay * 2, 500) : 50; previous = current;
            await Task.Delay(delay, deadline.Token);
        }
    }

    public async Task PressAsync(WindowsProcessIdentity identity, string title, CancellationToken token)
    {
        var observation = await observer.InspectAsync(identity, token);
        var candidates = observation.GetProperty("buttons").EnumerateArray().Where(item => Ready(item, title)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Use one visible, enabled native button in the exact editor process.");
        var button = candidates[0];
        using var process = Process.GetProcessById(identity.ProcessId);
        if (WindowsProcessIdentity.Read(process.Id) != identity) throw new InvalidDataException("The native UI process changed before input.");
        token.ThrowIfCancellationRequested();
        nint window = Handle(button.GetProperty("window")), control = Handle(button.GetProperty("control"));
        if (control != 0) WindowsNativeUi.ClickButton(process, window, control, title);
        else
        {
            var rect = button.GetProperty("rectangle");
            WindowsNativeUi.Click(process, window, rect.GetProperty("x").GetInt32() + rect.GetProperty("width").GetInt32() / 2,
                rect.GetProperty("y").GetInt32() + rect.GetProperty("height").GetInt32() / 2);
        }
    }
    private static bool Ready(JsonElement button, string title) => button.GetProperty("title").GetString() == title
        && button.GetProperty("enabled").GetBoolean() && button.GetProperty("visible").GetBoolean();
    private static nint Handle(JsonElement value) => checked((nint)ulong.Parse(value.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture));
}
