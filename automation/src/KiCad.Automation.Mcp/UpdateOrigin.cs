using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>Identity of the still-running automation session captured by its
/// native Update action. Not a substitute for kernel or payload verification.</summary>
public sealed record UpdateOrigin(string Endpoint, string Epoch)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Epoch) || Epoch.Length > 256 || Epoch.Any(char.IsControl))
            throw new InvalidDataException("A valid original native process epoch is required.");
        NngTransport.ValidateEndpoint(Endpoint);
    }

    internal static void ValidateRecorded(UpdateOrigin? origin, int schema, int firstVerifiedSchema)
    {
        if (origin is not null && schema < firstVerifiedSchema)
            throw new InvalidDataException("A legacy handoff cannot claim verified native origin metadata.");
        origin?.Validate();
    }

    internal static async Task VerifyAsync(UpdateOrigin? origin, Guid instance, string project,
        CancellationToken token, INativeTransport? transport = null)
    {
        if (origin is null) return; // Legacy/manual sessions are not eligible for MCP replacement adoption.
        origin.Validate(); token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var client = new NativeClient(transport ?? new NngTransport(), origin.Endpoint, origin.Epoch);
        try
        {
            var session = await client.HandshakeAsync(deadline.Token);
            if (session.InstanceId != instance.ToString("D") || session.ProjectPath != project || session.Epoch != origin.Epoch)
                throw new InvalidDataException("The restart origin does not match the exact native instance, project and epoch.");
        }
        catch (Exception error) when (error is NngException or NativeApiException or AutomationException)
        { throw new InvalidDataException("The original native session could not be verified; it must remain open.", error); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new InvalidDataException("The original native session did not verify before the restart deadline.", error); }
    }
}
