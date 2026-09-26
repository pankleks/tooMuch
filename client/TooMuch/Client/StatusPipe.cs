using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TooMuch.Core;

namespace TooMuch.Client;

internal static class StatusPipe
{
    public const string Name = "TooMuch.Status";

    public static async Task Serve(string childSid, Func<ChildStatus?> readStatus, Action<Exception> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.SetOwner(new SecurityIdentifier("S-1-5-18")); // LocalSystem
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-18"), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-32-544"), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(childSid), PipeAccessRights.Read, AccessControlType.Allow));

                using var pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4096, security);
                await pipe.WaitForConnectionAsync(ct);
                var status = readStatus();
                var payload = JsonSerializer.SerializeToUtf8Bytes(status);
                await pipe.WriteAsync(payload, ct);
                await pipe.FlushAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log(ex);
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    public static async Task<ChildStatus?> Read(CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.In, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, ct);
        using var stream = new MemoryStream();
        await pipe.CopyToAsync(stream, ct);
        return JsonSerializer.Deserialize<ChildStatus>(stream.ToArray());
    }
}

internal sealed class StatusSnapshotStore
{
    private ChildStatus? current;
    public ChildStatus? Read() => Volatile.Read(ref current);
    public void Publish(ChildStatus status) => Volatile.Write(ref current, status);
}
