using Xunit;
using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.AuditLog;

[Collection("OpenEmr DB")]
public class AuditLogDbTests
{
    private readonly DbConnectionFixture _fixture;

    public AuditLogDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Log_Table_Has_The_Expected_Audit_Columns()
    {
        var sql = "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'log'";
        var columns = (await _fixture.Connection.QueryAsync<string>(sql)).ToList();
        columns.Should().Contain(new[] { "date", "event", "category", "user", "patient_id", "comments" });
    }

    [Fact]
    public async Task No_Database_Triggers_Exist_Anywhere_In_This_Schema()
    {
        var sql = "SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE()";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().Be(0, "audit logging in this OpenEMR version is entirely PHP-mediated (EventAuditLogger::newEvent calls), never DB-trigger-enforced");
    }

    [Fact]
    public async Task Application_Mediated_Patient_Inserts_Are_Represented_In_The_Audit_Log()
    {
        var sql = "SELECT COUNT(*) FROM log WHERE event = 'patient-record-insert' AND category = 'Patient Demographics'";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().BeGreaterThan(0, "every patient created through the real application (API/UI) writes a corresponding row here, confirmed against this instance's existing log history");
    }

    [Fact]
    public async Task Direct_Sql_Insert_Into_Patient_Data_Produces_No_Audit_Log_Row_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var nextPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 1 FROM patient_data", transaction: transaction);
        var insertSql = "INSERT INTO patient_data (pid, fname, lname, DOB, sex) VALUES (@Pid, @FirstName, @LastName, @Dob, @Sex)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = nextPid, FirstName = "Direct", LastName = "SqlBypass", Dob = "1999-01-01", Sex = "Female" }, transaction);
        var logRowCount = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM log WHERE patient_id = @Pid", new { Pid = nextPid }, transaction);
        logRowCount.Should().Be(0, "there are no DB triggers on patient_data, so a raw SQL insert bypassing the OpenEMR application layer leaves no trace in the audit log at all - a real HIPAA-relevant gap, not a test artifact");
        await transaction.RollbackAsync();
    }
}
