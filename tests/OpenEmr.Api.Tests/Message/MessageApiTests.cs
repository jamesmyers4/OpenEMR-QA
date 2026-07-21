using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Message;

[Collection("OpenEmr API")]
public class MessageApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public MessageApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Message_With_Valid_Payload_Returns_Created_With_Mid()
    {
        var pid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await PostMessageAsync(pid, "Test 456");
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "response body was: {0}", raw);
        JsonDocument.Parse(raw).RootElement.GetProperty("mid").GetInt32().Should().BeGreaterThan(0, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Post_Message_Missing_Required_Field_Returns_BadRequest()
    {
        var pid = await CreateTestPatientAsync("Grace", "Hopper");
        var payload = new { body = "Test 456", groupname = "Default", from = "Matthew", title = "Other", message_status = "New" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'to' is required by MessageService::validate() and was omitted, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Message_Returns_NotFound()
    {
        var pid = await CreateTestPatientAsync("Katherine", "Johnson");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Message is a cud resource on this API — no GET route is registered at all, not even a single-record read, response body was: {0}", raw);
    }

    [Fact]
    public async Task Put_Message_Updates_Body_And_Appends_Rather_Than_Replaces()
    {
        var pid = await CreateTestPatientAsync("Rosalind", "Franklin");
        var mid = await CreateMessageAsync(pid, "Original body");
        var marker = $"UpdatedMarker{DateTime.UtcNow.Ticks}";
        var response = await PutMessageAsync(pid, mid, marker);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = await GetMessageBodyAsync(mid);
        body.Should().Contain("Original body", "MessageService::update() prepends the prior body rather than replacing it, response body was: {0}", raw);
        body.Should().Contain(marker, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Put_Message_With_Mismatched_Patient_Id_Still_Updates_Record()
    {
        var ownerPid = await CreateTestPatientAsync("Marie", "Curie");
        var otherPid = await CreateTestPatientAsync("Chien-Shiung", "Wu");
        var mid = await CreateMessageAsync(ownerPid, "Owner's original note");
        var marker = $"CrossPatientEdit{DateTime.UtcNow.Ticks}";
        var response = await PutMessageAsync(otherPid, mid, marker);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "MessageService::update() runs 'UPDATE pnotes SET ... WHERE id=?' with no pid filter at all, so the {{pid}} in the URL is never actually checked against the note's real owner, response body was: {0}", raw);
        var body = await GetMessageBodyAsync(mid);
        body.Should().Contain(marker, "the update succeeded against patient {0}'s note despite the request URL naming an unrelated patient {1}, response body was: {2}", ownerPid, otherPid, raw);
    }

    [Fact]
    public async Task Put_Message_For_Nonexistent_Mid_Still_Returns_Ok()
    {
        var pid = await CreateTestPatientAsync("Barbara", "McClintock");
        var response = await PutMessageAsync(pid, 999999999, "Ghost edit");
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "sqlStatement() returns a truthy statement handle regardless of affected row count, so MessageService::update() reports success even when the WHERE clause matched zero rows, response body was: {0}", raw);
    }

    [Fact]
    public async Task Delete_Message_Soft_Deletes_Record()
    {
        var pid = await CreateTestPatientAsync("Dorothy", "Hodgkin");
        var mid = await CreateMessageAsync(pid, "To be deleted");
        var response = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message/{mid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var deleted = await GetMessageDeletedFlagAsync(mid);
        deleted.Should().BeTrue("MessageService::delete() sets pnotes.deleted=1 rather than removing the row, response body was: {0}", raw);
    }

    [Fact]
    public async Task Delete_Message_With_Mismatched_Patient_Id_Returns_OkButDoesNotDelete()
    {
        var ownerPid = await CreateTestPatientAsync("Lise", "Meitner");
        var otherPid = await CreateTestPatientAsync("Rachel", "Carson");
        var mid = await CreateMessageAsync(ownerPid, "Should survive the wrong-pid delete");
        var response = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{otherPid}/message/{mid}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "unlike update(), delete() does run 'WHERE pid=? AND id=?', but responseHandler() still reports the same 200/true success regardless of whether any row actually matched, response body was: {0}", raw);
        var deleted = await GetMessageDeletedFlagAsync(mid);
        deleted.Should().BeFalse("the delete matched zero rows because the pid in the URL did not own this note, yet the API response gave no indication that nothing happened, response body was: {0}", raw);
    }

    [Fact]
    public async Task Delete_Message_For_Nonexistent_Mid_Still_Returns_Ok()
    {
        var pid = await CreateTestPatientAsync("Jane", "Goodall");
        var response = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message/999999999"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "sqlStatement() returns a truthy statement handle regardless of affected row count, so a delete of a mid that never existed still reports success, response body was: {0}", raw);
    }

    private async Task<int> CreateTestPatientAsync(string first, string last)
    {
        var payload = new { fname = first, lname = $"{last}{DateTime.UtcNow.Ticks}", DOB = "1985-05-05", sex = "Female" };
        var response = await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"), payload, ExactCasing);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"fixture patient creation should succeed, response was: {raw}");
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        return data.GetProperty("pid").GetInt32();
    }

    private async Task<HttpResponseMessage> PostMessageAsync(int pid, string bodyText)
    {
        var payload = new { body = bodyText, groupname = "Default", from = "Matthew", to = "admin", title = "Other", message_status = "New" };
        return await _fixture.Client.PostAsJsonAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/message"), payload, ExactCasing);
    }

    private async Task<int> CreateMessageAsync(int pid, string bodyText)
    {
        var response = await PostMessageAsync(pid, bodyText);
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

    private async Task<bool> GetMessageDeletedFlagAsync(int mid)
    {
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        var deleted = await connection.QuerySingleAsync<int>("SELECT deleted FROM pnotes WHERE id = @Id", new { Id = mid });
        return deleted == 1;
    }
}
