using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OpenEmr.Api.Tests.Fixtures;

public class OpenEmrOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string SiteId { get; set; } = "default";
    public string AdminUser { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

public class OAuthTokenFixture : IAsyncLifetime
{
    public OpenEmrOptions Options { get; }
    public HttpClient Client { get; }
    public string AccessToken { get; private set; } = string.Empty;

    public OAuthTokenFixture()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .AddEnvironmentVariables(prefix: "OPENEMR_")
            .Build();
        Options = config.GetSection("OpenEmr").Get<OpenEmrOptions>() ?? new OpenEmrOptions();
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        Client = new HttpClient(handler) { BaseAddress = new Uri(Options.BaseUrl) };
    }

    public async Task InitializeAsync()
    {
        var clientId = await RegisterClientAsync();
        AccessToken = await RequestPasswordGrantTokenAsync(clientId);
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> RegisterClientAsync()
    {
        var registrationUrl = $"/oauth2/{Options.SiteId}/registration";
        var payload = new
        {
            application_type = "private",
            client_name = Options.ClientName,
            redirect_uris = new[] { "https://localhost/apis" },
            token_endpoint_auth_method = "client_secret_post",
            grant_types = new[] { "password", "refresh_token" },
            scope = Options.Scope
        };
        var response = await Client.PostAsJsonAsync(registrationUrl, payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("client_id").GetString()!;
    }

    private async Task<string> RequestPasswordGrantTokenAsync(string clientId)
    {
        var tokenUrl = $"/oauth2/{Options.SiteId}/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = Options.AdminUser,
            ["password"] = Options.AdminPassword,
            ["scope"] = Options.Scope,
            ["user_role"] = "users"
        };
        var response = await Client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}

[CollectionDefinition("OpenEmr API")]
public class OpenEmrApiCollection : ICollectionFixture<OAuthTokenFixture>
{
}
