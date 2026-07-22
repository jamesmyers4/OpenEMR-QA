using Xunit;
using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.ReferentialIntegrity;

[Collection("OpenEmr DB")]
public class ReferentialIntegrityDbTests
{
    private readonly DbConnectionFixture _fixture;

    public ReferentialIntegrityDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Insurance_Data_And_Billing_Have_No_Declared_Foreign_Key_Constraints()
    {
        var sql = @"
            SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
            AND TABLE_NAME IN ('insurance_data', 'billing')";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().Be(0, "both tables are InnoDB (which supports FKs) but referential integrity here is purely application-enforced, never a DB-level constraint");
    }

    [Fact]
    public async Task Insurance_Data_Does_Not_Reference_Orphaned_Patient_Ids()
    {
        var sql = @"
            SELECT i.id
            FROM insurance_data i
            LEFT JOIN patient_data p ON i.pid = p.pid
            WHERE i.pid > 0 AND p.pid IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty();
    }

    [Fact]
    public async Task Billing_Does_Not_Reference_Orphaned_Encounters()
    {
        var sql = @"
            SELECT b.id
            FROM billing b
            LEFT JOIN form_encounter fe ON b.encounter = fe.encounter AND b.pid = fe.pid
            WHERE b.encounter IS NOT NULL AND b.pid IS NOT NULL AND fe.id IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_Insert_Insurance_Data_For_Real_Patient_Passes_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var realPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT pid FROM patient_data ORDER BY pid LIMIT 1", transaction: transaction);
        var insertSql = "INSERT INTO insurance_data (pid, type, provider) VALUES (@Pid, 'primary', 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = realPid }, transaction);
        var orphanCheckSql = @"
            SELECT i.id
            FROM insurance_data i
            LEFT JOIN patient_data p ON i.pid = p.pid
            WHERE i.pid = @Pid AND p.pid IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { Pid = realPid }, transaction);
        orphaned.Should().BeEmpty("the row references a real patient, so it must not be flagged by the orphan-detection query");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Insurance_Data_For_Nonexistent_Patient_Is_Flagged_By_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var bogusPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 100000 FROM patient_data", transaction: transaction);
        var insertSql = "INSERT INTO insurance_data (pid, type, provider) VALUES (@Pid, 'primary', 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = bogusPid }, transaction);
        var orphanCheckSql = @"
            SELECT i.id
            FROM insurance_data i
            LEFT JOIN patient_data p ON i.pid = p.pid
            WHERE i.pid = @Pid AND p.pid IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { Pid = bogusPid }, transaction);
        orphaned.Should().NotBeEmpty("there is no FK constraint on insurance_data.pid, so the DB itself happily accepts a reference to a patient that does not exist");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Billing_Row_For_Real_Encounter_Passes_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var realPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT pid FROM patient_data ORDER BY pid LIMIT 1", transaction: transaction);
        const long encounterId = 555555555;
        await _fixture.Connection.ExecuteAsync(
            "INSERT INTO form_encounter (date, reason, facility, pid, encounter, provider_id) VALUES (NOW(), 'Referential Integrity Fixture', 'Fixture Facility', @Pid, @Encounter, 1)",
            new { Pid = realPid, Encounter = encounterId }, transaction);
        var realEncounter = (Pid: (long)realPid, Encounter: encounterId);
        var insertSql = "INSERT INTO billing (pid, encounter, provider_id) VALUES (@Pid, @Encounter, 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { realEncounter.Pid, realEncounter.Encounter }, transaction);
        var orphanCheckSql = @"
            SELECT b.id
            FROM billing b
            LEFT JOIN form_encounter fe ON b.encounter = fe.encounter AND b.pid = fe.pid
            WHERE b.pid = @Pid AND b.encounter = @Encounter AND fe.id IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { realEncounter.Pid, realEncounter.Encounter }, transaction);
        orphaned.Should().BeEmpty("the row references a real encounter for the same patient, so it must not be flagged by the orphan-detection query");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Billing_Row_For_Nonexistent_Encounter_Is_Flagged_By_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        const long bogusEncounter = 999999999;
        var bogusPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 100000 FROM patient_data", transaction: transaction);
        var insertSql = "INSERT INTO billing (pid, encounter, provider_id) VALUES (@Pid, @Encounter, 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = bogusPid, Encounter = bogusEncounter }, transaction);
        var orphanCheckSql = @"
            SELECT b.id
            FROM billing b
            LEFT JOIN form_encounter fe ON b.encounter = fe.encounter AND b.pid = fe.pid
            WHERE b.pid = @Pid AND b.encounter = @Encounter AND fe.id IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { Pid = bogusPid, Encounter = bogusEncounter }, transaction);
        orphaned.Should().NotBeEmpty("there is no FK constraint on billing.encounter/pid, so the DB itself happily accepts a reference to an encounter that does not exist");
        await transaction.RollbackAsync();
    }
}
