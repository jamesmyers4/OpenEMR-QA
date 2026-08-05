using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.GreyArea;

[Collection("OpenEmr API")]
public class TimingBoundaryApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public TimingBoundaryApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Appointment_Duration_Crossing_Midnight_Stores_A_Time_Only_EndTime_With_No_Date_Rollover()
    {
        var pid = await CreateTestPatientAsync("Grace", "MidnightBoundary");
        var eventDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        var payload = new
        {
            pc_catid = "5",
            pc_title = "Overnight Visit",
            pc_duration = "7200",
            pc_hometext = "Timing-boundary test appointment crossing midnight",
            pc_apptstatus = "-",
            pc_eventDate = eventDate,
            pc_startTime = "23:00",
            pc_facility = "1",
            pc_billing_location = "1",
            pc_aid = "1"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var eid = JsonDocument.Parse(raw).RootElement.GetProperty("id").GetInt32();

        var getResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"appointment/{eid}"));
        var getRaw = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", getRaw);
        var record = JsonDocument.Parse(getRaw).RootElement.EnumerateArray().Single();

        var storedEventDate = record.GetProperty("pc_eventDate").GetString();
        var storedStartTime = record.GetProperty("pc_startTime").GetString();
        var storedEndTime = record.GetProperty("pc_endTime").GetString();

        storedEventDate.Should().Be(eventDate, "pc_eventDate is copied verbatim from the request and AppointmentService::insert() never adjusts it for a duration that pushes the end past midnight, response body was: {0}", getRaw);
        storedEndTime.Should().NotBeNull("response body was: {0}", getRaw);
        TimeOnly.Parse(storedEndTime!).Should().BeBefore(TimeOnly.Parse(storedStartTime!),
            "pc_endTime is a plain TIME column with no date component, and AppointmentService::insert() computes the real end DateTime correctly (23:00 + 2h = 01:00 the next day) but then stores only ->format('H:i:s') - discarding the date rollover entirely. The stored row for a 23:00 start with a 2-hour duration ends up with pc_startTime=23:00:00 and pc_endTime=01:00:00 on the same pc_eventDate, an end time that is literally earlier than its own start time with nothing in the schema to indicate the appointment actually spans two calendar days. Raw record was: {0}", record.GetRawText());
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
}
