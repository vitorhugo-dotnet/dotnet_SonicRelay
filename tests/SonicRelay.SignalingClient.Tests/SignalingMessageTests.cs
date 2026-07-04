using System.Text.Json;
using SonicRelay.SignalingClient;
using Xunit;

namespace SonicRelay.SignalingClient.Tests;

public sealed class SignalingMessageTests
{
    [Fact]
    public void Create_builds_a_client_envelope_with_fake_payload()
    {
        var recipient = Guid.NewGuid();
        using var payload = JsonDocument.Parse("""{"sdp":"fake-test-offer-sdp"}""");

        var message = SignalingMessage.Create("webrtc.offer", recipient, payload.RootElement);

        Assert.Equal("webrtc.offer", message.Type);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.Equal(recipient, message.To);
        Assert.Equal("fake-test-offer-sdp", message.Payload.GetProperty("sdp").GetString());
    }

    [Fact]
    public void ValidateRouted_accepts_expected_server_metadata()
    {
        var sessionId = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "viewer.ready", messageId, sessionId, from = sender, to = recipient,
            timestamp = "2026-07-04T12:00:00Z", payload = new { marker = "fake-test-viewer-ready" }
        }));

        SignalingMessage.ValidateRouted(
            document.RootElement, "viewer.ready", messageId, sessionId, sender, recipient);
    }

    [Fact]
    public void ValidateRouted_rejects_wrong_sender()
    {
        var sessionId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "publisher.ready", messageId, sessionId, from = Guid.NewGuid(), to = recipient,
            timestamp = "2026-07-04T12:00:00Z", payload = new { marker = "fake-test-publisher-ready" }
        }));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SignalingMessage.ValidateRouted(
                document.RootElement, "publisher.ready", messageId, sessionId, Guid.NewGuid(), recipient));

        Assert.Contains("sender", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
