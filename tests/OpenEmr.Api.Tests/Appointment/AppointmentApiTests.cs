using Xunit;
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
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public AppointmentApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Create_Appointment_Returns_Created_With_New_Eid()
    {
        var pid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await CreateTestAppointmentAsync(pid, "09:00", DateTime.UtcNow.AddDays(3));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_Appointments_For_Patient_Returns_Only_That_Patients_Visits()
    {
        var pid = await CreateTestPatientAsync("Grace", "Hopper");
        await CreateTestAppointmentAsync(pid, "11:00", DateTime.UtcNow.AddDays(4));
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.ValueKind.Should().Be(JsonValueKind.Array, "response body was: {0}", raw);
        body.GetArrayLength().Should().BeGreaterThan(0, "response body was: {0}", raw);
        body.EnumerateArray().Should().OnlyContain(item => item.GetProperty("pid").GetInt32() == pid, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Double_Booking_Same_Slot_Same_Provider_Is_Allowed_With_Distinct_Ids()
    {
        var pid = await CreateTestPatientAsync("Katherine", "Johnson");
        var first = await CreateTestAppointmentAsync(pid, "10:00", DateTime.UtcNow.AddDays(5));
        var firstRaw = await first.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", firstRaw);
        var firstId = JsonDocument.Parse(firstRaw).RootElement.GetProperty("id").GetInt32();
        var second = await CreateTestAppointmentAsync(pid, "10:00", DateTime.UtcNow.AddDays(5));
        var secondRaw = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", secondRaw);
        var secondId = JsonDocument.Parse(secondRaw).RootElement.GetProperty("id").GetInt32();
        secondId.Should().NotBe(firstId, "response body was: {0}", secondRaw);
    }

    [Fact]
    public async Task Fhir_Appointment_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Appointment"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle");
    }

    private async Task<int> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        return body.GetProperty("data").GetProperty("pid").GetInt32();
    }

    private async Task<HttpResponseMessage> CreateTestAppointmentAsync(int pid, string startTime, DateTime eventDate)
    {
        var payload = new
        {
            pc_catid = "5",
            pc_title = "Office Visit",
            pc_duration = "900",
            pc_hometext = "Automated test appointment",
            pc_apptstatus = "-",
            pc_eventDate = eventDate.ToString("yyyy-MM-dd"),
            pc_startTime = startTime,
            pc_facility = "1",
            pc_billing_location = "1",
            pc_aid = "1"
        };
        return await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"), payload, ExactCasing);
    }
}