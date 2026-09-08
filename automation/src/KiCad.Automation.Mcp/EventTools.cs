using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record NativeEventWaitResult(string Status, JsonElement? Notification, bool TrackingComplete,
    string? ErrorCode = null, string? ErrorMessage = null);

[McpServerToolType]
public sealed class EventTools(InstanceRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "kicad_events_wait", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(NativeEventWaitResult)),
     Description("Wait for committed schematic-change notifications from one explicit live instance without polling designs. InitialStateRequired or RecoveryRequired requires a fresh native snapshot; never infer missing edits. Resume with the returned eventEpoch and sequence. Native edit coverage is incomplete, and this is not automatic XML synchronization. Deadline means no tracked notification arrived within the wait; cancellation does not close KiCad.")]
    public async Task<CallToolResult> Wait(string instanceId, CancellationToken cancellationToken,
        string? eventEpoch = null, ulong? afterSequence = null, int timeoutSeconds = 15)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result(await WaitCore(instanceId, cancellationToken, eventEpoch, afterSequence, timeoutSeconds));
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException)
        {
            string code = error switch
            {
                AutomationException automation => automation.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_event"
            };
            return Result(new("Failed", null, false, code, error.Message));
        }
    }

    private static CallToolResult Result(NativeEventWaitResult result)
    {
        var structured = JsonSerializer.SerializeToElement(result, Json);
        return new()
        {
            IsError = result.ErrorCode is not null,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private async Task<NativeEventWaitResult> WaitCore(string instanceId, CancellationToken cancellationToken,
        string? eventEpoch = null, ulong? afterSequence = null, int timeoutSeconds = 15)
    {
        if (timeoutSeconds is < 1 or > 60)
            throw new AutomationException("invalid_deadline", "Choose an event wait between 1 and 60 seconds.");
        if (afterSequence is not null && string.IsNullOrEmpty(eventEpoch))
            throw new AutomationException("missing_event_epoch", "A resumed cursor requires its event epoch.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var session = await registry.Client(instanceId).HandshakeAsync(deadline.Token);
            if (eventEpoch is not null && eventEpoch != session.EventEpoch)
                throw new AutomationException("event_stream_changed", "Rediscover the native event stream and recover state before resuming.");
            using var subscription = new NativeEventSubscription(session, afterSequence);
            while (true)
            {
                var delivery = await subscription.ReceiveAsync(deadline.Token);
                if (delivery.Disposition is NativeEventDisposition.Heartbeat or NativeEventDisposition.Duplicate) continue;
                using var json = JsonDocument.Parse(JsonFormatter.Default.Format(delivery.Event));
                return new(delivery.Disposition.ToString(), json.RootElement.Clone(), false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("Deadline", null, false);
        }
    }
}
