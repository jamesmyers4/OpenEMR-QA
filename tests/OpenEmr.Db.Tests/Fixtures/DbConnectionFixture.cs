using Xunit;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Dapper;

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
        await SeedFixturePatientsAsync();
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
    }

    private async Task SeedFixturePatientsAsync()
    {
        var existing = await Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM patient_data WHERE lname IN ('Lovelace', 'Hopper', 'Johnson')");
        if (existing > 0) return;
        var nextPid = await Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 1 FROM patient_data");
        var fixtures = new[]
        {
            new { Pid = nextPid, FirstName = "Ada", LastName = "Lovelace", Dob = "1990-01-01", Sex = "Female" },
            new { Pid = nextPid + 1, FirstName = "Grace", LastName = "Hopper", Dob = "1985-05-05", Sex = "Female" },
            new { Pid = nextPid + 2, FirstName = "Katherine", LastName = "Johnson", Dob = "1980-08-26", Sex = "Female" }
        };
        var insertSql = "INSERT INTO patient_data (pid, fname, lname, DOB, sex) VALUES (@Pid, @FirstName, @LastName, @Dob, @Sex)";
        foreach (var fixture in fixtures)
        {
            await Connection.ExecuteAsync(insertSql, fixture);
        }
    }
}

[CollectionDefinition("OpenEmr DB")]
public class OpenEmrDbCollection : ICollectionFixture<DbConnectionFixture>
{
}