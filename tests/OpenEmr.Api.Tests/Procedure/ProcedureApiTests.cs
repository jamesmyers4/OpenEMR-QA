using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Procedure;

[Collection("OpenEmr API")]
public class ProcedureApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public ProcedureApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Procedure_Returns_NotFound()
    {
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "procedure"), new { }, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Procedure is a read/search-only (rs) resource on this API and no POST route is registered for it at all, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Procedure_By_Uuid_Returns_Matching_Record()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Ada", "Lovelace");
        var encounterId = await InsertEncounterRowAsync(pid);
        var (uuid, controlId) = await InsertLinkedProcedureOrderAsync(pid, encounterId);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"procedure/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("control_id").GetString().Should().Be(controlId, "response body was: {0}", raw);
        body.GetProperty("data").GetProperty("puuid").GetString().Should().Be(puuid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Procedure_List_Ignores_Patient_Id_Query_Filter()
    {
        var (targetPid, _) = await CreateTestPatientAsync("Grace", "Hopper");
        var (otherPid, _) = await CreateTestPatientAsync("Katherine", "Johnson");
        var encounterId = await InsertEncounterRowAsync(targetPid);
        var (uuid, controlId) = await InsertLinkedProcedureOrderAsync(targetPid, encounterId);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"procedure?patient_id={otherPid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").EnumerateArray().Should().Contain(item => item.GetProperty("uuid").GetString() == uuid, "'GET /api/procedure' => function () {{ ... ->getAll() }} never forwards $_GET to the service, so a query-string filter for a completely different patient still returns this record — list-by-patient is not actually possible on this route, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Procedure_With_Unresolved_Encounter_Returns_InternalServerError()
    {
        var (pid, _) = await CreateTestPatientAsync("Rosalind", "Franklin");
        var orderId = await InsertOrphanedProcedureOrderAsync(pid);
        try
        {
            var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "procedure"));
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "ProcedureService::getAll()/getOne() call UuidRegistry::uuidToString() on the LEFT-JOINed encounter/practitioner uuid columns without a null guard, so any procedure_order row whose encounter_id or provider_id does not resolve to a real row crashes the entire list, not just that record");
        }
        finally
        {
            await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM procedure_order WHERE procedure_order_id = @Id", new { Id = orderId });
        }
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

    private async Task<long> InsertEncounterRowAsync(int patientPid)
    {
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        var encounterId = DateTime.UtcNow.Ticks;
        await connection.ExecuteAsync(
            "INSERT INTO form_encounter (date, reason, facility, pid, encounter, provider_id) VALUES (NOW(), 'Fixture Visit', 'Fixture Facility', @Pid, @EncounterId, 1)",
            new { Pid = patientPid, EncounterId = encounterId });
        return encounterId;
    }

    private async Task<(string Uuid, string ControlId)> InsertLinkedProcedureOrderAsync(int patientPid, long encounterId)
    {
        var controlId = $"CTRL{DateTime.UtcNow.Ticks}";
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO procedure_order (provider_id, patient_id, encounter_id, order_priority, order_status, control_id, clinical_hx, specimen_type, specimen_location, specimen_volume)
              VALUES (1, @PatientId, @EncounterId, 'normal', 'pending', @ControlId, 'none', 'blood', 'arm', '5ml')",
            new { PatientId = patientPid, EncounterId = encounterId, ControlId = controlId });
        var uuid = await FindProcedureUuidAsync(controlId);
        return (uuid, controlId);
    }

    private async Task<int> InsertOrphanedProcedureOrderAsync(int patientPid)
    {
        var controlId = $"ORPHAN{DateTime.UtcNow.Ticks}";
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO procedure_order (provider_id, patient_id, encounter_id, order_priority, order_status, control_id, clinical_hx, specimen_type, specimen_location, specimen_volume)
              VALUES (1, @PatientId, 0, 'normal', 'pending', @ControlId, 'none', 'blood', 'arm', '5ml')",
            new { PatientId = patientPid, ControlId = controlId });
        return await connection.QuerySingleAsync<int>("SELECT procedure_order_id FROM procedure_order WHERE control_id = @ControlId", new { ControlId = controlId });
    }

    private async Task<string> FindProcedureUuidAsync(string controlId)
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "procedure"));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var match = body.GetProperty("data").EnumerateArray().First(item => item.GetProperty("control_id").GetString() == controlId);
        return match.GetProperty("uuid").GetString()!;
    }
}
