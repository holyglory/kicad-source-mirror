using System.Runtime.InteropServices;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Uses the real native NNG library with an isolated echo peer, not a simulated editor.
[TestClass]
public sealed class NngTransportTests
{
    [TestMethod]
    [DataRow(6)]
    [DataRow(2097152)]
    public async Task NativeRequestReplyPreservesBinaryBytes(int payloadSize)
    {
        string directory = Directory.CreateTempSubdirectory("kng-").FullName;
        string endpoint = "ipc://" + Path.Combine(directory, "peer.sock");
        Nng.Check(Nng.nng_rep0_open(out var socket));
        Task? server = null;
        bool exchangeCompleted = false;
        try
        {
            Nng.Check(Nng.nng_setopt_ms(socket, "recv-timeout", 5000));
            Nng.Check(Nng.nng_setopt_ms(socket, "send-timeout", 5000));
            // Isolate the real client's receive behavior from the echo peer's
            // receive default. No mock transport or encoding shortcut is used.
            Nng.Check(Nng.nng_setopt_size(socket, "recv-size-max", 8 * 1024 * 1024));
            Nng.Check(Nng.nng_listen(socket, endpoint, IntPtr.Zero, 0));
            server = Task.Run(() =>
            {
                nuint size = 0;
                Nng.Check(Nng.nng_recv(socket, out IntPtr buffer, ref size, 1));
                try
                {
                    byte[] bytes = new byte[checked((int)size)];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    Nng.Check(Nng.nng_send(socket, bytes, size, 0));
                }
                finally { Nng.nng_free(buffer, size); }
            });
            byte[] input = new byte[payloadSize];
            for (int i = 0; i < input.Length; i++) input[i] = (byte)(i % 256);
            byte[] output = await new NngTransport().ExchangeAsync(endpoint, input, TimeSpan.FromSeconds(5));
            exchangeCompleted = true;
            await server;
            Assert.AreEqual(input.Length, output.Length);
            Assert.IsTrue(input.AsSpan().SequenceEqual(output), "The native binary payload must be preserved completely.");
        }
        finally
        {
            Nng.nng_close(socket);
            if (server is not null)
                try { await server; } catch (NngException) when (!exchangeCompleted) { }
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CancellationAfterPeerReceivesRequestAllowsNextExchange()
    {
        string directory = Directory.CreateTempSubdirectory("kng-").FullName;
        string endpoint = "ipc://" + Path.Combine(directory, "peer.sock");
        Nng.Check(Nng.nng_rep0_open(out var socket));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? server = null;
        bool completed = false;
        try
        {
            Nng.Check(Nng.nng_setopt_ms(socket, "recv-timeout", 5000));
            Nng.Check(Nng.nng_setopt_ms(socket, "send-timeout", 5000));
            Nng.Check(Nng.nng_listen(socket, endpoint, IntPtr.Zero, 0));
            server = Task.Run(async () =>
            {
                for (int exchange = 0; exchange < 2; exchange++)
                {
                    nuint size = 0;
                    Nng.Check(Nng.nng_recv(socket, out IntPtr buffer, ref size, 1));
                    try
                    {
                        byte[] bytes = new byte[checked((int)size)];
                        Marshal.Copy(buffer, bytes, 0, bytes.Length);
                        if (exchange == 0)
                        {
                            received.SetResult();
                            await releaseReply.Task;
                        }
                        Nng.Check(Nng.nng_send(socket, bytes, size, 0));
                    }
                    finally { Nng.nng_free(buffer, size); }
                }
            });
            using var cancellation = new CancellationTokenSource();
            var transport = new NngTransport();
            Task<byte[]> first = transport.ExchangeAsync(endpoint, [1, 2, 3],
                TimeSpan.FromSeconds(5), cancellation.Token);
            try
            {
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first);
            }
            finally
            {
                cancellation.Cancel();
                releaseReply.TrySetResult();
                try { await first; } catch (OperationCanceledException) { }
            }
            byte[] result = await transport.ExchangeAsync(endpoint, [4, 5, 6], TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, result,
                "A new request must receive its own reply, not the cancelled request's reply.");
            await server;
            completed = true;
        }
        finally
        {
            releaseReply.TrySetResult();
            Nng.nng_close(socket);
            if (server is not null)
                try { await server; } catch (NngException) when (!completed) { }
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task MissingPeerCanBeCancelledAndRetriedWithoutLeakingSocket()
    {
        string directory = Directory.CreateTempSubdirectory("kng-").FullName;
        try
        {
            string endpoint = "ipc://" + Path.Combine(directory, "absent.sock");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    new NngTransport().ExchangeAsync(endpoint, [1], TimeSpan.FromSeconds(10), cancellation.Token));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

}
