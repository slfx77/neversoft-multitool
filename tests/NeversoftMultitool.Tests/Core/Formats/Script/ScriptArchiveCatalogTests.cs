using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Script;

namespace NeversoftMultitool.Tests.Core.Formats.Script;

public sealed class ScriptArchiveCatalogTests
{
    [Fact]
    public void PickerPolicy_CoversEveryArchiveFileSystemRootAndCompoundArchives()
    {
        var extensions = ScriptArchiveCatalog.PickerExtensions;

        Assert.Contains(".wad", extensions);
        Assert.Contains(".ddx", extensions);
        Assert.Contains(".bon", extensions);
        Assert.Contains(".zip", extensions);
        Assert.Contains(".cut", extensions);
        Assert.Contains(".z64", extensions);
        Assert.Contains(".ps2", extensions);
        Assert.Contains(".xen", extensions);
        Assert.DoesNotContain(".iso", extensions);
    }

    [Fact]
    public void Open_UsesBreadthFirstArchiveSourcesAndPreservesDuplicateBasenames()
    {
        var disposalOrder = new List<string>();
        var root = new MemoryArchiveFileSystem("root.zip", ArchiveAssetType.Zip, disposalOrder);
        root.Add("scripts\\shared.qb.ps2", [0]);
        root.Add("audio.sqb.ngc", [0], "sound");
        root.Add("ignored.qb.tmp", [0]);
        var innerEntry = root.Add("inner.pre", []);

        var child = new MemoryArchiveFileSystem(
            "root.zip::inner.pre",
            ArchiveAssetType.Pre,
            disposalOrder,
            root);
        child.Add("shared.qb.ps2", [0], "alpha");
        child.Add("start.trg.n64", BuildTerminatorTrg(), "levels");
        child.Add("ignored.trg.n64.bak", BuildTerminatorTrg());
        root.AddNested(innerEntry, child);

        var catalog = ScriptArchiveCatalog.Open(root);
        try
        {
            Assert.Equal(4, catalog.Candidates.Count);
            var displayNames = catalog.Candidates
                .Select(static candidate => candidate.DisplayName)
                .ToArray();
            Assert.Equal(
                displayNames
                    .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static name => name, StringComparer.Ordinal),
                displayNames);
            Assert.All(catalog.Candidates, candidate =>
            {
                Assert.Equal(candidate.Source.DisplayName, candidate.DisplayName);
                Assert.Contains("::", candidate.DisplayName);
            });

            var duplicateSources = catalog.Candidates
                .Where(static candidate => candidate.Source.EntryName.Equals(
                    "shared.qb.ps2", StringComparison.OrdinalIgnoreCase))
                .Select(static candidate => candidate.Source)
                .ToArray();
            Assert.Equal(2, duplicateSources.Length);
            Assert.NotEqual(duplicateSources[0].DisplayName, duplicateSources[1].DisplayName);
            Assert.All(duplicateSources, static source =>
                Assert.Equal(QbTokenType.EndOfFile,
                    Assert.Single(ScriptAssetParser.ParseQb(source).Tokens).Type));

            var trgCandidate = Assert.Single(catalog.Candidates,
                static candidate => candidate.Kind == ScriptAssetKind.Trg);
            Assert.Equal("start.trg.n64", trgCandidate.Source.EntryName);
            Assert.Equal(255,
                Assert.Single(ScriptAssetParser.ParseTrg(trgCandidate.Source).Nodes).TypeId);
            Assert.False(root.IsDisposed);
            Assert.False(child.IsDisposed);
        }
        finally
        {
            catalog.Dispose();
        }

        catalog.Dispose();
        Assert.True(root.IsDisposed);
        Assert.True(child.IsDisposed);
        Assert.Equal([child.DisplayPath, root.DisplayPath], disposalOrder);
    }

    [Fact]
    public void Open_WhenCancelledDuringNestedScan_DisposesChildBeforeRoot()
    {
        var disposalOrder = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var root = new MemoryArchiveFileSystem("root.zip", ArchiveAssetType.Zip, disposalOrder);
        var innerEntry = root.Add("inner.pre", []);
        var child = new MemoryArchiveFileSystem(
            "root.zip::inner.pre",
            ArchiveAssetType.Pre,
            disposalOrder,
            root);
        child.Add("level.qb.ps2", [0]);
        root.AddNested(innerEntry, child, cancellation.Cancel);

        Assert.Throws<OperationCanceledException>(() =>
            ScriptArchiveCatalog.Open(root, cancellation.Token));

        Assert.Equal([child.DisplayPath, root.DisplayPath], disposalOrder);
    }

    [Fact]
    public void Open_WithAlreadyCancelledToken_DisposesOwnedRoot()
    {
        var disposalOrder = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = new MemoryArchiveFileSystem(
            "root.zip",
            ArchiveAssetType.Zip,
            disposalOrder);

        Assert.Throws<OperationCanceledException>(() =>
            ScriptArchiveCatalog.Open(root, cancellation.Token));

        Assert.Equal([root.DisplayPath], disposalOrder);
    }

    [Fact]
    public void MaterializedCandidates_ParseAfterCatalogDisposesOwnedRoot()
    {
        var disposalOrder = new List<string>();
        var root = new MemoryArchiveFileSystem(
            "root.zip",
            ArchiveAssetType.Zip,
            disposalOrder);
        root.Add("menu.qb.ps2", [0], "scripts");
        root.Add("warehouse.trg.n64", BuildTerminatorTrg(), "levels");

        AssetSource bufferedQb;
        AssetSource bufferedTrg;
        using (var catalog = ScriptArchiveCatalog.Open(root))
        {
            bufferedQb = ScriptAssetParser.Materialize(
                Assert.Single(catalog.Candidates,
                    static candidate => candidate.Kind == ScriptAssetKind.Qb).Source);
            bufferedTrg = ScriptAssetParser.Materialize(
                Assert.Single(catalog.Candidates,
                    static candidate => candidate.Kind == ScriptAssetKind.Trg).Source);
        }

        Assert.True(root.IsDisposed);
        Assert.Equal([root.DisplayPath], disposalOrder);
        Assert.Equal(
            QbTokenType.EndOfFile,
            Assert.Single(ScriptAssetParser.ParseQb(bufferedQb).Tokens).Type);
        Assert.Equal(
            255,
            Assert.Single(ScriptAssetParser.ParseTrg(bufferedTrg).Nodes).TypeId);
    }

    private static byte[] BuildTerminatorTrg()
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x4752545F);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x00010002);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 255);
        return data;
    }

    private sealed class MemoryArchiveFileSystem : IArchiveFileSystem
    {
        private readonly Dictionary<ArchiveEntry, byte[]> _bytes = new();
        private readonly List<string> _disposalOrder;
        private readonly List<ArchiveEntry> _entries = [];
        private readonly Dictionary<ArchiveEntry, (IArchiveFileSystem Child, Action? OnOpen)> _nested = new();

        public MemoryArchiveFileSystem(
            string displayPath,
            ArchiveAssetType type,
            List<string> disposalOrder,
            IArchiveFileSystem? parent = null)
        {
            DisplayPath = displayPath;
            ContainerPath = "memory";
            Type = type;
            NestingDepth = parent == null ? 0 : parent.NestingDepth + 1;
            Parent = parent;
            _disposalOrder = disposalOrder;
        }

        public string DisplayPath { get; }
        public string ContainerPath { get; }
        public ArchiveAssetType Type { get; }
        public int NestingDepth { get; }
        public IArchiveFileSystem? Parent { get; }
        public IReadOnlyList<ArchiveEntry> Entries => _entries;
        public bool IsDisposed { get; private set; }

        public ArchiveEntry Add(string name, byte[] bytes, string directory = "")
        {
            var entry = new ArchiveEntry
            {
                Name = name,
                Directory = directory,
                Size = bytes.Length
            };
            _entries.Add(entry);
            _bytes.Add(entry, bytes);
            return entry;
        }

        public void AddNested(ArchiveEntry entry, IArchiveFileSystem child, Action? onOpen = null)
        {
            _nested.Add(entry, (child, onOpen));
        }

        public byte[] ReadEntry(ArchiveEntry entry)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _bytes[entry];
        }

        public ArchiveEntry? FindByPath(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            return _entries.FirstOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(
                normalized, StringComparison.OrdinalIgnoreCase));
        }

        public ArchiveEntry? FindByName(string basename) =>
            FindAllByName(basename).FirstOrDefault();

        public IReadOnlyList<ArchiveEntry> FindAllByName(string basename) =>
            _entries.Where(entry => EntryBasename(entry.Name).Equals(
                    basename, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        public IArchiveFileSystem? TryOpenNested(ArchiveEntry entry)
        {
            if (!_nested.TryGetValue(entry, out var nested))
                return null;

            nested.OnOpen?.Invoke();
            return nested.Child;
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            _disposalOrder.Add(DisplayPath);
        }

        private static string EntryBasename(string name)
        {
            var separator = name.LastIndexOfAny(['/', '\\']);
            return separator < 0 ? name : name[(separator + 1)..];
        }
    }
}
