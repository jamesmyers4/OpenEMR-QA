using Xunit;
using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.SoftDelete;

[Collection("OpenEmr DB")]
public class SoftDeleteDbTests
{
    private readonly DbConnectionFixture _fixture;

    public SoftDeleteDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Date_Deleted_Column_Does_Not_Exist_Anywhere_In_This_Schema()
    {
        var sql = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND column_name = 'date_deleted'";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().Be(0, "this OpenEMR version only tracks soft-delete via a `deleted` flag column, never a paired `date_deleted` timestamp");
    }

    [Fact]
    public async Task Documents_And_Pnotes_Are_The_Tables_With_A_Deleted_Flag()
    {
        var sql = "SELECT table_name FROM information_schema.columns WHERE table_schema = DATABASE() AND column_name = 'deleted'";
        var tables = (await _fixture.Connection.QueryAsync<string>(sql)).ToList();
        tables.Should().Contain("documents").And.Contain("pnotes");
    }

    [Fact]
    public async Task Direct_Insert_Soft_Deleted_Document_Is_Excluded_By_The_Api_Read_Predicate_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var nextId = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT IFNULL(MAX(id), 0) + 1 FROM documents", transaction: transaction);
        var name = $"softdeletecheck{DateTime.UtcNow.Ticks}.txt";
        var insertSql = "INSERT INTO documents (id, name, deleted, foreign_id, revision) VALUES (@Id, @Name, 1, 0, NOW())";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Id = nextId, Name = name }, transaction);
        var rowExists = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM documents WHERE name = @Name", new { Name = name }, transaction);
        rowExists.Should().Be(1, "the row itself must exist for this to be a meaningful negative check");
        var apiReadPredicateSql = "SELECT COUNT(*) FROM documents WHERE name = @Name AND deleted = 0";
        var visibleToApi = await _fixture.Connection.ExecuteScalarAsync<int>(apiReadPredicateSql, new { Name = name }, transaction);
        visibleToApi.Should().Be(0, "DocumentService::getAllAtPath() filters `doc.deleted = 0`, so a soft-deleted row is excluded from every API read despite still existing in the table");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Soft_Deleted_Pnote_Row_Correctly_Reflects_The_Deleted_Flag_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var body = $"softdeletecheck{DateTime.UtcNow.Ticks}";
        var insertSql = "INSERT INTO pnotes (body, pid, deleted) VALUES (@Body, 0, 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Body = body }, transaction);
        var deletedFlag = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT deleted FROM pnotes WHERE body = @Body", new { Body = body }, transaction);
        deletedFlag.Should().Be(1, "pnotes has no read/list route at the API layer, so this only verifies the flag itself is stored and readable at the DB layer, not that any API response excludes it");
        await transaction.RollbackAsync();
    }
}
