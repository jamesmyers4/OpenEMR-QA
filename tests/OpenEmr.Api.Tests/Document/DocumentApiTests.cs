using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.Document;

[Collection("OpenEmr API")]
public class DocumentApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public DocumentApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Document_With_Normalized_Category_Path_Returns_OkTrue()
    {
        var pid = await CreateTestPatientAsync("Ada", "Lovelace");
        var response = await UploadDocumentAsync(pid, "medicalrecord", $"fixture{DateTime.UtcNow.Ticks}.txt", "fixture content");
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        raw.Trim().Should().Be("true", "DocumentService::insertAtPath() returns a bare boolean, not an object, so responseHandler() serializes the literal value true rather than an envelope, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Document_List_At_Normalized_Path_Returns_Uploaded_Document()
    {
        var pid = await CreateTestPatientAsync("Grace", "Hopper");
        var filename = $"fixture{DateTime.UtcNow.Ticks}.txt";
        await UploadDocumentAsync(pid, "medicalrecord", filename, "fixture content");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path=medicalrecord"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.ValueKind.Should().Be(JsonValueKind.Array, "the document list route returns a bare array at the root, not a {{\"data\": [...]}} envelope, response body was: {0}", raw);
        body.EnumerateArray().Should().Contain(item => item.GetProperty("filename").GetString() == filename, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Post_Document_With_HumanReadable_Category_Name_Uploads_But_Is_Never_Listable()
    {
        var pid = await CreateTestPatientAsync("Katherine", "Johnson");
        var filename = $"fixture{DateTime.UtcNow.Ticks}.txt";
        var uploadResponse = await UploadDocumentAsync(pid, "Medical Record", filename, "fixture content");
        var uploadRaw = await uploadResponse.Content.ReadAsStringAsync();
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the upload itself succeeds even though the category can never actually be resolved, response body was: {0}", uploadRaw);
        var listResponse = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path=Medical%20Record"));
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "DocumentService::getLastIdOfPath() compares the bound path parameter against 'replace(LOWER(name), ' ', '')' on the categories table but never lowercases or strips spaces from the parameter itself, so a human-readable category name like 'Medical Record' never resolves to a real category id, categories_to_documents is never populated by the upload, and the exact same path used for both calls can never find the record again, response body was: {0}", listRaw);
    }

    [Fact]
    public async Task Get_Document_By_Id_Downloads_File_Content()
    {
        var pid = await CreateTestPatientAsync("Rosalind", "Franklin");
        var filename = $"fixture{DateTime.UtcNow.Ticks}.txt";
        const string fileContent = "fixture file content for download";
        await UploadDocumentAsync(pid, "medicalrecord", filename, fileContent);
        var did = await FindDocumentIdAsync(pid, "medicalrecord", filename);
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document/{did}"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        response.Content.Headers.ContentDisposition!.FileName.Should().Be(filename, "response body was: {0}", raw);
        raw.Should().Be(fileContent, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Document_Download_By_Nonexistent_Id_Returns_BadRequest()
    {
        var pid = await CreateTestPatientAsync("Marie", "Curie");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document/999999999"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "DocumentRestController::downloadFile() falls back to a plain http_response_code(400) with no JSON body when DocumentService::getFile() finds nothing, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Document_List_At_Category_With_No_Documents_Returns_NotFound()
    {
        var pid = await CreateTestPatientAsync("Chien-Shiung", "Wu");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path=labreport"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "DocumentService::getAllAtPath() returns a plain PHP array, and an empty array is falsy, so RestControllerHelper::responseHandler() treats a real, valid, zero-result category the same as a hard failure and returns 404 instead of 200 with an empty list, response body was: {0}", raw);
    }

    [Fact]
    public async Task Post_Document_Missing_File_Returns_InternalServerError()
    {
        var pid = await CreateTestPatientAsync("Barbara", "McClintock");
        var response = await _fixture.Client.PostAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path=medicalrecord"), new StringContent(string.Empty));
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "DocumentService::insertAtPath() indexes $fileData['tmp_name'] with no isset() guard, so an absent $_FILES['document'] part crashes the request instead of returning a clean 400");
    }

    [Fact]
    public async Task Post_Document_Missing_Path_Query_Param_Returns_OkWithRawSqlErrorBody()
    {
        var pid = await CreateTestPatientAsync("Jane", "Goodall");
        var response = await UploadDocumentAsync(pid, path: null, filename: $"fixture{DateTime.UtcNow.Ticks}.txt", fileContent: "fixture content");
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "DocumentService::getLastIdOfPath() runs a parameterized query with a null bind value, which MariaDB rejects at prepare time, but the resulting fatal error is only ever echoed as raw HTML by the global DB error handler and never routed through http_response_code(), so the route still reports 200 despite leaking a full server file-path stack trace, response body was: {0}", raw);
        raw.Should().Contain("Query Error", "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Document_List_Missing_Path_Query_Param_Returns_OkWithRawSqlErrorBody()
    {
        var pid = await CreateTestPatientAsync("Rachel", "Carson");
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the same missing-bind-value SQL failure as the POST route affects GET /api/patient/{{pid}}/document when 'path' is omitted, response body was: {0}", raw);
        raw.Should().Contain("Query Error", "response body was: {0}", raw);
    }

    [Fact]
    public async Task Put_Document_Returns_NotFound()
    {
        var pid = await CreateTestPatientAsync("Dorothy", "Hodgkin");
        var response = await _fixture.Client.PutAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document/1"), new StringContent(string.Empty));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Document is a crs resource on this API — no PUT route is registered at all, so this fails the same route-not-found way the read-only trio's POST calls do, response body was: {0}", raw);
    }

    [Fact]
    public async Task Delete_Document_Returns_NotFound()
    {
        var pid = await CreateTestPatientAsync("Lise", "Meitner");
        var response = await _fixture.Client.DeleteAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document/1"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Document is a crs resource on this API — no DELETE route is registered at all, response body was: {0}", raw);
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

    private async Task<HttpResponseMessage> UploadDocumentAsync(int pid, string? path, string filename, string fileContent)
    {
        using var content = new MultipartFormDataContent();
        var fileBytes = System.Text.Encoding.UTF8.GetBytes(fileContent);
        var filePart = new ByteArrayContent(fileBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(filePart, "document", filename);
        var resourcePath = path is null ? $"patient/{pid}/document" : $"patient/{pid}/document?path={Uri.EscapeDataString(path)}";
        return await _fixture.Client.PostAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, resourcePath), content);
    }

    private async Task<int> FindDocumentIdAsync(int pid, string path, string filename)
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, $"patient/{pid}/document?path={path}"));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var match = body.EnumerateArray().First(item => item.GetProperty("filename").GetString() == filename);
        return match.GetProperty("id").GetInt32();
    }
}
