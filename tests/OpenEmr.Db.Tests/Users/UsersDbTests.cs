using Xunit;
using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.Users;

[Collection("OpenEmr DB")]
public class UsersDbTests
{
    private readonly DbConnectionFixture _fixture;

    public UsersDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Users_Table_Has_No_Duplicate_Populated_Usernames()
    {
        var sql = "SELECT username FROM users WHERE username IS NOT NULL GROUP BY username HAVING COUNT(*) > 1";
        var duplicates = await _fixture.Connection.QueryAsync<string>(sql);
        duplicates.Should().BeEmpty();
    }

    [Fact]
    public async Task Users_Table_Has_No_Unique_Constraint_On_Username()
    {
        var sql = "SHOW INDEX FROM users WHERE Column_name = 'username'";
        var indexes = await _fixture.Connection.QueryAsync(sql);
        indexes.Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_Insert_Duplicate_Username_Is_Accepted_By_Schema_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var username = $"dupcheck{DateTime.UtcNow.Ticks}";
        var insertSql = "INSERT INTO users (username, fname, lname, active) VALUES (@Username, @FirstName, @LastName, 1)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Username = username, FirstName = "First", LastName = "One" }, transaction);
        await _fixture.Connection.ExecuteAsync(insertSql, new { Username = username, FirstName = "Second", LastName = "Two" }, transaction);
        var count = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE username = @Username", new { Username = username }, transaction);
        count.Should().Be(2, "because {0}", "no unique constraint exists on users.username in this schema, so duplicate insertion is not rejected at the DB level");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Direct_Insert_Disabled_User_Is_Excluded_By_Login_Active_Predicate_Then_Rolled_Back()
    {
        await using var transaction = await _fixture.Connection.BeginTransactionAsync();
        var username = $"disabledcheck{DateTime.UtcNow.Ticks}";
        var insertSql = "INSERT INTO users (username, fname, lname, active) VALUES (@Username, @FirstName, @LastName, 0)";
        await _fixture.Connection.ExecuteAsync(insertSql, new { Username = username, FirstName = "Disabled", LastName = "User" }, transaction);
        var rowExists = await _fixture.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE username = @Username", new { Username = username }, transaction);
        rowExists.Should().Be(1, "the row itself must exist for this to be a meaningful negative check");
        var loginQuery = "SELECT `id`, `authorized`, `see_auth`, `active` FROM `users` WHERE BINARY `username` = @Username";
        var loginRow = await _fixture.Connection.QuerySingleOrDefaultAsync(loginQuery, new { Username = username }, transaction);
        ((object?)loginRow).Should().NotBeNull("AuthUtils.php's own login query finds the row regardless of active status - the active check happens after this SELECT");
        ((bool)loginRow!.active).Should().BeFalse("active = 0 is exactly the condition AuthUtils.php checks (`elseif ($userInfo['active'] != 1)`) to reject the login attempt");
        await transaction.RollbackAsync();
    }
}
