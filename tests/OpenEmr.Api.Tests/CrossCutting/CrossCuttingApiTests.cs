using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MySqlConnector;
using Dapper;
using OpenEmr.Api.Tests.Fixtures;

namespace OpenEmr.Api.Tests.CrossCutting;

[Collection("OpenEmr API")]
public class CrossCuttingApiTests
{
    private readonly OAuthTokenFixture _fixture;
    private static readonly JsonSerializerOptions ExactCasing = new() { PropertyNamingPolicy = null };

    public CrossCuttingApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Facility_List_With_No_Bearer_Token_Returns_Unauthorized()
    {
        using var anonymousClient = NewClient();
        var response = await anonymousClient.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Facility_List_With_Malformed_Bearer_Token_Returns_Unauthorized()
    {
        using var anonymousClient = NewClient();
        anonymousClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not.a.real.jwt");
        var response = await anonymousClient.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "response body was: {0}", raw);
    }

    [Fact]
    public async Task Token_Scoped_To_Patient_Read_Only_Cannot_Reach_Facility_Endpoint()
    {
        var (client, _) = await RegisterAndAuthenticateAsync("openid api:oemr user/patient.read");
        using (client)
        {
            var inScope = await client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"));
            var inScopeRaw = await inScope.Content.ReadAsStringAsync();
            inScope.StatusCode.Should().Be(HttpStatusCode.OK, "the token was granted user/patient.read, response body was: {0}", inScopeRaw);

            var outOfScope = await client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"));
            var outOfScopeRaw = await outOfScope.Content.ReadAsStringAsync();
            outOfScope.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the token was never granted user/facility.read, response body was: {0}", outOfScopeRaw);
        }
    }

    [Fact]
    public async Task Refresh_Token_Grant_Issues_A_New_Working_Access_Token()
    {
        var scope = "openid offline_access api:oemr user/patient.read";
        var (clientId, clientSecret) = await RegisterClientAsync(scope);
        await EnableClientAsync(clientId);
        using var client = NewClient();
        var initialToken = await RequestPasswordGrantTokenRawAsync(client, clientId, clientSecret, scope);
        var refreshToken = initialToken.GetProperty("refresh_token").GetString();
        refreshToken.Should().NotBeNullOrEmpty("the client was registered with the refresh_token grant type");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken!,
            ["scope"] = scope
        };
        var refreshResponse = await client.PostAsync($"/oauth2/{_fixture.Options.SiteId}/token", new FormUrlEncodedContent(form));
        var refreshRaw = await refreshResponse.Content.ReadAsStringAsync();
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", refreshRaw);
        var newAccessToken = JsonDocument.Parse(refreshRaw).RootElement.GetProperty("access_token").GetString();
        newAccessToken.Should().NotBeNullOrEmpty("response body was: {0}", refreshRaw);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newAccessToken);
        var verify = await client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient"));
        var verifyRaw = await verify.Content.ReadAsStringAsync();
        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the refreshed access token should work exactly like the original, response body was: {0}", verifyRaw);
    }

    [Fact]
    public async Task Create_Facility_With_Malformed_Json_Body_Returns_BadRequest_Not_ServerError()
    {
        var content = new StringContent("{\"name\": \"Broken\", ", System.Text.Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "facility"), content);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a truncated/invalid JSON body should fail validation cleanly, not crash the server, response body was: {0}", raw);
    }

    [Fact]
    public async Task Get_Patient_List_Honors_Limit_And_Offset_Pagination_Params()
    {
        var firstPage = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient?_offset=0&_limit=2"));
        var firstPageRaw = await firstPage.Content.ReadAsStringAsync();
        firstPage.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", firstPageRaw);
        var firstPageIds = JsonDocument.Parse(firstPageRaw).RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
        firstPageIds.Should().HaveCount(2, "_limit=2 should cap the result set to exactly two rows, response body was: {0}", firstPageRaw);

        var secondPage = await _fixture.Client.GetAsync(OpenEmrEndpoints.Rest(_fixture.Options.SiteId, "patient?_offset=2&_limit=2"));
        var secondPageRaw = await secondPage.Content.ReadAsStringAsync();
        secondPage.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", secondPageRaw);
        var secondPageIds = JsonDocument.Parse(secondPageRaw).RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
        secondPageIds.Should().HaveCount(2, "response body was: {0}", secondPageRaw);

        secondPageIds.Should().NotIntersectWith(firstPageIds, "advancing the offset by 2 should move to a genuinely different pair of rows, not repeat or ignore the offset");
    }

    private async Task<(HttpClient Client, string AccessToken)> RegisterAndAuthenticateAsync(string scope)
    {
        var (clientId, clientSecret) = await RegisterClientAsync(scope);
        await EnableClientAsync(clientId);
        var client = NewClient();
        var tokenBody = await RequestPasswordGrantTokenRawAsync(client, clientId, clientSecret, scope);
        var accessToken = tokenBody.GetProperty("access_token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return (client, accessToken);
    }

    private async Task<(string ClientId, string ClientSecret)> RegisterClientAsync(string scope)
    {
        using var client = NewClient();
        var registrationUrl = $"/oauth2/{_fixture.Options.SiteId}/registration";
        var payload = new
        {
            application_type = "private",
            client_name = $"{_fixture.Options.ClientName}.CrossCutting.{DateTime.UtcNow.Ticks}",
            redirect_uris = new[] { "https://localhost/apis" },
            token_endpoint_auth_method = "client_secret_post",
            grant_types = new[] { "password", "refresh_token" },
            scope
        };
        var response = await client.PostAsJsonAsync(registrationUrl, payload);
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"client registration should succeed, response was: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;
        return (body.GetProperty("client_id").GetString()!, body.GetProperty("client_secret").GetString()!);
    }

    private async Task EnableClientAsync(string clientId)
    {
        await using var connection = new MySqlConnection(_fixture.Options.DbConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("UPDATE oauth_clients SET is_enabled = 1 WHERE client_id = @ClientId", new { ClientId = clientId });
    }

    private async Task<JsonElement> RequestPasswordGrantTokenRawAsync(HttpClient client, string clientId, string clientSecret, string scope)
    {
        var tokenUrl = $"/oauth2/{_fixture.Options.SiteId}/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["username"] = _fixture.Options.AdminUser,
            ["password"] = _fixture.Options.AdminPassword,
            ["scope"] = scope,
            ["user_role"] = "users"
        };
        var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
        var raw = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"token request should succeed, response was: {raw}");
        return JsonDocument.Parse(raw).RootElement;
    }

    private HttpClient NewClient()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.Options.BaseUrl) };
    }
}
