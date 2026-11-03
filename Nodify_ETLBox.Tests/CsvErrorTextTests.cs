using EtlDesigner.Engine.Csv;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CsvErrorTextTests
{
    [Fact]
    public void TryGetRowNumber_ExtractsRowNumber_FromExpectedProbeText()
    {
        const string text = "Bad data found in csv file Row: 7 - Start Position: 483 -- RawRecord: 6,Unclosed,\"Quote,unclosed6@demo.io";

        var ok = CsvErrorText.TryGetRowNumber(text, out var rowNumber);

        Assert.True(ok);
        Assert.Equal(7, rowNumber);
    }
}
