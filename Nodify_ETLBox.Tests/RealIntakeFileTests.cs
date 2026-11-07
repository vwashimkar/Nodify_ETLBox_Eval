using System.IO;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The real instrument files must stay byte for byte as copied: their truth counts their lines.</summary>
public class RealIntakeFileTests
{
    public static readonly string Folder = Path.Combine(IntakeTestData.Folder, "real");

    [Theory]
    [InlineData("r01_luminex_xponent.csv", 3969)]
    [InlineData("r02_moldev_softmax_pro_utf16.txt", 10240)]
    [InlineData("r03_perkinelmer_envision.csv", 6027)]
    [InlineData("r04_quantstudio.txt", 2659)]
    [InlineData("r05_agilent_gen5.txt", 1307)]
    [InlineData("r06_bmg_pherastar.csv", 1176)]
    [InlineData("r07_ctl_immunospot.txt", 4932)]
    [InlineData("r08_revvity_kaleido.csv", 3790)]
    [InlineData("r09_qiacuity_dpcr.csv", 3093)]
    [InlineData("r10_methodical_mind.txt", 3893)]
    [InlineData("r11_roche_cedex_bioht.txt", 1849)]
    [InlineData("r12_msd_workbench.csv", 4052)]
    public void RealFile_HasItsOriginalSize(string name, long size)
    {
        var actual = new FileInfo(Path.Combine(Folder, name)).Length;

        Assert.True(actual == size,
            $"{name} is {actual} bytes, not {size}. Git probably changed its line ends: check that .gitattributes has "
            + "'Nodify_ETLBox/SampleData/TestData/intake/real/** -text', then check the file out again.");
    }
}
