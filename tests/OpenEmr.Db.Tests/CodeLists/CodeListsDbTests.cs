using Xunit;
using Dapper;
using FluentAssertions;
using OpenEmr.Db.Tests.Fixtures;

namespace OpenEmr.Db.Tests.CodeLists;

[Collection("OpenEmr DB")]
public class CodeListsDbTests
{
    private readonly DbConnectionFixture _fixture;

    public CodeListsDbTests(DbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Cvx_Reference_Code_Set_Is_Populated()
    {
        var sql = @"
            SELECT COUNT(*) FROM codes
            WHERE code_type = (SELECT ct_id FROM code_types WHERE ct_key = 'CVX')";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().BeGreaterThan(0, "the immunization vaccine dropdown is backed by the standard external CVX code set loaded into 'codes'/'code_types', not by 'list_options' as the resource name might suggest");
    }

    [Fact]
    public async Task Immunization_Cvx_Codes_Resolve_To_Real_Cvx_Reference_Codes()
    {
        var sql = @"
            SELECT i.id
            FROM immunizations i
            LEFT JOIN codes c ON c.code = CAST(i.cvx_code AS UNSIGNED) AND c.code_type = (SELECT ct_id FROM code_types WHERE ct_key = 'CVX')
            WHERE i.cvx_code IS NOT NULL AND i.cvx_code != '' AND c.id IS NULL";
        var orphaned = await _fixture.Connection.QueryAsync(sql);
        orphaned.Should().BeEmpty("every stored immunization should reference a real CVX code, not an arbitrary string");
    }

    [Fact]
    public async Task Allergy_Issue_List_Reference_Options_Are_Populated()
    {
        var sql = "SELECT COUNT(*) FROM list_options WHERE list_id = 'allergy_issue_list'";
        var count = await _fixture.Connection.ExecuteScalarAsync<int>(sql);
        count.Should().BeGreaterThan(0, "this is the reference list the allergy-type dropdown/autocomplete is seeded from");
    }
}
