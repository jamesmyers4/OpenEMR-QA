using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.MedicationRequest;

[Collection("OpenEmr API")]
public class MedicationRequestApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public MedicationRequestApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Fhir_MedicationRequest_Search_Filtered_By_Patient_Returns_The_Seeded_Prescription()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Grace", "Hopper");
        var marker = await InsertPrescriptionRowAsync(pid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, $"MedicationRequest?patient={puuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle", "response body was: {0}", raw);
        body.GetProperty("total").GetInt32().Should().BeGreaterThan(0, "Prescription is a read/search-only resource with no REST create endpoint, so this fixture is seeded via the same direct 'prescriptions' insert PrescriptionApiTests already uses - a bare, unfiltered search could return a technically-valid but misleadingly empty bundle, response body was: {0}", raw);
        body.GetProperty("entry").EnumerateArray().Should().Contain(
            e => e.GetProperty("resource").GetProperty("medicationCodeableConcept").GetProperty("text").GetString() == marker
                 && e.GetProperty("resource").GetProperty("subject").GetProperty("reference").GetString() == $"Patient/{puuid}",
            "response body was: {0}", raw);
    }

    private async Task<(int Pid, string Puuid)> CreateTestPatientAsync(string first, string last)
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
}
