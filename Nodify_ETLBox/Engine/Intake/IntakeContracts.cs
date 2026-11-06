using System.IO;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Reads and writes configuration by key (plan section 10): node labels, marker presets, detector
/// thresholds and layout templates. First implementation later: text and JSON files in a config
/// folder; after that SQLite. No constants for these values in new code.
/// </summary>
public interface IConfigStore
{
    /// <summary>The text stored under a key, or null when there is none.</summary>
    string? ReadText(string key);

    void WriteText(string key, string text);
}

/// <summary>
/// Keeps the unchanged original of every intake (plan section 10). Keyed by SHA-256, so the same
/// file is stored once. First implementation later: a folder with a manifest; after that SQLite,
/// synchronised to a central database by an outbox. Originals are never modified.
/// </summary>
public interface IRawArchive
{
    /// <summary>Stores the item and gives its archive id.</summary>
    string Store(IntakeItem item);

    /// <summary>Opens a stored original as a read-only stream.</summary>
    Stream Open(string archiveId);
}

/// <summary>
/// Records what happened in each run (plan section 10): input id and hash, containers, which layout
/// and definition produced which output, counts, times, user and tool version. This is the lineage
/// and data quality metadata DMBOK asks for. First implementation later: one JSON file per run.
/// </summary>
public interface ILineageLog
{
    void Record(IntakeResult result);
}
