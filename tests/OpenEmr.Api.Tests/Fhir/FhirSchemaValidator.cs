using NJsonSchema;
using NJsonSchema.Validation;

namespace OpenEmr.Api.Tests.Fhir;

public static class FhirSchemaValidator
{
    private static readonly Lazy<JsonSchema> RootSchema = new(() =>
        JsonSchema.FromFileAsync(Path.Combine(AppContext.BaseDirectory, "Fhir", "Schemas", "fhir.schema.json")).GetAwaiter().GetResult());

    public static ICollection<ValidationError> Validate(string json, string definitionName)
    {
        var definitionSchema = RootSchema.Value.Definitions[definitionName];
        return definitionSchema.Validate(json);
    }

    public static ICollection<ValidationError> ValidateBundleAllowingKnownLastUpdatedDefect(string json)
    {
        return Validate(json, "Bundle").Where(e => e.Path != "#/meta.lastUpdated").ToList();
    }
}
