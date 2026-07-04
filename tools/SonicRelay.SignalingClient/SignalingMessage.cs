using System.Text.Json;

namespace SonicRelay.SignalingClient;

public sealed record SignalingMessage(string Type, Guid MessageId, Guid To, JsonElement Payload)
{
    public static SignalingMessage Create(string type, Guid to, JsonElement payload) =>
        new(type, Guid.NewGuid(), to, payload.Clone());

    public static void ValidateRouted(JsonElement envelope, string type, Guid messageId,
        Guid sessionId, Guid sender, Guid recipient)
    {
        Require(envelope.GetProperty("type").GetString() == type, "type");
        Require(envelope.GetProperty("messageId").GetGuid() == messageId, "message ID");
        Require(envelope.GetProperty("sessionId").GetGuid() == sessionId, "session");
        Require(envelope.GetProperty("from").GetGuid() == sender, "sender");
        Require(envelope.GetProperty("to").GetGuid() == recipient, "recipient");
        Require(envelope.GetProperty("timestamp").ValueKind == JsonValueKind.String, "timestamp");
        Require(envelope.TryGetProperty("payload", out _), "payload");
    }

    private static void Require(bool condition, string field)
    {
        if (!condition)
            throw new InvalidOperationException($"Routed signaling envelope has unexpected {field}.");
    }
}
