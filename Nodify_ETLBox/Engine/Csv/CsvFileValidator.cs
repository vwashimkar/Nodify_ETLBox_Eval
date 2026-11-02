using System.IO;
using System.Text;

namespace EtlDesigner.Engine.Csv;

public static class CsvFileValidator
{
    public static List<string> Validate(CsvFileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            messages.Add("File path is empty. Choose a CSV file path before loading preview.");
        }
        else
        {
            if (!File.Exists(settings.Path))
            {
                messages.Add("File was not found. Check the path or browse to an existing file.");
            }
            else
            {
                try
                {
                    using var stream = new FileStream(settings.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length == 0)
                    {
                        messages.Add("File is empty. Choose a CSV file that contains at least one row.");
                    }
                }
                catch (Exception)
                {
                    messages.Add("File cannot be opened. Close it in other programs and try again.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Separator))
        {
            messages.Add("Separator is empty. Enter one separator character or use the recognized name 'tab'.");
        }
        else
        {
            var separator = settings.Separator.Trim();
            var isTabName = string.Equals(separator, "tab", StringComparison.OrdinalIgnoreCase);
            if (separator.Length > 1 && !isTabName)
            {
                messages.Add("Separator is longer than one character. Use a single character or the recognized name 'tab'.");
            }
        }

        if (settings.SkipLines < 0)
        {
            messages.Add("Skip lines is below 0. Enter 0 or a positive number.");
        }

        if (settings.PreviewRows < 1 || settings.PreviewRows > 1000)
        {
            messages.Add("Preview rows is out of range. Enter a value from 1 to 1000.");
        }

        if (string.IsNullOrWhiteSpace(settings.EncodingName))
        {
            messages.Add("Encoding is empty. Enter a valid encoding name like utf-8 or windows-1252.");
        }
        else
        {
            try
            {
                _ = Encoding.GetEncoding(settings.EncodingName);
            }
            catch (Exception)
            {
                messages.Add("Encoding name is unknown. Enter a valid .NET encoding name like utf-8 or windows-1252.");
            }
        }

        return messages;
    }
}