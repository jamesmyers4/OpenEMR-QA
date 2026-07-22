using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Condition;

[Collection("OpenEmr API")]
public class ConditionApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public ConditionApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Fhir_Condition_Search_Filtered_By_Patient_Returns_The_Seeded_Medical_Problem()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Rosalind", "Franklin");
        await InsertMedicalProblemRowAsync(pid);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, $"Condition?patient={puuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle", "response body was: {0}", raw);
        body.GetProperty("total").GetInt32().Should().BeGreaterThan(0, "OpenEMR's closest REST equivalent to Condition, Medical Problem, has no REST test class or create endpoint built in this project, so this fixture is seeded via a direct insert into 'lists' with type='medical_problem' - a bare, unfiltered search could return a technically-valid but misleadingly empty bundle, response body was: {0}", raw);
        body.GetProperty("entry").EnumerateArray().Should().Contain(
            e => e.GetProperty("resource").GetProperty("subject").GetProperty("reference").GetString() == $"Patient/{puuid}",
            "response body was: {0}", raw);
    }

    private async Task<(int Pid, string Puuid)> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        return (body.GetProperty("pid").GetInt32(), body.GetProperty("uuid").GetString()!);
    }

    private async Task InsertMedicalProblemRowAsync(int patientPid)
    {
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO lists (date, type, title, begdate, pid, activity) VALUES (NOW(), 'medical_problem', 'Fixture Condition', CURDATE(), @Pid, 1)",
            new { Pid = patientPid });
    }
}
