using EtlDesigner.Engine.Parsing;
using EtlDesigner.UI.ViewModels;
using Xunit;
using K = EtlDesigner.UI.ViewModels.SegmentKind;

namespace Nodify_ETLBox.Tests;

public class HighlightSegmentsTests
{
    private static readonly DelimiterLevel[] TwoLevels = [new(":", SplitMode.Once), new("|", SplitMode.All)];

    private static readonly DelimiterLevel[] ThreeLevels = [.. TwoLevels, new("/", SplitMode.Each, "revision")];

    private static (string, K)[] Of(string text, IReadOnlyList<DelimiterLevel> levels)
        => ParseStudioViewModel.HighlightSegments(text, levels).Select(s => (s.Text, s.Kind)).ToArray();

    [Fact]
    public void KeyAndTwoComponents()
        => Assert.Equal(
            [("S100", K.Key), (":", K.Level1Delimiter), ("compid1", K.Component), ("|", K.Level2Delimiter), ("compid2", K.Component)],
            Of("S100:compid1|compid2", TwoLevels));

    [Fact]
    public void Level3_SplitsEachComponent_MissingSubPartIsJustAComponent()
        => Assert.Equal(
            [("S1", K.Key), (":", K.Level1Delimiter), ("c1", K.Component), ("/", K.Level3Delimiter), ("r2", K.SubPart),
             ("|", K.Level2Delimiter), ("c2", K.Component)],
            Of("S1:c1/r2|c2", ThreeLevels));

    [Fact]
    public void KeepsSpacesAndEmptyItems_SoTheTextJoinsBack()
    {
        const string text = "S1: a | |b|";
        var segments = ParseStudioViewModel.HighlightSegments(text, TwoLevels);
        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
        Assert.Equal(3, segments.Count(s => s.Kind == K.Level2Delimiter));
    }

    [Fact]
    public void NoKeyDelimiter_WholeTextIsKey()
        => Assert.Equal([("abc|def", K.Key)], Of("abc|def", TwoLevels));

    [Fact]
    public void EmptyText_NoSegments()
        => Assert.Empty(ParseStudioViewModel.HighlightSegments("", TwoLevels));

    [Fact]
    public void InstanceOverload_UsesTheEditedLevels()
    {
        var vm = new ParseStudioViewModel(NodeFactory.CreateParseValue(new System.Windows.Point()), new EtlDesigner.UI.Services.ParseValueUpstream(null, null, null, null, "x"));
        vm.DelimiterLevels[1].Delimiter = ";";
        Assert.Equal(K.Level2Delimiter, vm.HighlightSegments("S1:a;b")[3].Kind);
    }
}
