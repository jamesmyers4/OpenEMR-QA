using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.InsuranceCompany;

[Collection("OpenEmr API")]
public class InsuranceCompanyApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public InsuranceCompanyApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_InsuranceCompany_List_Returns_Empty_Object_When_No_Records_Exist()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "insurance_company"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.ValueKind.Should().Be(JsonValueKind.Object, "response body was: {0}", raw);
        body.EnumerateObject().Should().BeEmpty("this instance has no seeded insurance_companies rows and creation is broken (see below), response body was: {0}", raw);
    }

    [Fact]
    public async Task Create_InsuranceCompany_Returns_InternalServerError()
    {
        var payload = new { name = UniqueName("Cool Insurance Co") };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "insurance_company"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "InsuranceCompanyRestController::post() calls InsuranceCompanyService::validate(), a method that does not exist on this class in this OpenEMR version, response body was: {0}", raw);
    }

    [Fact]
    public async Task Update_InsuranceCompany_Returns_InternalServerError()
    {
        var payload = new { name = UniqueName("Cool Insurance Co") };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "insurance_company/1"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "InsuranceCompanyRestController::put() calls the same missing InsuranceCompanyService::validate() method as post(), response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_InsuranceCompany_By_Nonexistent_Id_Returns_NotFound()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "insurance_company/999999"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string UniqueName(string baseName) => $"{baseName}{DateTime.UtcNow.Ticks}";
}
