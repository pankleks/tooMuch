using System.Net;
using System.Text.Json;
using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class AgentHeartbeatTests
{
    [Fact]
    public async Task SendHeartbeat_Includes_Assembly_Version()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-test-" + Guid.NewGuid().ToString("N"));
        var handler = new CaptureHandler();
        try
        {
            using var agent = new Agent(new AgentConfig
            {
                ServerUrl = "http://example.test",
                DeviceId = "test-device",
                Token = "test-token",
                DataDir = dir,
            }, httpHandler: handler);

            await agent.SendHeartbeatAsync(locked: false, counting: true, ct: CancellationToken.None);

            using var payload = JsonDocument.Parse(handler.Body!);
            var assemblyVersion = typeof(Agent).Assembly.GetName().Version!;
            Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}",
                payload.RootElement.GetProperty("client_version").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
