using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace OpenEmr.Db.Tests.Fixtures;

public class DbConnectionFixture : IAsyncLifetime
{
    public string ConnectionString { get; }
    public MySqlConnection Connection { get; }

    public DbConnectionFixture()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .AddEnvironmentVariables(prefix: "OPENEMR_")
            .Build();
        ConnectionString = config["OpenEmrDb:ConnectionString"] ?? throw new InvalidOperationException("Missing OpenEmrDb:ConnectionString");
        Connection = new MySqlConnection(ConnectionString);
    }

    public async Task InitializeAsync()
    {
        await Connection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
    }
}

[CollectionDefinition("OpenEmr DB")]
public class OpenEmrDbCollection : ICollectionFixture<DbConnectionFixture>
{
}
