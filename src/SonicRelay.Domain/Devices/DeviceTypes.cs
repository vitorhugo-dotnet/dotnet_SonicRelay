namespace SonicRelay.Domain.Devices;

// The old owner-scoped Device entity (ApplicationUser-owned CRUD) was removed in issue #26
// Phase 4. DeviceTypes/DevicePlatforms remain: they are shared constants also used by
// SonicRelay.Domain.DeviceIdentities.DeviceIdentity, the entity that replaced it.
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
