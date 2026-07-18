using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Practitioner;

[Collection("OpenEmr API")]
public class PractitionerApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public PractitionerApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Practitioner_List_Returns_Practitioner_With_Username()
    {
        var (_, uuid, lastName) = await CreateTestPractitionerAsync("Grace", "Hopper");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "practitioner"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("uuid").GetString() == uuid && item.GetProperty("lname").GetString() == lastName, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Practitioner_By_Uuid_Returns_Matching_Record()
    {
        var (_, uuid, lastName) = await CreateTestPractitionerAsync("Katherine", "Johnson");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"practitioner/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("lname").GetString().Should().Be(lastName, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Create_Practitioner_Without_Username_Is_Invisible_To_List_And_Get()
    {
        var (_, uuid, _) = await CreateTestPractitionerAsync("Ada", "Lovelace", withUsername: false);
        var listResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "practitioner"));
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        var listBody = JsonDocument.Parse(listRaw).RootElement;
        listBody.GetProperty("data").EnumerateArray().Should().NotContain(item => item.GetProperty("uuid").GetString() == uuid, "response body was: {0}", listRaw);
        var getResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"practitioner/{uuid}"));
        var getRaw = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "response body was: {0}", getRaw);
    }

    private async Task<(int Id, string Uuid, string LastName)> CreateTestPractitionerAsync(string first, string last, bool withUsername = true)
    {
        var uniqueLastName = $"{last}{DateTime.UtcNow.Ticks}";
        object payload = withUsername
            ? new { fname = first, lname = uniqueLastName, npi = UniqueNpi(), username = $"apitest{DateTime.UtcNow.Ticks}" }
            : new { fname = first, lname = uniqueLastName, npi = UniqueNpi() };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "practitioner"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"fixture practitioner creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        var data = body.GetProperty("data");
        return (data.GetProperty("id").GetInt32(), data.GetProperty("uuid").GetString()!, uniqueLastName);
    }

    private static string UniqueNpi() => DateTime.UtcNow.Ticks.ToString().Substring(0, 10);
}
