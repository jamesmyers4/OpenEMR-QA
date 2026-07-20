using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Facility;

[Collection("OpenEmr API")]
public class FacilityApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public FacilityApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Facility_Returns_Created_With_New_Id()
    {
        var payload = new { name = UniqueName("Aquaria"), facility_npi = UniqueNpi() };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("id").GetInt32().Should().BeGreaterThan(0, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Facility_By_Uuid_Returns_Matching_Record()
    {
        var (_, uuid, name) = await CreateTestFacilityAsync();
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"facility/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("name").GetString().Should().Be(name, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Facility_List_Contains_Created_Facility()
    {
        var (_, uuid, name) = await CreateTestFacilityAsync();
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("uuid").GetString() == uuid && item.GetProperty("name").GetString() == name, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Update_Facility_Persists_Changed_Fields()
    {
        var (_, uuid, _) = await CreateTestFacilityAsync();
        var update = new { city = "Knoxville" };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"facility/{uuid}"), update, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"facility/{uuid}"));
        var verifyRaw = await verify.Content.ReadAsStringAsync();
        var verifyBody = JsonDocument.Parse(verifyRaw).RootElement;
        verifyBody.GetProperty("data").GetProperty("city").GetString().Should().Be("Knoxville", "response body was: {0}", verifyRaw);
    }

    [Fact]
    public async Task Create_Facility_Missing_Required_Field_Returns_BadRequest()
    {
        var payload = new { facility_npi = UniqueNpi() };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Facility_By_Nonexistent_Uuid_Returns_BadRequest()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility/00000000-0000-0000-0000-000000000000"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "response body was: {0}", raw);
    }

    private async Task<(int Id, string Uuid, string Name)> CreateTestFacilityAsync()
    {
        var uniqueName = UniqueName("Aquaria");
        var payload = new { name = uniqueName, facility_npi = UniqueNpi() };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture facility creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        var data = body.GetProperty("data");
        return (data.GetProperty("id").GetInt32(), data.GetProperty("uuid").GetString()!, uniqueName);
    }

    private static string UniqueName(string baseName) => $"{baseName}{DateTime.UtcNow.Ticks}";

    private static string UniqueNpi() => DateTime.UtcNow.Ticks.ToString().Substring(0, 10);
}
