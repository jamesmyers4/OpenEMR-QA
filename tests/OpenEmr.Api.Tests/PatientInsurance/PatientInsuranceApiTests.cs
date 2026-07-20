using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.PatientInsurance;

[Collection("OpenEmr API")]
public class PatientInsuranceApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public PatientInsuranceApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_PatientInsurance_Returns_Ok_With_New_Uuid()
    {
        var puuid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await CreateTestInsuranceAsync(puuid);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "InsuranceRestController::post() always calls handleProcessingResult with 200, never 201, response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("uuid").GetString().Should().NotBeNullOrEmpty("response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_PatientInsurance_By_Uuid_Returns_Matching_Record()
    {
        var puuid = await CreateTestPatientAsync("Grace", "Hopper");
        var (uuid, policyNumber) = await CreateAndParseTestInsuranceAsync(puuid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("policy_number").GetString().Should().Be(policyNumber, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_PatientInsurance_List_Returns_Created_Record()
    {
        var puuid = await CreateTestPatientAsync("Katherine", "Johnson");
        var (uuid, _) = await CreateAndParseTestInsuranceAsync(puuid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("uuid").GetString() == uuid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Update_PatientInsurance_With_Partial_Payload_Nulls_Unset_Fields()
    {
        var puuid = await CreateTestPatientAsync("Marie", "Curie");
        var (uuid, _) = await CreateAndParseTestInsuranceAsync(puuid);
        var update = new { policy_number = $"UPDATED{DateTime.UtcNow.Ticks}" };
        var updateResponse = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/{uuid}"), update, ExactCasing);
        var updateRaw = await updateResponse.Content.ReadAsStringAsync();
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", updateRaw);
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/{uuid}"));
        var verifyRaw = await verify.Content.ReadAsStringAsync();
        var verifyBody = JsonDocument.Parse(verifyRaw).RootElement.GetProperty("data");
        verifyBody.GetProperty("policy_number").GetString().Should().Be(update.policy_number, "response body was: {0}", verifyRaw);
        verifyBody.GetProperty("subscriber_lname").ValueKind.Should().Be(JsonValueKind.Null, "InsuranceService::update() runs an unconditional full-column UPDATE using every key in the request body, so fields omitted from a PUT payload are overwritten with null rather than preserved, response body was: {0}", verifyRaw);
    }

    [Fact]
    public async Task Create_PatientInsurance_Missing_Required_Field_Returns_BadRequest()
    {
        var puuid = await CreateTestPatientAsync("Rosalind", "Franklin");
        var payload = new
        {
            type = "primary",
            provider = "1",
            subscriber_lname = "Franklin",
            subscriber_fname = "Rosalind",
            subscriber_relationship = "spouse",
            subscriber_DOB = "1988-02-02",
            subscriber_street = "123 Test St",
            subscriber_postal_code = "54321",
            subscriber_city = "Testville",
            subscriber_state = "CA",
            subscriber_sex = "Female",
            accept_assignment = "TRUE",
            date = "2026-01-01",
            date_end = "2026-12-31"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "policy_number was omitted, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_PatientInsurance_By_Nonexistent_Uuid_Returns_NotFound()
    {
        var puuid = await CreateTestPatientAsync("Chien-Shiung", "Wu");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/00000000-0000-0000-0000-000000000000"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "response body was: {0}", raw);
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

    private async Task<HttpResponseMessage> CreateTestInsuranceAsync(string puuid)
    {
        var payload = new
        {
            type = "primary",
            provider = "1",
            policy_number = $"POL{DateTime.UtcNow.Ticks}",
            subscriber_lname = "Curl",
            subscriber_fname = "InsTest",
            subscriber_relationship = "spouse",
            subscriber_DOB = "1988-02-02",
            subscriber_street = "123 Test St",
            subscriber_postal_code = "54321",
            subscriber_city = "Testville",
            subscriber_state = "CA",
            subscriber_sex = "Female",
            accept_assignment = "TRUE",
            date = "2026-01-01",
            date_end = "2026-12-31"
        };
        return await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance"), payload, ExactCasing);
    }

    private async Task<(string Uuid, string PolicyNumber)> CreateAndParseTestInsuranceAsync(string puuid)
    {
        var response = await CreateTestInsuranceAsync(puuid);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"fixture insurance creation should succeed, response was: {raw}");
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        return (data.GetProperty("uuid").GetString()!, data.GetProperty("policy_number").GetString()!);
    }
}
