using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlDesigner.Engine.Intake;

/// <summary>Intake settings and results as JSON: indented, camelCase, enums as strings. An <see cref="IntakeItem"/> holds a stream factory and is not serialized.</summary>
public static class IntakeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(IntakeSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static string Serialize(IntakeResult result) => JsonSerializer.Serialize(result, Options);

    public static string Serialize(IntakeValidationResult result) => JsonSerializer.Serialize(result, Options);

    public static IntakeValidationResult DeserializeValidation(string json)
        => JsonSerializer.Deserialize<IntakeValidationResult>(json, Options)
           ?? throw new JsonException("The JSON holds no validation result.");

    public static IntakeSettings DeserializeSettings(string json)
        => JsonSerializer.Deserialize<IntakeSettings>(json, Options) ?? new IntakeSettings();

    public static IntakeResult DeserializeResult(string json)
        => JsonSerializer.Deserialize<IntakeResult>(json, Options)
           ?? throw new JsonException("The JSON holds no intake result.");
}
