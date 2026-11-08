namespace EtlDesigner.Engine.Intake;

/// <summary>The names a user sees for a container's format. The format names in the code stay short and lower case.</summary>
public static class ContainerDisplay
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["text"] = "Text",
        ["json"] = "JSON",
        ["json.badgerfish"] = "JSON (BadgerFish)",
        ["json-lines"] = "JSON lines",
        ["xml"] = "XML",
        ["xml.soap"] = "XML (SOAP)",
        ["csv"] = "CSV table",
        ["tsv"] = "TSV table",
        ["keyvalue"] = "Key-value",
        ["yaml"] = "YAML",
        ["fixedwidth"] = "Fixed-width table"
    };

    /// <summary>Every label key a format name can be overridden by, with its default name. A SOAP version is added to the soap name.</summary>
    public static IReadOnlyDictionary<string, string> DefaultNames { get; } = Defaults.ToDictionary(p => "container.format." + p.Key, p => p.Value);

    /// <summary>
    /// The name a user sees, for example "CSV table" or "XML (SOAP 1.2)". The marker-mode names
    /// badgerfish-json and soap give the same names as the detector's sub-types. A format name
    /// without a default is shown as it is. <paramref name="labels"/> can override a name by the
    /// key <c>container.format.&lt;format&gt;</c> or <c>container.format.&lt;format&gt;.&lt;subtype&gt;</c>.
    /// </summary>
    public static string Format(ContainerInfo info, Labels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        var (format, subType) = info.Format switch
        {
            "badgerfish-json" => ("json", "badgerfish"),
            "soap" => ("xml", "soap"),
            _ => (info.Format, info.SubType)
        };

        var key = subType is null ? format : $"{format}.{subType}";
        if (labels?.Find("container.format." + key) is { } label)
        {
            return label;
        }

        if (!Defaults.TryGetValue(key, out var name))
        {
            // An unknown sub-type keeps the name of its format.
            return Defaults.TryGetValue(format, out var formatName) ? labels?.Find("container.format." + format) ?? formatName : info.Format;
        }

        return key == "xml.soap" && info.SubTypeVersion is { Length: > 0 } version ? $"XML (SOAP {version})" : name;
    }
}
