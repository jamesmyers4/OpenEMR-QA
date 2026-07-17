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
            lname = "Lovelace",
            DOB = "1990-01-01",
            sex = "Female"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("pid").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Patient_List_Returns_Seeded_Demo_Patients()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Patient_By_Pid_Returns_Matching_Record()
    {
        var created = await CreateTestPatientAsync("Grace", "Hopper");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{created}"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("lname").GetString().Should().Be("Hopper");
    }

    [Fact]
    public async Task Update_Patient_Persists_Changed_Fields()
    {
        var created = await CreateTestPatientAsync("Katherine", "Johnson");
        var update = new { city = "Knoxville" };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{created}"), update);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{created}"));
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("city").GetString().Should().Be("Knoxville");
    }

    [Fact]
    public async Task Fhir_Patient_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Patient"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("resourceType").GetString().Should().Be("Bundle");
    }

    [Fact]
    public async Task Create_Patient_Missing_Required_Field_Returns_BadRequest()
    {
        var payload = new { fname = "NoLastName" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<int> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = last, DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("pid").GetInt32();
    }
}
