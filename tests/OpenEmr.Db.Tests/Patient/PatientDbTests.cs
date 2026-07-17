using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.Patient;

[Collection("OpenEmr DB")]
public class PatientDbTests
{
    private readonly DbConnectionFixture _fixture;

    public PatientDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Demo_Seed_Data_Contains_Patients()
    {
        var count = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM patient_data");
        count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Patient_Pid_Values_Are_Unique()
    {
        var rows = await _fixture.Connection.QueryAsync<int>("SELECT pid FROM patient_data");
        var pids = rows.ToList();
        pids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Active_Patients_Have_Required_Demographic_Fields()
    {
        var sql = "SELECT pid, fname, lname, DOB FROM patient_data WHERE fname IS NULL OR lname IS NULL OR DOB IS NULL";
        var incomplete = await _fixture.Connection.QueryAsync(sql);
        incomplete.Should().BeEmpty();
    }

    [Fact]
    public async Task Appointments_Do_Not_Reference_Orphaned_Patient_Ids()
    {
        var sql = @"
            SELECT e.pc_eid
            FROM openemr_postcalendar_events e
            LEFT JOIN patient_data p ON e.pc_pid = p.pid
            WHERE e.pc_pid > 0 AND p.pid IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_Insert_Is_Immediately_Readable_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var insertSql = "INSERT INTO patient_data (fname, lname, DOB, sex) VALUES (@FirstName, @LastName, @Dob, @Sex)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { FirstName = "Margaret", LastName = "Hamilton", Dob = "1936-08-17", Sex = "Female" }, transaction);
        var pid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT pid FROM patient_data WHERE lname = @LastName", new { LastName = "Hamilton" }, transaction);
        pid.Should().BeGreaterThan(0);
        await transaction.RollbackAsync();
    }
}
