using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Appointment;

[Collection("OpenEmr API")]
public class AppointmentApiTests
{
    private readonly OAuthTokenFixture _fixture;

    public AppointmentApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Appointment_Returns_Created_With_New_Eid()
    {
        var payload = new
        {
            pc_eventDate = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd"),
            pc_startTime = "09:00:00",
            pc_duration = 900,
            pc_facility = 1,
            pc_catid = 5,
            pc_title = "Follow-up",
            pc_pid = 1
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "appointment"), payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("pc_eid").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Appointments_For_Patient_Returns_Only_That_Patients_Visits()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient/1/appointment"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var appointment in body.GetProperty("data").EnumerateArray())
        {
            appointment.GetProperty("pc_pid").GetString().Should().Be("1");
        }
    }

    [Fact]
    public async Task Double_Booking_Same_Slot_Same_Provider_Returns_Conflict()
    {
        var slot = new
        {
            pc_eventDate = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd"),
            pc_startTime = "10:00:00",
            pc_duration = 900,
            pc_facility = 1,
            pc_catid = 5,
            pc_title = "Intake",
            pc_pid = 1
        };
        var first = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "appointment"), slot);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "appointment"), slot);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Fhir_Appointment_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Appointment"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("resourceType").GetString().Should().Be("Bundle");
    }
}
