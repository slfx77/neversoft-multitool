using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NeversoftMultitool.Core.Formats.Trg;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class TrgViewerTab : UserControl
{
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<TrgFileEntry> _parentFiles = [];
    private CancellationTokenSource? _cts;
    private string _outputDir = "";

    public TrgViewerTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".trg");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        InputPathText.Text = file.Path;
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        try
        {
            var trg = TrgFile.Parse(file.Path);
            var entry = new TrgFileEntry
            {
                FileName = Path.GetFileName(file.Path),
                FilePath = file.Path
            };
            entry.CachedParsedFile = trg;
            entry.NodeCount = trg.NodeCount;
            entry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
            _parentFiles.Add(entry);
            _items.Add(entry);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Failed to parse: {ex.Message}");
        }

        UpdateUiState();
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        InputPathText.Text = path;
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        var trgFiles = Directory.GetFiles(path)
            .Where(f => Path.GetExtension(f).Equals(".trg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in trgFiles)
        {
            var entry = new TrgFileEntry
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();

        // Background parse to get version + node counts
        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    var trg = TrgFile.Parse(entry.FilePath);
                    entry.CachedParsedFile = trg;
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.NodeCount = trg.NodeCount;
                        entry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
                    });
                }
                catch
                {
                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Error);
                }
            }
        });
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _parentFiles.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrgFileEntry parent }) return;
        if (parent.NodeCount == 0) return;

        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            if (parent.CachedChildren == null)
            {
                if (parent.CachedParsedFile == null)
                {
                    try
                    {
                        parent.CachedParsedFile = TrgFile.Parse(parent.FilePath);
                        parent.NodeCount = parent.CachedParsedFile.NodeCount;
                        parent.VersionDisplay =
                            $"{parent.CachedParsedFile.VersionMajor}.{parent.CachedParsedFile.VersionMinor}";
                    }
                    catch
                    {
                        parent.CachedChildren = [];
                        return;
                    }
                }

                parent.CachedChildren = parent.CachedParsedFile.Nodes
                    .Select((n, i) => new TrgNodeEntry
                    {
                        ParentFileName = parent.FileName,
                        NodeIndex = i,
                        Node = n
                    }).ToList();
            }

            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    // ── Selection and Detail Panel ──────────────────────────────────────

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (FilesListView.SelectedItem)
        {
            case TrgNodeEntry nodeEntry:
                ShowNodeDetail(nodeEntry);
                break;
            case TrgFileEntry fileEntry:
                ShowFileDetail(fileEntry);
                break;
            default:
                ClearDetail();
                break;
        }
    }

    private void ShowFileDetail(TrgFileEntry entry)
    {
        DetailPanel.Visibility = Visibility.Visible;
        DetailColumn.Width = new GridLength(320);

        DetailTypeText.Text = "FILE";
        DetailIndexText.Text = entry.FileName;

        // Build type distribution summary
        var props = new List<(string Label, string Value)>
        {
            ("Version", entry.VersionDisplay),
            ("Nodes", entry.NodeCount.ToString())
        };

        if (entry.CachedParsedFile != null)
        {
            var typeDist = entry.CachedParsedFile.Nodes
                .GroupBy(n => n.Type)
                .OrderByDescending(g => g.Count())
                .Take(8)
                .Select(g => $"{g.Key}: {g.Count()}");
            props.Add(("Types", string.Join("\n", typeDist)));
        }

        PopulateProperties(props);

        PositionSection.Visibility = Visibility.Collapsed;
        AnglesSection.Visibility = Visibility.Collapsed;
        LinksSection.Visibility = Visibility.Collapsed;
        CommandsSection.Visibility = Visibility.Collapsed;
        ScriptSection.Visibility = Visibility.Collapsed;
        RawHexSection.Visibility = Visibility.Collapsed;
    }

    private void ShowNodeDetail(TrgNodeEntry entry)
    {
        var node = entry.Node;

        DetailPanel.Visibility = Visibility.Visible;
        DetailColumn.Width = new GridLength(320);

        DetailTypeText.Text = node.Type;
        DetailIndexText.Text = $"Node #{node.Index}  |  Offset 0x{node.Offset:X}";

        // Properties
        var props = new List<(string Label, string Value)>();
        if (node.Name != null) props.Add(("Name", node.Name));
        if (node.Checksum.HasValue) props.Add(("Checksum", $"0x{node.Checksum.Value:X8}"));
        if (node.SubType.HasValue)
            props.Add(("Sub-Type",
                node.SubTypeName != null ? $"{node.SubTypeName} ({node.SubType.Value})" : $"#{node.SubType.Value}"));
        if (node.PickupType.HasValue)
            props.Add(("Pickup",
                node.PickupTypeName != null
                    ? $"{node.PickupTypeName} ({node.PickupType.Value})"
                    : $"#{node.PickupType.Value}"));
        if (node.CameraMode.HasValue)
            props.Add(("Camera Mode",
                node.CameraModeName != null
                    ? $"{node.CameraModeName} ({node.CameraMode.Value})"
                    : $"#{node.CameraMode.Value}"));
        if (node.CameraRadius.HasValue)
            props.Add(("Camera Radius", node.CameraRadius.Value.ToString()));
        if (node.TerrainType.HasValue)
            props.Add(("Terrain",
                node.TerrainTypeName != null
                    ? $"{node.TerrainTypeName} ({node.TerrainType.Value})"
                    : $"#{node.TerrainType.Value}"));
        if (node.LightParams != null)
        {
            var lp = node.LightParams;
            props.Add(("Color 1", $"RGB({lp.Color1R}, {lp.Color1G}, {lp.Color1B})"));
            props.Add(("Color 2", $"RGB({lp.Color2R}, {lp.Color2G}, {lp.Color2B})"));
            props.Add(("Range", $"{lp.Range}"));
            props.Add(("Cone", $"{lp.InnerAngle}° / {lp.OuterAngle}°"));
            props.Add(("Falloff", $"{lp.Falloff}"));
        }

        PopulateProperties(props);

        // Position
        if (node.Position != null)
        {
            PositionSection.Visibility = Visibility.Visible;
            DetailPositionText.Text =
                $"X: {node.Position.X:F2}  Y: {node.Position.Y:F2}  Z: {node.Position.Z:F2}";
        }
        else
        {
            PositionSection.Visibility = Visibility.Collapsed;
        }

        // Angles
        if (node.Angles != null)
        {
            AnglesSection.Visibility = Visibility.Visible;
            DetailAnglesText.Text =
                $"X: {node.Angles.X:F2}\u00B0  Y: {node.Angles.Y:F2}\u00B0  Z: {node.Angles.Z:F2}\u00B0";
        }
        else
        {
            AnglesSection.Visibility = Visibility.Collapsed;
        }

        // Links
        if (node.Links is { Count: > 0 })
        {
            LinksSection.Visibility = Visibility.Visible;
            DetailLinksText.Text = string.Join(", ", node.Links.Select(l => $"#{l}"));
        }
        else
        {
            LinksSection.Visibility = Visibility.Collapsed;
        }

        // Commands
        if (node.Commands is { Count: > 0 })
        {
            CommandsSection.Visibility = Visibility.Visible;
            CommandsHeaderText.Text = $"Commands ({node.Commands.Count})";
            CommandsRepeater.ItemsSource = node.Commands.Select(FormatCommand).ToList();
        }
        else
        {
            CommandsSection.Visibility = Visibility.Collapsed;
        }

        // Script
        if (node.Script is { Count: > 0 })
        {
            ScriptSection.Visibility = Visibility.Visible;
            ScriptHeaderText.Text = $"Script ({node.Script.Count} ops)";
            ScriptRepeater.ItemsSource = node.Script.Select(FormatScriptOp).ToList();
        }
        else
        {
            ScriptSection.Visibility = Visibility.Collapsed;
        }

        // Raw hex
        if (node.RawHex != null)
        {
            RawHexSection.Visibility = Visibility.Visible;
            DetailRawHexText.Text = FormatHex(node.RawHex);
        }
        else
        {
            RawHexSection.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateProperties(List<(string Label, string Value)> props)
    {
        PropertiesGrid.Children.Clear();
        PropertiesGrid.RowDefinitions.Clear();
        PropertiesGrid.ColumnDefinitions.Clear();

        if (props.Count == 0)
        {
            PropertiesSection.Visibility = Visibility.Collapsed;
            return;
        }

        PropertiesSection.Visibility = Visibility.Visible;
        PropertiesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        PropertiesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < props.Count; i++)
        {
            PropertiesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = props[i].Label + ":",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            PropertiesGrid.Children.Add(label);

            var value = new TextBlock
            {
                Text = props[i].Value,
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            PropertiesGrid.Children.Add(value);
        }
    }

    private void ClearDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
    }

    // ── Formatting helpers ──────────────────────────────────────────────

    private static string FormatCommand(TrgCommand cmd)
    {
        if (cmd.Args is { Count: > 0 })
        {
            var args = string.Join(", ", cmd.Args.Select(a =>
                a is string s ? $"\"{s}\"" : a?.ToString() ?? ""));
            return $"{cmd.Name}({args})";
        }

        return cmd.Name;
    }

    private static string FormatScriptOp(TrgScriptOp op)
    {
        if (op.Value != null)
        {
            var valueStr = op.Value is object[] arr
                ? string.Join(", ", arr)
                : op.Value.ToString();
            return $"{op.Opcode}  {op.Name}  {valueStr}";
        }

        return $"{op.Opcode}  {op.Name}";
    }

    private static string FormatHex(string hex)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < hex.Length; i += 32)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(hex.AsSpan(i, Math.Min(32, hex.Length - i)));
        }

        return sb.ToString();
    }

    // ── Batch Export ────────────────────────────────────────────────────

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _parentFiles)
            file.Status = ExtractionStatus.Pending;

        ExportButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _parentFiles.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;
        var entries = _parentFiles.ToList();

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(outputDir);

                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    try
                    {
                        var trg = entry.CachedParsedFile ?? TrgFile.Parse(entry.FilePath);
                        entry.CachedParsedFile ??= trg;

                        var outputPath = Path.Combine(outputDir,
                            Path.GetFileNameWithoutExtension(entry.FileName) + ".json");
                        trg.WriteJson(outputPath);

                        var processed = Interlocked.Increment(ref filesProcessed);
                        dispatcher.TryEnqueue(() =>
                        {
                            entry.Status = ExtractionStatus.Done;
                            ExportProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                    catch
                    {
                        var processed = Interlocked.Increment(ref filesProcessed);
                        dispatcher.TryEnqueue(() =>
                        {
                            entry.Status = ExtractionStatus.Error;
                            ExportProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                }
            }, token);

            stopwatch.Stop();
            ExportProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Exported {filesProcessed} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Export cancelled");
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Export cancelled");
    }
}
