using Xunit;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using OpenEmr.Api.Tests.Fixtures;
using OpenEmr.Api.Tests.Fhir;

namespace OpenEmr.Api.Tests.Observation;

[Collection("OpenEmr API")]
public class ObservationApiTests
{
    private readonly OAuthTokenFixture _fixture;

    public ObservationApiTests(OAuthTokenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Fhir_Observation_Search_Returns_Valid_Bundle()
    {
        var response = await _fixture.Client.GetAsync(OpenEmrEndpoints.Fhir(_fixture.Options.SiteId, "Observation"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body was: {0}", raw);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("resourceType").GetString().Should().Be("Bundle", "response body was: {0}", raw);
        FhirSchemaValidator.ValidateBundleAllowingKnownLastUpdatedDefect(raw).Should().BeEmpty(
            "the response should conform to the official FHIR R4 JSON schema aside from the known Bundle.meta.lastUpdated defect (date() with no timezone in FhirResourcesService::createBundle(), see FINDINGS.md), response body was: {0}", raw);
    }
}
