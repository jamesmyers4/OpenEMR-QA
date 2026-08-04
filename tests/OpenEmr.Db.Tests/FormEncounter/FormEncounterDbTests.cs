using Xunit;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.FormEncounter;

[Collection("OpenEmr DB")]
public class FormEncounterDbTests
{
    private readonly DbConnectionFixture _fixture;

    public FormEncounterDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Form_Encounter_And_Patient_Data_Have_No_Declared_Foreign_Key_Constraint()
    {
        var sql = @"
            SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
            AND TABLE_NAME = 'form_encounter'";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().Be(0, "form_encounter is InnoDB (which supports FKs) but its pid/provider_id references are purely application-enforced, never a DB-level constraint");
    }

    [Fact]
    public async Task Form_Encounter_Does_Not_Reference_Orphaned_Patient_Ids()
    {
        var sql = @"
            SELECT fe.id
            FROM form_encounter fe
            LEFT JOIN patient_data p ON fe.pid = p.pid
            WHERE fe.pid IS NOT NULL AND fe.pid > 0 AND p.pid IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty("every real encounter row should belong to a real, still-existing patient");
    }

    [Fact]
    public async Task Form_Encounter_Does_Not_Reference_Orphaned_Provider_Ids()
    {
        var sql = @"
            SELECT fe.id
            FROM form_encounter fe
            LEFT JOIN users u ON fe.provider_id = u.id
            WHERE fe.provider_id IS NOT NULL AND fe.provider_id > 0 AND u.id IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty("provider_id = 0 means unassigned, but any nonzero value should point at a real users row");
    }

    [Fact]
    public async Task Direct_Insert_Form_Encounter_For_Real_Patient_And_Provider_Passes_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var realPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT pid FROM patient_data ORDER BY pid LIMIT 1", transaction: transaction);
        var realProviderId = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT id FROM users ORDER BY id LIMIT 1", transaction: transaction);
        var insertSql = "INSERT INTO form_encounter (date, reason, facility, pid, encounter, provider_id) VALUES (NOW(), 'Fixture Visit', 'Fixture Facility', @Pid, @Encounter, @ProviderId)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = realPid, Encounter = DateTime.UtcNow.Ticks, ProviderId = realProviderId }, transaction);
        var orphanCheckSql = @"
            SELECT fe.id
            FROM form_encounter fe
            LEFT JOIN patient_data p ON fe.pid = p.pid
            LEFT JOIN users u ON fe.provider_id = u.id
            WHERE fe.pid = @Pid AND fe.provider_id = @ProviderId AND (p.pid IS NULL OR u.id IS NULL)";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { Pid = realPid, ProviderId = realProviderId }, transaction);
        orphaned.Should().BeEmpty("the row references a real patient and a real provider, so it must not be flagged by either orphan-detection query");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Form_Encounter_For_Nonexistent_Patient_And_Provider_Is_Flagged_By_The_Orphan_Check_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var bogusPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(pid), 0) + 100000 FROM patient_data", transaction: transaction);
        var bogusProviderId = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(id), 0) + 100000 FROM users", transaction: transaction);
        var insertSql = "INSERT INTO form_encounter (date, reason, facility, pid, encounter, provider_id) VALUES (NOW(), 'Fixture Visit', 'Fixture Facility', @Pid, @Encounter, @ProviderId)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Pid = bogusPid, Encounter = DateTime.UtcNow.Ticks, ProviderId = bogusProviderId }, transaction);
        var orphanCheckSql = @"
            SELECT fe.id
            FROM form_encounter fe
            LEFT JOIN patient_data p ON fe.pid = p.pid
            LEFT JOIN users u ON fe.provider_id = u.id
            WHERE fe.pid = @Pid AND fe.provider_id = @ProviderId AND (p.pid IS NULL OR u.id IS NULL)";
        var orphaned = await _fixture.Connection.QueryAsync(orphanCheckSql, new { Pid = bogusPid, ProviderId = bogusProviderId }, transaction);
        orphaned.Should().NotBeEmpty("there is no FK constraint on form_encounter.pid/provider_id, so the DB itself happily accepts references to a patient and provider that do not exist");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Billing_Row_Cannot_Reference_A_Realistic_Sized_Form_Encounter_Encounter_Value_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var realPid = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT pid FROM patient_data ORDER BY pid LIMIT 1", transaction: transaction);
        var encounter = DateTime.UtcNow.Ticks;
        var insertEncounterSql = "INSERT INTO form_encounter (date, reason, facility, pid, encounter) VALUES (NOW(), 'Fixture Visit', 'Fixture Facility', @Pid, @Encounter)";
        await _fixture.Connection.ExecuteAsync(insertEncounterSql, new { Pid = realPid, Encounter = encounter }, transaction);
        var insertBillingSql = "INSERT INTO billing (pid, encounter, notecodes, revenue_code) VALUES (@Pid, @Encounter, '', '')";
        var insertBilling = async () => await _fixture.Connection.ExecuteAsync(insertBillingSql, new { Pid = realPid, Encounter = encounter }, transaction);
        var assertion = await insertBilling.Should().ThrowAsync<MySqlException>("form_encounter.encounter is bigint(20) and this project's own Encounter fixtures size it from DateTime.UtcNow.Ticks (18 digits), well beyond what billing.encounter's int(11) can hold (~2.1 billion) - a real column-width mismatch predating this project (FINDINGS.md #12), not something a graceful validation layer catches first");
        assertion.Which.Message.Should().Contain("Out of range value", "MariaDB rejects the value outright at the storage-engine level rather than truncating or coercing it, so a billing row genuinely cannot reference an encounter this large");
        await transaction.RollbackAsync();
    }
}
