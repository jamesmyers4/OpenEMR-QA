using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.GreyArea;

[Collection("OpenEmr API")]
public class ConcurrencyApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public ConcurrencyApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Concurrent_Patient_Creates_Race_On_Computed_Pid_And_Report_False_Success_On_Collision()
    {
        const int concurrentRequests = 15;
        var tasks = Enumerable.Range(0, concurrentRequests).Select(i => CreatePatientRawAsync($"PidRace{i}_{DateTime.UtcNow.Ticks}")).ToArray();
        var results = await Task.WhenAll(tasks);
        var statuses = string.Join(",", results.Select(r => (int)r.Status));
        var created = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        var falseSuccesses = results.Where(r => r.Status == HttpStatusCode.OK && r.Raw.Contains("Duplicate entry") && r.Raw.Contains("for key 'pid'")).ToList();
        created.Should().NotBeEmpty("at least some of the concurrent creates should succeed, statuses were: {0}", statuses);
        falseSuccesses.Should().NotBeEmpty("patient_data.pid is computed via an unguarded 'SELECT MAX(pid)+1' with no locking (PatientService::insert()), so concurrent requests reliably compute the same next pid; the resulting MySqlException is caught by the global DB error handler which never overrides the default 200, so a request that inserted nothing looks identical to a success by status code alone, statuses were: {0}", statuses);
        created.Count.Should().BeLessThan(concurrentRequests, "the collisions above should mean fewer than every concurrent request actually resulted in a real patient row, demonstrating genuine data loss under concurrency rather than a harmless race that self-resolves, statuses were: {0}", statuses);
        var createdPids = created.Select(r => JsonDocument.Parse(r.Raw).RootElement.GetProperty("data").GetProperty("pid").GetInt32()).ToList();
        createdPids.Should().OnlyHaveUniqueItems("the DB's own unique index on pid (confirmed via SHOW INDEX FROM patient_data) at least prevents two readable patient rows from ever sharing a pid, even though the losing request's failure is misreported as success");
    }

    [Fact]
    public async Task Concurrent_Puts_To_Same_Message_Lose_Updates_Under_Race()
    {
        const int concurrentRequests = 10;
        var pid = await CreateTestPatientAsync("Ada", "MessageRace");
        var mid = await CreateMessageAsync(pid, "Original body");
        var tasks = Enumerable.Range(1, concurrentRequests).Select(i => PutMessageAsync(pid, mid, $"Marker_{i}")).ToArray();
        var responses = await Task.WhenAll(tasks);
        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK, "MessageService::update() reports 200 regardless of what actually happened, matching the existing lost-write findings in FINDINGS.md");
        var finalBody = await GetMessageBodyAsync(mid);
        var survivingMarkers = Enumerable.Range(1, concurrentRequests).Count(i => finalBody.Contains($"Marker_{i}"));
        survivingMarkers.Should().BeLessThan(concurrentRequests, "MessageService::update() reads the existing body then writes existing+new with no locking around the read-modify-write, so concurrent PUTs to the same mid should lose some updates entirely rather than every one being durably appended, final body was: {0}", finalBody);
    }

    [Fact]
    public async Task Concurrent_Deletes_Of_Same_Appointment_All_Report_Success_Though_Only_One_Row_Existed()
    {
        const int concurrentRequests = 5;
        var pid = await CreateTestPatientAsync("Grace", "AppointmentRace");
        var eid = await CreateTestAppointmentAsync(pid);
        var tasks = Enumerable.Range(0, concurrentRequests).Select(_ => _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment/{eid}"))).ToArray();
        var responses = await Task.WhenAll(tasks);
        var raws = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        var combinedRaws = string.Join(" | ", raws);
        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK, "AppointmentService::deleteAppointmentRecord() returns whatever sqlStatement() hands back regardless of affected-row count, the same truthy-statement-handle pattern already confirmed on Message (FINDINGS.md #3), so every one of these concurrent deletes against a row that can only really be deleted once should still report 200, raw bodies were: {0}", combinedRaws);
        var remaining = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"));
        remaining.StatusCode.Should().Be(HttpStatusCode.NotFound, "only one row ever existed to delete, so after the race every appointment for this patient should genuinely be gone, matching the existing empty-list-becomes-404 pattern already confirmed on this resource");
    }

    private async Task<(HttpStatusCode Status, string Raw)> CreatePatientRawAsync(string uniqueSuffix)
    {
        var payload = new { fname = "Race", lname = uniqueSuffix, DOB = "1990-01-01", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw);
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

    private async Task<int> CreateTestAppointmentAsync(int pid)
    {
        var payload = new
        {
            pc_catid = "5",
            pc_title = "Office Visit",
            pc_duration = "900",
            pc_hometext = "Grey-area concurrency test appointment",
            pc_apptstatus = "-",
            pc_eventDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd"),
            pc_startTime = "09:00",
            pc_facility = "1",
            pc_billing_location = "1",
            pc_aid = "1"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/appointment"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<int> CreateMessageAsync(int pid, string bodyText)
    {
        var payload = new { body = bodyText, groupname = "Default", from = "Matthew", to = "admin", title = "Other", message_status = "New" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw).RootElement.GetProperty("mid").GetInt32();
    }

    private async Task<HttpResponseMessage> PutMessageAsync(int pid, int mid, string bodyText)
    {
        var payload = new { body = bodyText, groupname = "Default", from = "Matthew", to = "admin", title = "Other", message_status = "Done" };
        return await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message/{mid}"), payload, ExactCasing);
    }

    private async Task<string> GetMessageBodyAsync(int mid)
    {
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<string>("SELECT body FROM pnotes WHERE id = @Id", new { Id = mid });
    }
}
