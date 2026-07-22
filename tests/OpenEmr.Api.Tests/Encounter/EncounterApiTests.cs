using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Encounter;

[Collection("OpenEmr API")]
public class EncounterApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public EncounterApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Encounter_Returns_Created_With_New_Euuid()
    {
        var puuid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await CreateTestEncounterAsync(puuid);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("euuid").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Get_Encounters_For_Patient_Returns_Only_That_Patients_Encounters()
    {
        var puuid = await CreateTestPatientAsync("Grace", "Hopper");
        await CreateTestEncounterAsync(puuid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/encounter"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array, "response body was: {0}", raw);
        body.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0, "response body was: {0}", raw);
        body.GetProperty("data").EnumerateArray().Should().OnlyContain(item => item.GetProperty("puuid").GetString() == puuid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Close_Encounter_Sets_Last_Level_Closed_True()
    {
        var puuid = await CreateTestPatientAsync("Katherine", "Johnson");
        var created = await CreateTestEncounterAsync(puuid);
        var createdRaw = await created.Content.ReadAsStringAsync();
        var euuid = JsonDocument.Parse(createdRaw).RootElement.GetProperty("data").GetProperty("euuid").GetString();
        var closePayload = new { last_level_closed = "1", user = _fixture.Options.AdminUser, group = "Default" };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/encounter/{euuid}"), closePayload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("last_level_closed").GetInt32().Should().Be(1, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Fhir_Encounter_Search_Filtered_By_Patient_Returns_The_Created_Encounter()
    {
        var puuid = await CreateTestPatientAsync("Marie", "Curie");
        var created = await CreateTestEncounterAsync(puuid);
        created.StatusCode.Should().Be(HttpStatusCode.Created, "fixture encounter creation should succeed");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, $"Encounter?patient={puuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle", "response body was: {0}", raw);
        body.GetProperty("total").GetInt32().Should().BeGreaterThan(0, "a bare, unfiltered FHIR search can return a technically-valid but misleadingly empty bundle for a patient-scoped resource - filtering by the real patient uuid (confirmed to be the same uuid as the REST puuid) and asserting a real entry proves the search genuinely works, response body was: {0}", raw);
        body.GetProperty("entry").EnumerateArray().Should().Contain(
            e => e.GetProperty("resource").GetProperty("subject").GetProperty("reference").GetString() == $"Patient/{puuid}",
            "response body was: {0}", raw);
    }

    private async Task<string> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        return body.GetProperty("data").GetProperty("uuid").GetString()!;
    }

    private async Task<HttpResponseMessage> CreateTestEncounterAsync(string puuid)
    {
        var payload = new { pc_catid = "5", class_code = "AMB" };
        return await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/encounter"), payload, ExactCasing);
    }
}
