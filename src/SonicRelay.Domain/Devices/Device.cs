namespace SonicRelay.Domain.Devices;

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = DeviceTypes.FlutterViewer;
    public string Platform { get; set; } = DevicePlatforms.Android;
    public string? PublicKey { get; set; }
    public bool Trusted { get; set; } = true;
    public bool Revoked { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class DeviceTypes
{
    public const string WindowsPublisher = "windows_publisher";
    public const string FlutterViewer = "flutter_viewer";
}

public static class DevicePlatforms
{
    public const string Windows = "windows";
    public const string Android = "android";
    public const string Ios = "ios";
}
