namespace OpenEmr.Api.Tests.Fixtures;

public static class OpenEmrEndpoints
{
    public static string Rest(string siteId, string resourcePath) => $"/apis/{siteId}/api/{resourcePath.TrimStart('/')}";

    public static string Fhir(string siteId, string resourcePath) => $"/apis/{siteId}/fhir/{resourcePath.TrimStart('/')}";
}
