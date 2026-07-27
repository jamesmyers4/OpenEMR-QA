using Xunit;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.GreyArea;

[Collection("OpenEmr DB")]
public class ConcurrencyDbTests
{
    private readonly DbConnectionFixture _fixture;

    public ConcurrencyDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Concurrent_Raw_Inserts_Reusing_The_Same_Computed_Next_Pid_Are_Rejected_By_A_Real_Unique_Index()
    {
        await using var connectionA = new MySqlConnection(_fixture.ConnectionString);
        await using var connectionB = new MySqlConnection(_fixture.ConnectionString);
        await connectionA.OpenAsync();
        await connectionB.OpenAsync();
        var nextPidA = await connectionA.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 1 FROM patient_data");
        var nextPidB = await connectionB.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 1 FROM patient_data");
        nextPidB.Should().Be(nextPidA, "both connections read patient_data's MAX(pid) before either has inserted, reproducing the exact race window PatientService::insert() leaves open under real concurrent traffic (see the corresponding API-layer test in ConcurrencyApiTests.cs)");
        var insertSql = "INSERT INTO patient_data (pid, fname, lname, DOB, sex) VALUES (@Pid, @FirstName, @LastName, @Dob, @Sex)";
        await connectionA.ExecuteAsync(insertSql, new { Pid = nextPidA, FirstName = "RaceA", LastName = $"DbRaceA{DateTime.UtcNow.Ticks}", Dob = "1990-01-01", Sex = "Female" });
        var duplicateInsert = async () => await connectionB.ExecuteAsync(insertSql, new { Pid = nextPidB, FirstName = "RaceB", LastName = $"DbRaceB{DateTime.UtcNow.Ticks}", Dob = "1990-01-01", Sex = "Female" });
        var assertion = await duplicateInsert.Should().ThrowAsync<MySqlException>("pid has a real unique index at the DB level (confirmed via SHOW INDEX FROM patient_data), so the second insert reusing a stale MAX(pid)+1 value should be rejected outright rather than silently letting two rows share one pid");
        assertion.Which.Message.Should().Contain("Duplicate entry", "this is the same 'Duplicate entry ... for key pid' failure the REST API surfaces - and mishandles by reporting it as a 200 - whenever two concurrent POST /api/patient requests race on the same computed next pid");
        await connectionA.ExecuteAsync("DELETE FROM patient_data WHERE pid = @Pid", new { Pid = nextPidA });
    }
}
