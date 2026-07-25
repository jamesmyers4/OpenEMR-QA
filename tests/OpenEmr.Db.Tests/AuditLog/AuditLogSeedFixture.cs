using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Dapper;
using Xunit;

namespace OpenEmr.Db.Tests.AuditLog;

public class AuditLogSeedFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .AddEnvironmentVariables(prefix: "OPENEMR_")
            .Build();

        var baseUrl = config["OpenEmrApi:BaseUrl"] ?? throw new InvalidOperationException("Missing OpenEmrApi:BaseUrl");
        var siteId = config["OpenEmrApi:SiteId"] ?? "default";
        var adminUser = config["OpenEmrApi:AdminUser"] ?? throw new InvalidOperationException("Missing OpenEmrApi:AdminUser");
        var adminPassword = config["OpenEmrApi:AdminPassword"] ?? throw new InvalidOperationException("Missing OpenEmrApi:AdminPassword");
        var clientName = config["OpenEmrApi:ClientName"] ?? "OpenEmr.Db.Tests.AuditLogSeed";
        var scope = config["OpenEmrApi:Scope"] ?? throw new InvalidOperationException("Missing OpenEmrApi:Scope");
        var dbConnectionString = config["OpenEmrDb:ConnectionString"] ?? throw new InvalidOperationException("Missing OpenEmrDb:ConnectionString");

        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        var registrationPayload = new
        {
            application_type = "private",
            client_name = clientName,
            redirect_uris = new[] { "https://localhost/apis" },
            token_endpoint_auth_method = "client_secret_post",
            grant_types = new[] { "password", "refresh_token" },
            scope
        };
        var registrationResponse = await client.PostAsJsonAsync($"/oauth2/{siteId}/registration", registrationPayload);
        registrationResponse.EnsureSuccessStatusCode();
        var registrationBody = await registrationResponse.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = registrationBody.GetProperty("client_id").GetString()!;
        var clientSecret = registrationBody.GetProperty("client_secret").GetString()!;

        await using (var connection = new MySqlConnection(dbConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("UPDATE oauth_clients SET is_enabled = 1 WHERE client_id = @ClientId", new { ClientId = clientId });
        }

        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["username"] = adminUser,
            ["password"] = adminPassword,
            ["scope"] = scope,
            ["user_role"] = "users"
        };
        var tokenResponse = await client.PostAsync($"/oauth2/{siteId}/token", new FormUrlEncodedContent(tokenForm));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokenBody.GetProperty("access_token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var patientPayload = new
        {
            fname = "AuditSeed",
            lname = $"AuditSeed{DateTime.UtcNow.Ticks}",
            DOB = "1975-03-15",
            sex = "Female"
        };
        var patientOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };
        var patientResponse = await client.PostAsJsonAsync($"/apis/{siteId}/api/patient", patientPayload, patientOptions);
        patientResponse.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
