using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task Concurrent_Puts_To_Same_PatientInsurance_Record_Let_The_Last_Writer_Erase_Every_Other_Concurrent_Update()
    {
        var puuid = await CreateTestPatientUuidAsync("Katherine", "InsuranceRace");
        var uuid = await CreateFullTestInsuranceAsync(puuid);
        var fieldValues = new Dictionary<string, string>
        {
            ["policy_number"] = "RaceValue_policy_number",
            ["subscriber_lname"] = "RaceValue_subscriber_lname",
            ["subscriber_fname"] = "RaceValue_subscriber_fname",
            ["subscriber_city"] = "RaceValue_subscriber_city",
            ["subscriber_state"] = "NY"
        };
        var tasks = fieldValues.Select(kvp => PutInsuranceSingleFieldAsync(puuid, uuid, kvp.Key, kvp.Value)).ToArray();
        var responses = await Task.WhenAll(tasks);
        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.OK, "InsuranceRestController::put() reports 200 for every one of these concurrent full-column overwrites regardless of which one actually stuck");
        var verify = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/{uuid}"));
        var verifyRaw = await verify.Content.ReadAsStringAsync();
        var verifyBody = JsonDocument.Parse(verifyRaw).RootElement.GetProperty("data");
        var survivingFields = fieldValues.Where(kvp => verifyBody.GetProperty(kvp.Key).ValueKind == JsonValueKind.String && verifyBody.GetProperty(kvp.Key).GetString() == kvp.Value).ToList();
        survivingFields.Should().HaveCount(1, "InsuranceService::update() runs an unconditional full-column overwrite with no locking (FINDINGS.md #4), so under real concurrent PUTs to the same record, each request's write completely erases every other concurrent request's field rather than merging them - only whichever write physically commits last should still be visible, response body was: {0}", verifyRaw);
    }

    [Fact]
    public async Task Concurrent_Practitioner_Creates_With_Identical_Username_All_Succeed_With_No_Uniqueness_Guard()
    {
        const int concurrentRequests = 5;
        var sharedUsername = $"racedupe{DateTime.UtcNow.Ticks}";
        var npiBase = DateTime.UtcNow.Ticks.ToString().Substring(0, 9);
        try
        {
            var tasks = Enumerable.Range(0, concurrentRequests).Select(i => CreatePractitionerRawAsync(sharedUsername, $"{npiBase}{i}", i)).ToArray();
            var results = await Task.WhenAll(tasks);
            var statuses = string.Join(",", results.Select(r => (int)r.Status));
            results.Select(r => r.Status).Should().OnlyContain(status => status == HttpStatusCode.Created, "users.username has no unique constraint at the DB level (confirmed via SHOW INDEX FROM users, see UsersDbTests.cs), so PractitionerService::insert() has nothing to reject a colliding username against - unlike the Patient pid race, there is no guard here for a collision to even sometimes fail against, statuses were: {0}", statuses);
            var ids = results.Select(r => JsonDocument.Parse(r.Raw).RootElement.GetProperty("data").GetProperty("id").GetInt32()).ToList();
            ids.Should().OnlyHaveUniqueItems("every concurrent create should still produce its own distinct real user row, just one that happens to share a username with the others");
            var listResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "practitioner"));
            var listRaw = await listResponse.Content.ReadAsStringAsync();
            var matchingUsernames = JsonDocument.Parse(listRaw).RootElement.GetProperty("data").EnumerateArray().Count(item => item.GetProperty("username").GetString() == sharedUsername);
            matchingUsernames.Should().Be(concurrentRequests, "all {0} concurrently-created practitioners should be independently visible and indistinguishable from one another by username alone, response body was: {1}", concurrentRequests, listRaw);
        }
        finally
        {
            await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM users WHERE username = @Username", new { Username = sharedUsername });
        }
    }

    [Fact]
    public async Task Concurrent_Document_Uploads_To_Same_Existing_Category_All_Succeed_And_Stay_Independently_Listable()
    {
        const int concurrentRequests = 10;
        var pid = await CreateTestPatientAsync("Rosalind", "DocumentRace");
        var filenames = Enumerable.Range(0, concurrentRequests).Select(i => $"docrace_{i}_{DateTime.UtcNow.Ticks}.txt").ToArray();
        var tasks = filenames.Select(filename => UploadDocumentRawAsync(pid, "medicalrecord", filename, $"content for {filename}")).ToArray();
        var results = await Task.WhenAll(tasks);
        var statuses = string.Join(",", results.Select(r => (int)r.Status));
        results.Select(r => r.Status).Should().OnlyContain(status => status == HttpStatusCode.OK, "Document::createDocument() generates documents.id via ADOdb's GenID() sequence mechanism (an atomic 'UPDATE sequences SET id=LAST_INSERT_ID(id+1)'), unlike patient_data.pid's unguarded SELECT MAX(pid)+1 pattern, so every concurrent upload should succeed independently rather than colliding, statuses were: {0}", statuses);
        var listResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path=medicalrecord"));
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", listRaw);
        var listedFilenames = JsonDocument.Parse(listRaw).RootElement.EnumerateArray().Select(item => item.GetProperty("filename").GetString()).ToList();
        listedFilenames.Should().Contain(filenames, "each concurrent upload's categories_to_documents link row is a REPLACE INTO keyed by that upload's own distinct sequence-generated document_id, so none of the {0} concurrent uploads to this shared category should be able to overwrite or clobber another's link row, response body was: {1}", concurrentRequests, listRaw);
    }

    private async Task<(HttpStatusCode Status, string Raw)> UploadDocumentRawAsync(int pid, string path, string filename, string fileContent)
    {
        using var content = new MultipartFormDataContent();
        var fileBytes = System.Text.Encoding.UTF8.GetBytes(fileContent);
        var filePart = new ByteArrayContent(fileBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(filePart, "document", filename);
        var response = await _fixture.Client.PostAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path={Uri.EscapeDataString(path)}"), content);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw);
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

    private async Task<string> CreateTestPatientUuidAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        return body.GetProperty("data").GetProperty("uuid").GetString()!;
    }

    private async Task<string> CreateFullTestInsuranceAsync(string puuid)
    {
        var payload = new
        {
            type = "primary",
            provider = "1",
            policy_number = $"POL{DateTime.UtcNow.Ticks}",
            subscriber_lname = "OriginalLast",
            subscriber_fname = "OriginalFirst",
            subscriber_relationship = "spouse",
            subscriber_DOB = "1988-02-02",
            subscriber_street = "123 Test St",
            subscriber_postal_code = "54321",
            subscriber_city = "Testville",
            subscriber_state = "CA",
            subscriber_sex = "Female",
            accept_assignment = "TRUE",
            date = "2026-01-01",
            date_end = "2026-12-31"
        };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"fixture insurance creation should succeed, response was: {raw}");
        return JsonDocument.Parse(raw).RootElement.GetProperty("data").GetProperty("uuid").GetString()!;
    }

    private async Task<HttpResponseMessage> PutInsuranceSingleFieldAsync(string puuid, string uuid, string field, string value)
    {
        var payload = new Dictionary<string, string> { [field] = value };
        return await _fixture.Client.PutAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{puuid}/insurance/{uuid}"), payload, ExactCasing);
    }

    private async Task<(HttpStatusCode Status, string Raw)> CreatePractitionerRawAsync(string username, string npi, int index)
    {
        var payload = new { fname = $"Race{index}", lname = $"Prac{index}_{DateTime.UtcNow.Ticks}", npi, username };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "practitioner"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, raw);
    }
}
