using Microsoft.AspNetCore.Authorization;

namespace SonicRelay.Api.Authorization;

public sealed class DeviceScopeRequirement(string? scope = null) : IAuthorizationRequirement
{
    public string? Scope { get; } = scope;
}
