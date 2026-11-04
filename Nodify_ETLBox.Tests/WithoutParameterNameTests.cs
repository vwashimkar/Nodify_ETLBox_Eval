using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class WithoutParameterNameTests
{
    [Fact]
    public void RemovesTheParameterSuffix()
        => Assert.Equal(
            "Level 1 must split once (key) and level 2 must split all (components).",
            ParseStudioViewModel.WithoutParameterName(
                new ArgumentException("Level 1 must split once (key) and level 2 must split all (components).", "levels").Message));

    [Theory]
    [InlineData("Load the preview first.")]
    [InlineData("Keep (this) text")]
    [InlineData("")]
    public void LeavesOtherTextAlone(string message)
        => Assert.Equal(message, ParseStudioViewModel.WithoutParameterName(message));

    [Fact]
    public async Task DialogShowsTheParseErrorWithoutTheSuffix()
    {
        var vm = await ParseStudioViewModelTests.LoadedC01ForOtherTests();
        vm.SourceColumn = "sample";
        vm.DelimiterLevels[0].Mode = EtlDesigner.Engine.Parsing.SplitMode.All;

        Assert.Equal("Level 1 must split once (key) and level 2 must split all (components).", vm.ComponentsError);

        vm.TestParsing();
        Assert.DoesNotContain("(Parameter", vm.TestError);
    }
}
