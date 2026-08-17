using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// The Swagger UI is the API's public face, and its heading comes from the
/// OpenAPI document's <c>info.title</c>. Left unconfigured, Swashbuckle falls
/// back to the assembly name, so the page announces itself as
/// "SonicRelay.Api" — an internal identifier, not the product name from
/// issue #38. These tests pin the canonical spelling.
/// </summary>
public sealed class OpenApiBrandingTests
{
    private const string PublicName = "SonicRelay";

    private static SonicRelayApiFactory CreateFactoryWithSwagger() =>
        new(new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

    [Fact]
    public async Task Document_title_is_the_public_product_name()
    {
        using var factory = CreateFactoryWithSwagger();

        var document = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        Assert.Equal($"{PublicName} API", document.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Document_describes_what_the_api_is_for()
    {
        using var factory = CreateFactoryWithSwagger();

        var document = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var description = document.GetProperty("info").GetProperty("description").GetString();

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Contains(PublicName, description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sonic Relay")]
    [InlineData("Sonic_Relay")]
    public async Task Document_info_never_uses_a_spaced_or_underscored_variant(string forbidden)
    {
        using var factory = CreateFactoryWithSwagger();

        var document = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");

        Assert.DoesNotContain(forbidden, document, StringComparison.OrdinalIgnoreCase);
    }
}
