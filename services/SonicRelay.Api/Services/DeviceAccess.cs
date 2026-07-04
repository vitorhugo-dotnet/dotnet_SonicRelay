using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Devices;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

public enum DeviceEligibility
{
    Missing,
    Ineligible,
    Eligible
}

public static class DeviceAccess
{
    public static async Task<DeviceEligibility> CheckAsync(AppDbContext db, Guid deviceId, Guid ownerUserId,
        string expectedType, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deviceId && x.OwnerUserId == ownerUserId, ct);
        if (device is null) return DeviceEligibility.Missing;

        var platformCompatible = expectedType switch
        {
            DeviceTypes.WindowsPublisher => device.Platform == DevicePlatforms.Windows,
            DeviceTypes.FlutterViewer => device.Platform is DevicePlatforms.Android or DevicePlatforms.Ios,
            _ => false
        };
        return !device.Revoked && device.Type == expectedType && platformCompatible
            ? DeviceEligibility.Eligible
            : DeviceEligibility.Ineligible;
    }
}
