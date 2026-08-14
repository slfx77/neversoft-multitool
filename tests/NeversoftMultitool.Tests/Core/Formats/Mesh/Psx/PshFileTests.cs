using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public class PshFileTests(TestPaths paths)
{
    private string? FindHawk2Psh()
    {
        if (!paths.HasSampleBuilds) return null;
        var files = Directory.GetFiles(paths.SampleBuildsDir!, "hawk2.psh",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true });
        return files.Length > 0 ? files[0] : null;
    }

    [Fact]
    public void Parse_Hawk2Psh_ReturnsCorrectBoneCount()
    {
        var pshPath = FindHawk2Psh();
        Assert.SkipWhen(pshPath == null, "hawk2.psh not found in sample builds");

        var psh = PshFile.Parse(pshPath!);

        Assert.NotNull(psh);
        // hawk2.psh has 19 entries (indices 0-18): pelvis, legs, torso, arms, head, board, wheels
        Assert.Equal(19, psh.Bones.Count);
    }

    [Fact]
    public void Parse_Hawk2Psh_PelvisIsRoot()
    {
        var pshPath = FindHawk2Psh();
        Assert.SkipWhen(pshPath == null, "hawk2.psh not found in sample builds");

        var psh = PshFile.Parse(pshPath!);
        Assert.NotNull(psh);

        var pelvis = psh.Bones.FirstOrDefault(b => b.Index == 0);
        Assert.NotNull(pelvis);
        Assert.Contains("pelvis", pelvis.Name);
        Assert.Null(pelvis.ParentName); // Scene Root → null
    }

    [Fact]
    public void Parse_Hawk2Psh_ChildBonesHaveCorrectParents()
    {
        var pshPath = FindHawk2Psh();
        Assert.SkipWhen(pshPath == null, "hawk2.psh not found in sample builds");

        var psh = PshFile.Parse(pshPath!);
        Assert.NotNull(psh);

        // Index 1 = right shoe, parent = right shin
        var shoe = psh.Bones.FirstOrDefault(b => b.Index == 1);
        Assert.NotNull(shoe);
        Assert.Contains("right_shoe", shoe.Name);
        Assert.NotNull(shoe.ParentName);
        Assert.Contains("right_shin", shoe.ParentName);

        // Index 16 = board, parent = Scene Root (null)
        var board = psh.Bones.FirstOrDefault(b => b.Index == 16);
        Assert.NotNull(board);
        Assert.Contains("board", board.Name);
        Assert.Null(board.ParentName);
    }

    [Fact]
    public void GetBoneName_ReturnsLowercasedName()
    {
        var pshPath = FindHawk2Psh();
        Assert.SkipWhen(pshPath == null, "hawk2.psh not found in sample builds");

        var psh = PshFile.Parse(pshPath!);
        Assert.NotNull(psh);

        var name = psh.GetBoneName(0);
        Assert.NotNull(name);
        Assert.Equal(name, name.ToLowerInvariant()); // All lowercase
        Assert.Contains("pelvis", name);
    }

    [Fact]
    public void FindCompanion_ForHawk2Psx_ReturnsPshFile()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Find a hawk2.PSX that has a companion hawk2.psh
        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "hawk2.psx",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true });
        Assert.SkipWhen(psxFiles.Length == 0, "hawk2.PSX not found");

        // Try each hawk2.PSX until we find one with a companion .psh
        PshFile? psh = null;
        foreach (var psxPath in psxFiles)
        {
            psh = PshFile.FindCompanion(psxPath);
            if (psh != null) break;
        }

        Assert.SkipWhen(psh == null, "No hawk2.psh companion found alongside hawk2.PSX");
        Assert.True(psh!.Bones.Count > 0);
    }

    [Fact]
    public void Parse_NonexistentFile_ReturnsNull()
    {
        var result = PshFile.Parse(Path.Combine(Path.GetTempPath(), "nonexistent", "fake.psh"));
        Assert.Null(result);
    }

    [Fact]
    public void Parse_NegativeOnlyBoneIndex_ReturnsNull()
    {
        var data = System.Text.Encoding.ASCII.GetBytes(
            "#define TESTPART_BAD -1\n" +
            "//   parent: Scene Root\n");

        Assert.Null(PshFile.Parse(data));
    }

    [Fact]
    public void Parse_MixedNegativeAndZeroIndices_KeepsOnlyZero()
    {
        var data = System.Text.Encoding.ASCII.GetBytes(
            "#define TESTPART_BAD -1\n" +
            "//   parent: Scene Root\n" +
            "#define TESTPART_ROOT 0\n" +
            "//   parent: Scene Root\n");

        var parsed = Assert.IsType<PshFile>(PshFile.Parse(data));

        var bone = Assert.Single(parsed.Bones);
        Assert.Equal("root", bone.Name);
        Assert.Equal(0, bone.Index);
        Assert.Null(bone.ParentName);
        Assert.Null(parsed.GetBoneName(-1));
    }

    [Fact]
    public void FindCompanion_FilenameOnlyPath_UsesCurrentDirectory()
    {
        var stem = $"nmt-psh-{Guid.NewGuid():N}";
        var companionName = stem + ".psh";
        var companionPath = Path.GetFullPath(companionName);

        try
        {
            File.WriteAllText(
                companionPath,
                "#define TESTPART_ROOT 0" + Environment.NewLine +
                "//   parent: Scene Root" + Environment.NewLine);

            var parsed = PshFile.FindCompanion(stem + ".psx");

            var bone = Assert.Single(Assert.IsType<PshFile>(parsed).Bones);
            Assert.Equal("root", bone.Name);
            Assert.Equal(0, bone.Index);
            Assert.Null(bone.ParentName);
        }
        finally
        {
            File.Delete(companionPath);
        }
    }
}
