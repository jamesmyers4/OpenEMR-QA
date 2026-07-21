using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Immunization;

[Collection("OpenEmr API")]
public class ImmunizationApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public ImmunizationApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Immunization_Returns_NotFound()
    {
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "immunization"), new { cvx_code = "08" }, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Immunization is a read/search-only (rs) resource on this API and no POST route is registered for it at all, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Immunization_List_Filtered_By_Patient_Id_Returns_Seeded_Record()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Ada", "Lovelace");
        var marker = await InsertImmunizationRowAsync(pid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"immunization?patient_id={pid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("lot_number").GetString() == marker && item.GetProperty("puuid").GetString() == puuid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Immunization_By_Uuid_Returns_Matching_Record()
    {
        var (pid, _) = await CreateTestPatientAsync("Grace", "Hopper");
        var marker = await InsertImmunizationRowAsync(pid);
        var uuid = await FindImmunizationUuidAsync(pid, marker);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"immunization/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("lot_number").GetString().Should().Be(marker, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Immunization_By_Nonexistent_Uuid_Returns_NotFound()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "immunization/00000000-0000-0000-0000-000000000000"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "response body was: {0}", raw);
    }

    private async Task<(int Pid, string Uuid)> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        return (data.GetProperty("pid").GetInt32(), data.GetProperty("uuid").GetString()!);
    }

    private async Task<string> InsertImmunizationRowAsync(int patientPid)
    {
        var marker = $"LOT{DateTime.UtcNow.Ticks}";
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO immunizations (patient_id, administered_date, cvx_code, manufacturer, lot_number, added_erroneously, create_date, update_date) VALUES (@PatientId, NOW(), '08', 'FixtureMfg', @LotNumber, 0, NOW(), NOW())",
            new { PatientId = patientPid, LotNumber = marker });
        return marker;
    }

    private async Task<string> FindImmunizationUuidAsync(int patientPid, string marker)
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"immunization?patient_id={patientPid}"));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var match = body.GetProperty("data").EnumerateArray().First(item => item.GetProperty("lot_number").GetString() == marker);
        return match.GetProperty("uuid").GetString()!;
    }
}
