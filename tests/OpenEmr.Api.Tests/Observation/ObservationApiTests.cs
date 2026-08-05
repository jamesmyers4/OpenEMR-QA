using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;
using OpenEmr.Api.Tests.Fhir;

namespace OpenEmr.Api.Tests.Observation;

[Collection("OpenEmr API")]
public class ObservationApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public ObservationApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Fhir_Observation_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Observation"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle", "response body was: {0}", raw);
        FhirSchemaValidator.ValidateBundleAllowingKnownLastUpdatedDefect(raw).Should().BeEmpty(
            "the response should conform to the official FHIR R4 JSON schema aside from the known Bundle.meta.lastUpdated defect (date() with no timezone in FhirResourcesService::createBundle(), see FINDINGS.md), response body was: {0}", raw);
    }

    [Fact]
    public async Task Fhir_Observation_Search_500s_As_Soon_As_Any_Vitals_Record_Exists_Due_To_Missing_Uuid_Mapping_Backfill()
    {
        var (pid, puuid) = await CreateTestPatientAsync("Florence", "Vitals");
        var eid = await CreateTestEncounterAsync(puuid);
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        long formVitalsId = 0;
        long formsId = 0;
        try
        {
            formVitalsId = await connection.QuerySingleAsync<long>(
                @"INSERT INTO form_vitals (date, pid, `user`, groupname, authorized, activity, bps, bpd, weight, height, temperature, temp_method, pulse, respiration, BMI, waist_circ, head_circ, oxygen_saturation, last_updated)
                  VALUES (NOW(), @Pid, 'admin', 'Default', 1, 1, '130', '80', 220, 70, 98.6, 'Oral', 60, 20, 31.6, 37, 22.2, 96, NOW());
                  SELECT LAST_INSERT_ID();",
                new { Pid = pid });
            formsId = await connection.QuerySingleAsync<long>(
                @"INSERT INTO forms (date, encounter, form_name, form_id, pid, `user`, groupname, authorized, formdir)
                  VALUES (NOW(), @Encounter, 'Vitals', @FormVitalsId, @Pid, 'admin', 'Default', 1, 'vitals');
                  SELECT LAST_INSERT_ID();",
                new { Encounter = eid, FormVitalsId = formVitalsId, Pid = pid });

            var patientScoped = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, $"Observation?patient={puuid}&category=vital-signs"));
            patientScoped.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
                "FhirObservationVitalsService::parseVitalsIntoObservationRecords() calls UuidRegistry::uuidToString($uuidMappings[$code]) with no isset/empty guard (unlike the panel-code branch just above it, which does guard), and nothing in this codebase ever populates the uuid_mapping rows a form_vitals record needs - UuidRegistry::populateAllMissingUuids() is the only code path that would, and it has zero callers anywhere in the application, confirmed via a full-codebase grep. This crashes with an uncaught TypeError (Ramsey\\Uuid\\Uuid::fromBytes(): Argument #1 ($bytes) must be of type string, null given), see FINDINGS.md");

            var unfiltered = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Observation"));
            unfiltered.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
                "this is not a narrowly-scoped defect - the same missing uuid_mapping crash fires for a completely unfiltered Observation search too, as soon as any vitals record exists anywhere in the database, breaking the resource for every patient, not just the one with vitals data");
        }
        finally
        {
            if (formsId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM forms WHERE id = @Id", new { Id = formsId });
            }
            if (formVitalsId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM form_vitals WHERE id = @Id", new { Id = formVitalsId });
            }
        }
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

    private async Task<int> CreateTestEncounterAsync(string puuid)
    {
        var payload = new { pc_catid = "5", class_code = "AMB" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/encounter"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture encounter creation should succeed, response was: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").GetProperty("encounter").GetInt32();
    }
}
