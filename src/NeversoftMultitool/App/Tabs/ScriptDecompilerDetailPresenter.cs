using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool;

internal sealed class ScriptDecompilerDetailPresenter
{
    private readonly ScriptDecompilerDetailView _view;

    public ScriptDecompilerDetailPresenter(ScriptDecompilerDetailView view)
    {
        _view = view;
    }

    public void ShowSelection(object? selectedItem)
    {
        switch (selectedItem)
        {
            case TrgNodeEntry nodeEntry:
                ShowNodeDetail(nodeEntry);
                break;
            case TrgFileEntry trgFileEntry:
                ShowTrgFileDetail(trgFileEntry);
                break;
            case QbItemEntry qbItemEntry:
                ShowQbItemDetail(qbItemEntry);
                break;
            case QbFileEntry qbFileEntry:
                ShowQbFileDetail(qbFileEntry);
                break;
            default:
                Clear();
                break;
        }
    }

    public void Clear()
    {
        _view.DetailPanel.Visibility = Visibility.Collapsed;
        _view.DetailSplitter.Visibility = Visibility.Collapsed;
        _view.DetailSplitterColumn.Width = new GridLength(0);
        _view.DetailColumn.Width = new GridLength(0);
    }

    private void ShowTrgFileDetail(TrgFileEntry entry)
    {
        _view.DetailPanel.Visibility = Visibility.Visible;
        _view.DetailSplitter.Visibility = Visibility.Visible;
        _view.DetailSplitterColumn.Width = new GridLength(8);
        if (_view.DetailColumn.Width.Value <= 0)
            _view.DetailColumn.Width = new GridLength(320);

        _view.DetailTypeText.Text = "TRG FILE";
        _view.DetailIndexText.Text = entry.FileName;

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
        HideTrgSections();
        HideQbSections();
    }

    private void ShowQbFileDetail(QbFileEntry entry)
    {
        _view.DetailPanel.Visibility = Visibility.Visible;
        _view.DetailSplitter.Visibility = Visibility.Visible;
        _view.DetailSplitterColumn.Width = new GridLength(8);
        if (_view.DetailColumn.Width.Value <= 0)
            _view.DetailColumn.Width = new GridLength(400);

        _view.DetailTypeText.Text = "QB FILE";
        _view.DetailIndexText.Text = entry.FileName;

        var qb = entry.CachedParsedFile;
        var props = new List<(string Label, string Value)>
        {
            ("Format", "QB (compiled script)")
        };

        if (qb != null)
        {
            props.Add(("Scripts", qb.ScriptCount.ToString()));
            props.Add(("Globals", qb.GlobalCount.ToString()));
            props.Add(("Tokens", qb.Tokens.Count.ToString()));
            props.Add(("Local Names", qb.LocalNames.Count.ToString()));
        }

        PopulateProperties(props);
        HideTrgSections();

        if (qb != null)
        {
            _view.SourceSection.Visibility = Visibility.Visible;
            _view.SourceHeaderText.Text = "Decompiled Source";
            _view.DetailSourceText.Text = QbDecompiler.Decompile(qb);
        }
        else
        {
            HideQbSections();
        }
    }

    private void ShowNodeDetail(TrgNodeEntry entry)
    {
        var node = entry.Node;

        _view.DetailPanel.Visibility = Visibility.Visible;
        _view.DetailSplitter.Visibility = Visibility.Visible;
        _view.DetailSplitterColumn.Width = new GridLength(8);
        if (_view.DetailColumn.Width.Value <= 0)
            _view.DetailColumn.Width = new GridLength(320);

        _view.DetailTypeText.Text = node.Type;
        _view.DetailIndexText.Text = $"Node #{node.Index}  |  Offset 0x{node.Offset:X}";

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
            var light = node.LightParams;
            props.Add(("Color 1", $"RGB({light.Color1R}, {light.Color1G}, {light.Color1B})"));
            props.Add(("Color 2", $"RGB({light.Color2R}, {light.Color2G}, {light.Color2B})"));
            props.Add(("Range", $"{light.Range}"));
            props.Add(("Cone", $"{light.InnerAngle}° / {light.OuterAngle}°"));
            props.Add(("Falloff", $"{light.Falloff}"));
        }

        PopulateProperties(props);
        HideQbSections();

        if (node.Position != null)
        {
            _view.PositionSection.Visibility = Visibility.Visible;
            _view.DetailPositionText.Text =
                $"X: {node.Position.X:F2}  Y: {node.Position.Y:F2}  Z: {node.Position.Z:F2}";
        }
        else
        {
            _view.PositionSection.Visibility = Visibility.Collapsed;
        }

        if (node.Angles != null)
        {
            _view.AnglesSection.Visibility = Visibility.Visible;
            _view.DetailAnglesText.Text =
                $"X: {node.Angles.X:F2}°  Y: {node.Angles.Y:F2}°  Z: {node.Angles.Z:F2}°";
        }
        else
        {
            _view.AnglesSection.Visibility = Visibility.Collapsed;
        }

        if (node.Links is { Count: > 0 })
        {
            _view.LinksSection.Visibility = Visibility.Visible;
            _view.DetailLinksText.Text = string.Join(", ", node.Links.Select(link => $"#{link}"));
        }
        else
        {
            _view.LinksSection.Visibility = Visibility.Collapsed;
        }

        if (node.Commands is { Count: > 0 })
        {
            _view.CommandsSection.Visibility = Visibility.Visible;
            _view.CommandsHeaderText.Text = $"Commands ({node.Commands.Count})";
            _view.CommandsRepeater.ItemsSource = node.Commands.Select(FormatCommand).ToList();
        }
        else
        {
            _view.CommandsSection.Visibility = Visibility.Collapsed;
        }

        if (node.Script is { Count: > 0 })
        {
            _view.ScriptSection.Visibility = Visibility.Visible;
            _view.ScriptHeaderText.Text = $"Script ({node.Script.Count} ops)";
            _view.ScriptRepeater.ItemsSource = node.Script.Select(FormatScriptOp).ToList();
        }
        else
        {
            _view.ScriptSection.Visibility = Visibility.Collapsed;
        }

        if (node.RawHex != null)
        {
            _view.RawHexSection.Visibility = Visibility.Visible;
            _view.DetailRawHexText.Text = FormatHex(node.RawHex);
        }
        else
        {
            _view.RawHexSection.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowQbItemDetail(QbItemEntry entry)
    {
        _view.DetailPanel.Visibility = Visibility.Visible;
        _view.DetailSplitter.Visibility = Visibility.Visible;
        _view.DetailSplitterColumn.Width = new GridLength(8);
        if (_view.DetailColumn.Width.Value <= 0)
            _view.DetailColumn.Width = new GridLength(400);

        _view.DetailTypeText.Text = entry.TypeDisplay;
        _view.DetailIndexText.Text = $"Item #{entry.ItemIndex}  |  {entry.SummaryDisplay}";

        var props = new List<(string Label, string Value)>
        {
            ("Kind", entry.Item.Kind.ToString()),
            ("Name", entry.SummaryDisplay),
            ("Checksum", $"0x{entry.Item.NameChecksum:X8}")
        };
        PopulateProperties(props);
        HideTrgSections();

        _view.SourceSection.Visibility = Visibility.Visible;
        _view.SourceHeaderText.Text = "Decompiled Source";
        _view.DetailSourceText.Text = QbDecompiler.DecompileItem(entry.QbFile, entry.Item);
    }

    private void HideTrgSections()
    {
        _view.PositionSection.Visibility = Visibility.Collapsed;
        _view.AnglesSection.Visibility = Visibility.Collapsed;
        _view.LinksSection.Visibility = Visibility.Collapsed;
        _view.CommandsSection.Visibility = Visibility.Collapsed;
        _view.ScriptSection.Visibility = Visibility.Collapsed;
        _view.RawHexSection.Visibility = Visibility.Collapsed;
    }

    private void HideQbSections()
    {
        _view.SourceSection.Visibility = Visibility.Collapsed;
    }

    private void PopulateProperties(List<(string Label, string Value)> props)
    {
        _view.PropertiesGrid.Children.Clear();
        _view.PropertiesGrid.RowDefinitions.Clear();
        _view.PropertiesGrid.ColumnDefinitions.Clear();

        if (props.Count == 0)
        {
            _view.PropertiesSection.Visibility = Visibility.Collapsed;
            return;
        }

        _view.PropertiesSection.Visibility = Visibility.Visible;
        _view.PropertiesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _view.PropertiesGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < props.Count; i++)
        {
            _view.PropertiesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = props[i].Label + ":",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            _view.PropertiesGrid.Children.Add(label);

            var value = new TextBlock
            {
                Text = props[i].Value,
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _view.PropertiesGrid.Children.Add(value);
        }
    }

    private static string FormatCommand(TrgCommand cmd)
    {
        if (cmd.Args is { Count: > 0 })
        {
            var args = string.Join(", ", cmd.Args.Select(arg =>
                arg is string value ? $"\"{value}\"" : arg?.ToString() ?? string.Empty));
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
        var builder = new StringBuilder();
        for (var i = 0; i < hex.Length; i += 32)
        {
            if (i > 0)
                builder.AppendLine();

            builder.Append(hex.AsSpan(i, Math.Min(32, hex.Length - i)));
        }

        return builder.ToString();
    }
}
