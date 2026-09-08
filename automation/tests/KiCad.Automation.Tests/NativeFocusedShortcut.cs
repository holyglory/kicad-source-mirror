using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task FocusedSchematicShortcut(NativeClient client, DocumentSpecifier document,
        int processId, string display, string key, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        bool clicked = false;
        while (true)
        {
            try
            {
                var observation = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                    new() { Document = document }, deadline.Token);
                if (observation.Preview.Viewport.CanvasHasKeyboardFocus)
                {
                    // XSync acknowledges delivery, not GTK focus. Do not turn
                    // consecutive shortcuts into background double-clicks.
                    NativeKeyboard.SchematicShortcut(display, processId, key, focusCanvas: false);
                    return;
                }
                if (!clicked)
                {
                    NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false);
                    clicked = true;
                }
            }
            catch (NativeApiException error) when (error.Status is 4 or 7) { }
            await Task.Delay(100, deadline.Token);
        }
    }
}
