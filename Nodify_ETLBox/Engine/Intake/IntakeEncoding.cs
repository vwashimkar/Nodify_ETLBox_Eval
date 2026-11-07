using System.Text;
using System.Text.Unicode;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// The encodings a user can choose for an intake (<see cref="IntakeSettings.Encoding"/>). "auto"
/// lets a BOM decide; without a BOM the file is UTF-8 unless its bytes are not valid UTF-8, and
/// then windows-1252 is a guess. A BOM always decides, also over an explicit setting [U: chosen].
/// </summary>
public static class IntakeEncodings
{
    public const string Auto = "auto";

    public const string Utf8 = "utf-8";

    public const string Windows1252 = "windows-1252";

    public const string Latin1 = "iso-8859-1";

    public const string Utf16Le = "utf-16le";

    public const string Utf16Be = "utf-16be";

    public static IReadOnlyList<string> Names { get; } = [Auto, Utf8, Windows1252, Latin1, Utf16Le, Utf16Be];

    public const string GuessReason = "The file is not valid UTF-8, so it is read as Windows-1252 (a guess).";

    public static readonly string UnknownMessage = $"The encoding name is not known. Choose one of: {string.Join(", ", Names)}.";

    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    /// <summary>Makes windows-1252 available. Safe to call often and from several threads.</summary>
    public static void EnsureRegistered() => _ = Registered.Value;

    /// <summary>The encoding of a fixed name (not "auto"), or null for an unknown name. Names ignore case.</summary>
    public static Encoding? Get(string? name)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case Utf8:
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            case Windows1252:
                EnsureRegistered();
                return Encoding.GetEncoding(1252);
            case Latin1:
                return Encoding.Latin1;
            case Utf16Le:
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            case Utf16Be:
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            default:
                return null;
        }
    }

    public static bool IsAuto(string? name) => string.Equals(name?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>A known name in its lower-case form, or null.</summary>
    public static string? Normalize(string? name)
    {
        var lower = name?.Trim().ToLowerInvariant();
        return Names.Contains(lower) ? lower : null;
    }
}

/// <summary>
/// The encoding of one read while it runs. The line indexer gives it every block before the block's
/// lines, so <see cref="Current"/> covers all bytes read so far; the detector and the marker scanner
/// decode with it. In Auto mode it turns from UTF-8 to windows-1252 at the first invalid UTF-8
/// sequence, so lines decoded before that block were decoded as UTF-8; for ASCII lines this is the
/// same text [U: chosen; a one-pass read cannot know the end of the file earlier].
/// </summary>
internal sealed class EncodingTracker
{
    private readonly Utf8Check _check = new();
    private readonly Encoding? _fixed;
    private Encoding? _bom;
    private string? _bomName;

    private EncodingTracker(string setting, Encoding? fixedEncoding)
    {
        Setting = setting;
        _fixed = fixedEncoding;
    }

    /// <summary>The tracker for a setting, or null with a plain message for an unknown name.</summary>
    public static EncodingTracker? For(string? setting, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(setting) || IntakeEncodings.IsAuto(setting))
        {
            return new EncodingTracker(IntakeEncodings.Auto, null);
        }

        var fixedEncoding = IntakeEncodings.Get(setting);
        if (fixedEncoding is null)
        {
            error = IntakeEncodings.UnknownMessage;
            return null;
        }

        return new EncodingTracker(IntakeEncodings.Normalize(setting)!, fixedEncoding);
    }

    public string Setting { get; }

    public bool IsAuto => _fixed is null;

    /// <summary>A UTF-16 setting with no BOM in the file: the indexer reads two-byte units from the first byte.</summary>
    public bool? FixedUtf16BigEndian => _fixed is UnicodeEncoding ? Setting == IntakeEncodings.Utf16Be : null;

    public bool InvalidUtf8Seen => _check.InvalidSeen;

    /// <summary>True when Auto chose windows-1252 because the bytes are not valid UTF-8.</summary>
    public bool Guessed => IsAuto && _bom is null && _check.InvalidSeen;

    /// <summary>The encoding of the bytes read so far.</summary>
    public Encoding Current => _bom ?? _fixed ?? (_check.InvalidSeen ? IntakeEncodings.Get(IntakeEncodings.Windows1252)! : Utf8NoBom);

    /// <summary>The encoding of a line's head: the indexer converts UTF-16 lines to UTF-8.</summary>
    public Encoding HeadEncoding => Current is UnicodeEncoding ? Utf8NoBom : Current;

    public string Name => _bomName ?? (_fixed is not null ? Setting : _check.InvalidSeen ? IntakeEncodings.Windows1252 : IntakeEncodings.Utf8);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public void SetBom(Encoding encoding, string name) => (_bom, _bomName) = (encoding, name);

    /// <summary>Checks the next bytes of a one-byte read (no BOM): UTF-8 validity, carried across blocks.</summary>
    public void Append(ReadOnlySpan<byte> bytes) => _check.Append(bytes);

    public void Finish() => _check.Finish();
}

/// <summary>
/// An incremental UTF-8 validity check. A sequence cut by a block boundary is kept (at most three
/// bytes) and completed by the next block, so memory does not grow with the file. Once an invalid
/// sequence is seen the check stops.
/// </summary>
internal sealed class Utf8Check
{
    private readonly byte[] _pending = new byte[4];
    private int _pendingLength;
    private int _pendingNeeded;

    public bool InvalidSeen { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (InvalidSeen)
        {
            return;
        }

        if (_pendingLength > 0)
        {
            var take = Math.Min(_pendingNeeded - _pendingLength, bytes.Length);
            bytes[..take].CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            bytes = bytes[take..];
            if (_pendingLength < _pendingNeeded)
            {
                return;
            }

            InvalidSeen = !Utf8.IsValid(_pending.AsSpan(0, _pendingLength));
            _pendingLength = 0;
            if (InvalidSeen)
            {
                return;
            }
        }

        var tail = IncompleteTail(bytes, out var needed);
        if (!Utf8.IsValid(bytes[..^tail]))
        {
            InvalidSeen = true;
            return;
        }

        if (tail > 0)
        {
            bytes[^tail..].CopyTo(_pending);
            (_pendingLength, _pendingNeeded) = (tail, needed);
        }
    }

    /// <summary>A sequence still open at the end of the file is invalid.</summary>
    public void Finish()
    {
        if (_pendingLength > 0)
        {
            InvalidSeen = true;
            _pendingLength = 0;
        }
    }

    /// <summary>How many bytes at the end start a sequence that needs more bytes, and its full length.</summary>
    private static int IncompleteTail(ReadOnlySpan<byte> bytes, out int needed)
    {
        needed = 0;
        for (var k = 1; k <= Math.Min(3, bytes.Length); k++)
        {
            var b = bytes[^k];
            if ((b & 0xC0) == 0x80)
            {
                continue;
            }

            needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > k ? k : 0;
        }

        return 0;
    }
}
