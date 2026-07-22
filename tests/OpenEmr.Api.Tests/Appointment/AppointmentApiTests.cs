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
    public async Task Cancel_Appointment_Deletes_It_And_Removes_It_From_The_Patient_List()
    {
        var pid = await CreateTestPatientAsync("Marie", "Curie");
        var created = await CreateTestAppointmentAsync(pid, "13:00", DateTime.UtcNow.AddDays(6));
        var createdRaw = await created.Content.ReadAsStringAsync();
        var eid = JsonDocument.Parse(createdRaw).RootElement.GetProperty("id").GetInt32();
        var deleteResponse = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment/{eid}"));
        var deleteRaw = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", deleteRaw);
        var listResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"));
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "the REST DELETE route performs a real hard delete (AppointmentService::deleteAppointmentRecord), unlike the UI's separate cancel flow which has its own unresolved bug; with the patient's only appointment gone, getAllForPatient returns an empty array, which RestControllerHelper::responseHandler treats as falsy and turns into a 404 rather than 200 with [] - the same empty-list-404 pattern already confirmed on the Document resource, response body was: {0}", listRaw);
    }

    [Fact]
    public async Task Update_Appointment_Has_No_Put_Route_Returns_NotFound()
    {
        var pid = await CreateTestPatientAsync("Rosalind", "Franklin");
        var created = await CreateTestAppointmentAsync(pid, "14:00", DateTime.UtcNow.AddDays(7));
        var createdRaw = await created.Content.ReadAsStringAsync();
        var eid = JsonDocument.Parse(createdRaw).RootElement.GetProperty("id").GetInt32();
        var payload = new { pc_startTime = "15:00" };
        var response = await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment/{eid}"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "AppointmentRestController has no put()/update() method and no PUT route is registered in _rest_routes.inc.php, confirmed by reading the source - reschedule/update is not possible through this REST API at all, response body was: {0}", raw);
    }

    [Fact]
    public async Task Create_Appointment_With_Recurrence_Fields_Ignores_Them_And_Creates_A_Single_Event()
    {
        var pid = await CreateTestPatientAsync("Chien-Shiung", "Wu");
        var payload = new
        {
            pc_catid = "5",
            pc_title = "Office Visit",
            pc_duration = "900",
            pc_hometext = "Automated test appointment",
            pc_apptstatus = "-",
            pc_eventDate = DateTime.UtcNow.AddDays(8).ToString("yyyy-MM-dd"),
            pc_startTime = "16:00",
            pc_facility = "1",
            pc_billing_location = "1",
            pc_aid = "1",
            pc_recurrtype = "1",
            pc_recurrspec = "a:1:{s:8:\"exdate\";s:0:\"\";}",
            pc_recurrfreq = "1",
            pc_enddate = DateTime.UtcNow.AddMonths(1).ToString("yyyy-MM-dd")
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var eid = JsonDocument.Parse(raw).RootElement.GetProperty("id").GetInt32();
        var getResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"appointment/{eid}"));
        var getRaw = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", getRaw);
        var getBody = JsonDocument.Parse(getRaw).RootElement;
        getBody.ValueKind.Should().Be(JsonValueKind.Array, "AppointmentService::getAppointment() always wraps its result in an array, even for a single eid lookup, unlike every other resource's single-record get, response body was: {0}", getRaw);
        var record = getBody.EnumerateArray().Single();
        record.TryGetProperty("pc_recurrtype", out _).Should().BeFalse("AppointmentService::insert() only reads a fixed set of fields from the request body and never touches pc_recurrtype/pc_recurrspec at all - creating a real recurring series is not possible through this REST API, response body was: {0}", getRaw);
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