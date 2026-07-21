using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Allergy;

[Collection("OpenEmr API")]
public class AllergyApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public AllergyApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Allergy_Returns_Created_With_New_Uuid()
    {
        var puuid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await CreateTestAllergyAsync(puuid, "Iodine");
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("uuid").GetString().Should().NotBeNullOrEmpty("response body was: {0}", raw);
    }

    [Fact]
    public async Task Create_Allergy_Missing_Title_Returns_BadRequest()
    {
        var puuid = await CreateTestPatientAsync("Grace", "Hopper");
        var payload = new { begdate = "2026-01-01 00:00:00" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/allergy"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "title was omitted, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Allergy_List_By_Patient_Nested_Route_Returns_Empty_Despite_Existing_Record()
    {
        var puuid = await CreateTestPatientAsync("Katherine", "Johnson");
        await CreateAndParseTestAllergyAsync(puuid, "Penicillin");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/allergy"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetArrayLength().Should().Be(0, "AllergyIntoleranceService::getAll() maps the 'lists.pid' search key this route passes to 'patient_id' and compares it as a raw string against the numeric pid column using the puuid value verbatim, so it can never match a real row, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Allergy_List_Filtered_By_Puuid_Query_Param_Returns_Created_Record()
    {
        var puuid = await CreateTestPatientAsync("Marie", "Curie");
        var (uuid, title) = await CreateAndParseTestAllergyAsync(puuid, "Latex");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"allergy?puuid={puuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("uuid").GetString() == uuid && item.GetProperty("title").GetString() == title, "the top-level list route's 'puuid' search key resolves the uuid to a pid correctly, unlike the broken patient-nested route, response body was: {0}", raw);
    }

    [Fact]
    public async Task Delete_Allergy_Removes_Record_Then_Returns_NotFound()
    {
        var puuid = await CreateTestPatientAsync("Rosalind", "Franklin");
        var (uuid, _) = await CreateAndParseTestAllergyAsync(puuid, "Sulfa");
        var deleteResponse = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/allergy/{uuid}"));
        var deleteRaw = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", deleteRaw);
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"allergy/{uuid}"));
        var verifyRaw = await verify.Content.ReadAsStringAsync();
        verify.StatusCode.Should().Be(HttpStatusCode.NotFound, "response body was: {0}", verifyRaw);
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

    private async Task<HttpResponseMessage> CreateTestAllergyAsync(string puuid, string title)
    {
        var payload = new { title, begdate = "2026-01-01 00:00:00", comments = "created by AllergyApiTests" };
        return await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/allergy"), payload, ExactCasing);
    }

    private async Task<(string Uuid, string Title)> CreateAndParseTestAllergyAsync(string puuid, string title)
    {
        var response = await CreateTestAllergyAsync(puuid, title);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"fixture allergy creation should succeed, response was: {raw}");
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        return (data.GetProperty("uuid").GetString()!, title);
    }
}
