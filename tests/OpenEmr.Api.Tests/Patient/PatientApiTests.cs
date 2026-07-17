using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Patient;

[Collection("OpenEmr API")]
public class PatientApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public PatientApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Patient_Returns_Created_With_New_Pid()
    {
        var payload = new
        {
            fname = "Ada",
            lname = UniqueName("Lovelace"),
            DOB = "1990-01-01",
            sex = "Female"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("pid").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Patient_List_Returns_Seeded_Demo_Patients()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Patient_By_Pid_Returns_Matching_Record()
    {
        var (_, uuid, lastName) = await CreateTestPatientAsync("Grace", "Hopper");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{uuid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("lname").GetString().Should().Be(lastName);
    }

    [Fact]
    public async Task Update_Patient_Persists_Changed_Fields()
    {
        var (_, uuid, _) = await CreateTestPatientAsync("Katherine", "Johnson");
        var update = new { city = "Knoxville" };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{uuid}"), update, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{uuid}"));
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("city").GetString().Should().Be("Knoxville");
    }

    [Fact]
    public async Task Fhir_Patient_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Patient"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle");
    }

    [Fact]
    public async Task Create_Patient_Missing_Required_Field_Returns_BadRequest()
    {
        var payload = new { fname = "NoLastName" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "response body was: {0}", raw);
    }

    private async Task<(int Pid, string Uuid, string LastName)> CreateTestPatientAsync(string first, string last)
    {
        var uniqueLastName = UniqueName(last);
        var payload = new { fname = first, lname = uniqueLastName, DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        var data = body.GetProperty("data");
        return (data.GetProperty("pid").GetInt32(), data.GetProperty("uuid").GetString()!, uniqueLastName);
    }

    private static string UniqueName(string baseName) => $"{baseName}{DateTime.UtcNow.Ticks}";
}