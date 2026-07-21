using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Prescription;

[Collection("OpenEmr API")]
public class PrescriptionApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public PrescriptionApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Prescription_Returns_NotFound()
    {
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "prescription"), new { }, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Prescription is a read/search-only (rs) resource on this API and no POST route is registered for it at all, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Prescription_List_Includes_Seeded_Record()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Ada", "Lovelace");
        var marker = await InsertPrescriptionRowAsync(pid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "prescription"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("drug").GetString() == marker && item.GetProperty("puuid").GetString() == puuid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Prescription_List_Ignores_Patient_Id_Query_Filter()
    {
        var (targetPid, _) = await CreateTestPatientAsync("Grace", "Hopper");
        var (otherPid, _) = await CreateTestPatientAsync("Katherine", "Johnson");
        var marker = await InsertPrescriptionRowAsync(targetPid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"prescription?patient_id={otherPid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("drug").GetString() == marker, "'GET /api/prescription' => function () {{ ... ->getAll() }} never forwards $_GET to the service, so a query-string filter for a completely different patient still returns this record — list-by-patient is not actually possible on this route, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Prescription_By_Uuid_Always_Returns_InternalServerError()
    {
        var (pid, _) = await CreateTestPatientAsync("Rosalind", "Franklin");
        var marker = await InsertPrescriptionRowAsync(pid);
        var uuid = await FindPrescriptionUuidAsync(marker);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"prescription/{uuid}"));
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "PrescriptionService::getOne($uuid) calls $this->getAll(['_id' => $uuid], $puuidBind) — '_id' is not a real column on the combined_prescriptions query, so the generated SQL fails preparation on every call, valid uuid or not, making this single-record read route completely non-functional");
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

    private async Task<string> InsertPrescriptionRowAsync(int patientPid)
    {
        var marker = $"FixtureDrug{DateTime.UtcNow.Ticks}";
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO prescriptions (patient_id, drug, txDate, usage_category_title, request_intent_title, date_added)
              VALUES (@PatientId, @Drug, CURDATE(), 'Outpatient', 'Order', NOW())",
            new { PatientId = patientPid, Drug = marker });
        return marker;
    }

    private async Task<string> FindPrescriptionUuidAsync(string marker)
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "prescription"));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var match = body.GetProperty("data").EnumerateArray().First(item => item.GetProperty("drug").GetString() == marker);
        return match.GetProperty("uuid").GetString()!;
    }
}
