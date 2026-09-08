using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyViewportZoom(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, CancellationToken token)
    {
        Task<SchematicObservation> Observe(CancellationToken cancellation) =>
            client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = document }, cancellation);
        var before = await Observe(token);
        var saveBefore = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token);

        async Task<SchematicObservation> AwaitObservation(Func<SchematicObservation, bool> accepted, string phase)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    try
                    {
                        var observed = await Observe(deadline.Token);
                        if (accepted(observed)) return observed;
                    }
                    catch (NativeApiException error) when (error.Status == 4) { }
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try
                {
                    await NativeKeyboard.CaptureAsync(display,
                        Path.Combine(evidence, $"{processId}-viewport-{phase}-timeout.png"), token);
                    NativeKeyboard.HasWindow(display, processId, "Schematic Editor", Console.WriteLine);
                }
                catch (Exception captureError)
                {
                    Console.Error.WriteLine("Viewport timeout evidence unavailable: " + captureError.Message);
                }
                throw;
            }
        }

        // Real F1/F2 keyboard input on the isolated native window, not an API
        // mutation or synthetic viewport. The renderer must publish its new
        // pixel transform without advancing the design revision or dirty state.
        // XSync only acknowledges X-server delivery, not GTK focus handling.
        // Observe real canvas focus after the click before sending the hotkey.
        NativeKeyboard.SchematicShortcut(display, processId, "motion", titleMatch: " — KiCad ",
            controlKey: false, focusCanvas: false);
        var unfocused = await AwaitObservation(o => !o.Preview.Viewport.CanvasHasKeyboardFocus, "focus-away");
        Assert.AreEqual(before.Snapshot, unfocused.Snapshot);
        NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false);
        var focused = await AwaitObservation(o => o.Preview.Viewport.CanvasHasKeyboardFocus, "focus");
        Assert.AreEqual(before.Snapshot, focused.Snapshot);
        before = focused;
        NativeKeyboard.SchematicShortcut(display, processId, "F1", controlKey: false, focusCanvas: false);
        var zoomed = await AwaitObservation(o => o.Preview.Viewport.PixelXDxNm < before.Preview.Viewport.PixelXDxNm, "zoom-in");
        Assert.IsTrue(zoomed.Preview.Viewport.CanvasHasKeyboardFocus);
        Assert.IsTrue(before.Preview.Viewport.PixelXDxNm / zoomed.Preview.Viewport.PixelXDxNm >= 1.3 - 1e-9);
        Assert.AreEqual(before.Snapshot, zoomed.Snapshot);
        Assert.AreEqual(before.Preview.Revision, zoomed.Preview.Revision);
        Assert.AreEqual(before.Preview.WidthPixels, zoomed.Preview.WidthPixels);
        Assert.AreEqual(before.Preview.HeightPixels, zoomed.Preview.HeightPixels);
        CollectionAssert.AreEqual(before.Preview.Viewport.VisibleNativeLayers.ToArray(),
            zoomed.Preview.Viewport.VisibleNativeLayers.ToArray());
        Assert.AreNotEqual(before.Preview.Png, zoomed.Preview.Png);
        await File.WriteAllBytesAsync(Path.Combine(evidence, $"{processId}-viewport-before.png"),
            before.Preview.Png.ToByteArray(), token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, $"{processId}-viewport-zoomed.png"),
            zoomed.Preview.Png.ToByteArray(), token);

        NativeKeyboard.SchematicShortcut(display, processId, "F2", controlKey: false, focusCanvas: false);
        var zoomedOut = await AwaitObservation(o => o.Preview.Viewport.PixelXDxNm > zoomed.Preview.Viewport.PixelXDxNm, "zoom-out");
        // Native zoom selects the next preset at least 1.3x away. It is not
        // an exact inverse of an arbitrary initial fit-to-page scale.
        Assert.IsTrue(zoomedOut.Preview.Viewport.PixelXDxNm / zoomed.Preview.Viewport.PixelXDxNm >= 1.3 - 1e-9);
        Assert.AreNotEqual(zoomed.Preview.Png, zoomedOut.Preview.Png);
        Assert.AreEqual(before.Snapshot, zoomedOut.Snapshot);
        Assert.AreEqual(saveBefore, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
            new() { Document = document }, token));
    }
}
