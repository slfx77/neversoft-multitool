using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace NeversoftMultitool;

/// <summary>
///     Adds the common desktop-table affordances to the app's virtualized
///     ListView tables: synchronized column resizing, sortable headers, and a
///     cell-aware Copy context menu. Keeping this as an attached behavior lets
///     each tab retain its existing data template and virtualization.
/// </summary>
public sealed class FileTableBehavior : DependencyObject
{
    private static readonly ConditionalWeakTable<Grid, TableState> Tables = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(FileTableBehavior),
        new PropertyMetadata(false, IsEnabledChanged));

    public static readonly DependencyProperty IsRowProperty = DependencyProperty.RegisterAttached(
        "IsRow",
        typeof(bool),
        typeof(FileTableBehavior),
        new PropertyMetadata(false));

    public static readonly DependencyProperty SortPropertyProperty = DependencyProperty.RegisterAttached(
        "SortProperty",
        typeof(string),
        typeof(FileTableBehavior),
        new PropertyMetadata(""));

    public FileTableBehavior()
    {
    }

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsRow(DependencyObject element) =>
        (bool)element.GetValue(IsRowProperty);

    public static void SetIsRow(DependencyObject element, bool value) =>
        element.SetValue(IsRowProperty, value);

    public static string GetSortProperty(DependencyObject element) =>
        (string)element.GetValue(SortPropertyProperty);

    public static void SetSortProperty(DependencyObject element, string value) =>
        element.SetValue(SortPropertyProperty, value);

    private static void IsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Grid host) return;

        if (args.NewValue is true)
        {
            Tables.GetValue(host, static grid => new TableState(grid)).Enable();
            return;
        }

        if (Tables.TryGetValue(host, out var state))
        {
            state.Disable();
            Tables.Remove(host);
        }
    }

    private sealed class TableState
    {
        private const double SplitterWidth = 8;

        private readonly Grid _host;
        private readonly List<GridSplitter> _splitters = [];
        private readonly List<HeaderRegistration> _headers = [];
        private readonly List<(ColumnDefinition Column, long Token)> _columnCallbacks = [];
        private Grid? _header;
        private ListView? _listView;
        private IList? _sourceItems;
        private SortedObservableView? _sortedView;
        private string? _sortProperty;
        private bool _ascending = true;
        private bool _enabled;
        private bool _started;
        private bool _syncQueued;

        public TableState(Grid host)
        {
            _host = host;
        }

        public void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _host.Loaded += Host_Loaded;
            _host.Unloaded += Host_Unloaded;
            if (_host.IsLoaded) Start();
        }

        public void Disable()
        {
            if (!_enabled) return;
            _enabled = false;
            _host.Loaded -= Host_Loaded;
            _host.Unloaded -= Host_Unloaded;
            Stop();
            if (_header != null)
            {
                foreach (var splitter in _splitters)
                    _header.Children.Remove(splitter);
            }
            _splitters.Clear();

            if (_sortedView != null)
            {
                if (_listView != null && _sourceItems != null)
                    _listView.ItemsSource = _sourceItems;
                _sortedView.Dispose();
                _sortedView = null;
                _sourceItems = null;
            }
        }

        private void Host_Loaded(object sender, RoutedEventArgs e) => Start();

        private void Host_Unloaded(object sender, RoutedEventArgs e) => Stop();

        private void Start()
        {
            if (_started) return;

            _listView = _host.Children.OfType<ListView>().FirstOrDefault()
                        ?? FindDescendant<ListView>(_host);
            _header = _host.Children.OfType<Grid>()
                          .FirstOrDefault(grid => grid.ColumnDefinitions.Count > 1)
                      ?? FindDescendant<Grid>(_host, grid => grid.ColumnDefinitions.Count > 1 && !GetIsRow(grid));

            if (_listView == null || _header == null || _header.ColumnDefinitions.Count < 2) return;

            _started = true;
            RegisterHeaders();
            if (_sortProperty != null) UpdateHeaderIndicators();
            EnsureSplitters();
            RegisterColumnCallbacks();

            _header.SizeChanged += Header_SizeChanged;
            _listView.ContainerContentChanging += ListView_ContainerContentChanging;
            _listView.RightTapped += ListView_RightTapped;
            QueueColumnSync();
        }

        private void Stop()
        {
            if (!_started) return;
            _started = false;

            if (_header != null) _header.SizeChanged -= Header_SizeChanged;
            if (_listView != null)
            {
                _listView.ContainerContentChanging -= ListView_ContainerContentChanging;
                _listView.RightTapped -= ListView_RightTapped;
            }

            foreach (var registration in _headers)
                registration.Button.Click -= Header_Click;
            _headers.Clear();

            foreach (var (column, token) in _columnCallbacks)
                column.UnregisterPropertyChangedCallback(ColumnDefinition.WidthProperty, token);
            _columnCallbacks.Clear();
        }

        private void RegisterHeaders()
        {
            if (_header == null) return;

            foreach (var button in DescendantsAndSelf(_header).OfType<Button>())
            {
                var property = GetSortProperty(button);
                if (string.IsNullOrWhiteSpace(property)) continue;

                var label = (button.Content?.ToString() ?? property)
                    .TrimEnd()
                    .TrimEnd('▲', '▼')
                    .TrimEnd();
                _headers.Add(new HeaderRegistration(button, label, property));
                button.Click += Header_Click;
                ToolTipService.SetToolTip(button, $"Sort by {label}");
                AutomationProperties.SetHelpText(button, $"Sort by {label}");
            }
        }

        private void EnsureSplitters()
        {
            if (_header == null || _splitters.Count > 0) return;

            for (var index = 0; index < _header.ColumnDefinitions.Count - 1; index++)
            {
                var column = _header.ColumnDefinitions[index];
                if (column.MinWidth <= 0)
                {
                    var declaredWidth = column.Width.IsAbsolute ? column.Width.Value : 0;
                    column.MinWidth = declaredWidth is > 0 and <= 40 ? 28 : 44;
                }

                var splitter = new GridSplitter
                {
                    Width = SplitterWidth,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    ResizeDirection = GridSplitter.GridResizeDirection.Columns,
                    ResizeBehavior = GridSplitter.GridResizeBehavior.CurrentAndNext,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    IsTabStop = true
                };

                Grid.SetColumn(splitter, index);
                Canvas.SetZIndex(splitter, 100);
                ToolTipService.SetToolTip(splitter, "Drag to resize columns");
                AutomationProperties.SetName(splitter, $"Resize column {index + 1}");
                _header.Children.Add(splitter);
                _splitters.Add(splitter);
            }

            var lastColumn = _header.ColumnDefinitions[^1];
            if (lastColumn.MinWidth <= 0) lastColumn.MinWidth = 44;
        }

        private void RegisterColumnCallbacks()
        {
            if (_header == null) return;

            foreach (var column in _header.ColumnDefinitions)
            {
                var token = column.RegisterPropertyChangedCallback(
                    ColumnDefinition.WidthProperty,
                    ColumnWidthChanged);
                _columnCallbacks.Add((column, token));
            }
        }

        private void ColumnWidthChanged(DependencyObject sender, DependencyProperty property) => QueueColumnSync();

        private void Header_SizeChanged(object sender, SizeChangedEventArgs e) => QueueColumnSync();

        private void ListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue) QueueColumnSync();
        }

        private void QueueColumnSync()
        {
            if (_syncQueued || !_started) return;
            _syncQueued = true;
            if (!_host.DispatcherQueue.TryEnqueue(() =>
                {
                    _syncQueued = false;
                    SynchronizeRealizedRows();
                }))
                _syncQueued = false;
        }

        private void SynchronizeRealizedRows()
        {
            if (_header == null || _listView == null) return;

            var widths = _header.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
            if (widths.Length < 2 || widths.Any(width => width <= 0)) return;

            foreach (var row in DescendantsAndSelf(_listView).OfType<Grid>().Where(GetIsRow))
            {
                if (row.ColumnDefinitions.Count != widths.Length) continue;

                // Pixel widths keep every boundary aligned with the header. The
                // last column stays fluid so a visible scrollbar cannot force
                // the row wider than the ListView viewport.
                for (var index = 0; index < widths.Length - 1; index++)
                    row.ColumnDefinitions[index].Width = new GridLength(widths[index], GridUnitType.Pixel);
                row.ColumnDefinitions[^1].Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            var property = GetSortProperty(button);
            if (string.IsNullOrWhiteSpace(property)) return;

            var nextAscending = !string.Equals(_sortProperty, property, StringComparison.Ordinal) || !_ascending;
            if (!SortItems(property, nextAscending)) return;
            _sortProperty = property;
            _ascending = nextAscending;
            UpdateHeaderIndicators();
            QueueColumnSync();
        }

        private bool SortItems(string property, bool ascending)
        {
            if (_listView == null) return false;
            var source = _sourceItems ?? _listView.ItemsSource as IList;
            if (source == null) return false;

            var selectedItem = _listView.SelectedItem;
            if (_sortedView == null)
            {
                _sourceItems = source;
                _sortedView = new SortedObservableView(
                    source,
                    _host.DispatcherQueue,
                    () => _listView?.SelectedItem);
                _sortedView.SetSort(property, ascending, raiseChanged: false);
                _listView.ItemsSource = _sortedView;
            }
            else
                _sortedView.SetSort(property, ascending);
            if (selectedItem != null && _sortedView.Contains(selectedItem))
                _listView.SelectedItem = selectedItem;

            return true;
        }

        private void UpdateHeaderIndicators()
        {
            foreach (var registration in _headers)
            {
                var active = string.Equals(registration.Property, _sortProperty, StringComparison.Ordinal);
                var direction = _ascending ? "ascending" : "descending";
                var glyph = _ascending ? "▲" : "▼";
                registration.Button.Content = active ? $"{registration.Label} {glyph}" : registration.Label;
                AutomationProperties.SetHelpText(
                    registration.Button,
                    active
                        ? $"Sorted {direction}; activate to reverse"
                        : $"Sort by {registration.Label}");
            }
        }

        private void ListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_listView == null || e.OriginalSource is not DependencyObject source) return;

            var row = FindAncestor(source, element => element is Grid grid && GetIsRow(grid)) as Grid;
            var value = row == null
                ? GetDisplayedString(FindAncestor(source, IsTextualElement))
                : GetCellValue(row, e.GetPosition(row));
            if (string.IsNullOrEmpty(value)) return;

            var anchor = (FrameworkElement?)row ?? FindAncestor(source, element => element is FrameworkElement) as FrameworkElement;
            if (anchor == null) return;

            var copyItem = new MenuFlyoutItem { Text = "Copy" };
            copyItem.Click += (_, _) => CopyText(value);

            var flyout = new MenuFlyout();
            flyout.Items.Add(copyItem);
            flyout.ShowAt(anchor, new FlyoutShowOptions
            {
                Position = e.GetPosition(anchor),
                ShowMode = FlyoutShowMode.Transient
            });
            e.Handled = true;
        }

        private static string? GetCellValue(Grid row, Point point)
        {
            if (row.ColumnDefinitions.Count == 0) return GetDisplayedString(row);

            var x = Math.Max(0, point.X - row.Padding.Left);
            var columnIndex = row.ColumnDefinitions.Count - 1;
            var edge = 0d;
            for (var index = 0; index < row.ColumnDefinitions.Count; index++)
            {
                edge += row.ColumnDefinitions[index].ActualWidth;
                if (x <= edge)
                {
                    columnIndex = index;
                    break;
                }
            }

            foreach (var cell in row.Children.Where(child =>
            {
                if (child is not FrameworkElement element) return false;
                var first = Grid.GetColumn(element);
                var last = first + Math.Max(1, Grid.GetColumnSpan(element)) - 1;
                return columnIndex >= first && columnIndex <= last;
            }))
            {
                var value = GetDisplayedString(cell);
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        private static bool IsTextualElement(DependencyObject element) =>
            element is TextBlock or TextBox or ToggleButton or ContentControl;

        private static string? GetDisplayedString(DependencyObject? element)
        {
            switch (element)
            {
                case null:
                    return null;
                case TextBlock textBlock:
                    return textBlock.Text;
                case TextBox textBox:
                    return textBox.Text;
                case ToggleButton toggle when toggle.Content is string content:
                    return content;
                case ToggleButton toggle:
                    return toggle.IsChecked switch
                    {
                        true => "Checked",
                        false => "Unchecked",
                        null => "Indeterminate"
                    };
                case ContentControl contentControl when contentControl.Content is string content:
                    return content;
            }

            foreach (var descendant in DescendantsAndSelf(element).Skip(1))
            {
                var value = GetDisplayedStringDirect(descendant);
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        private static string? GetDisplayedStringDirect(DependencyObject element) => element switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            ContentControl contentControl when contentControl.Content is string content => content,
            _ => null
        };

        private static void CopyText(string value)
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
        }

        private static List<SortGroup> BuildSortGroups(IReadOnlyList<object?> items)
        {
            var hasParentRows = items.Any(item => item is IListEntry { IsChildEntry: false });
            if (!hasParentRows)
                return items.Select((item, index) => new SortGroup(item, [item], index)).ToList();

            var groups = new List<SortGroup>();
            SortGroup? current = null;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item is not IListEntry { IsChildEntry: true } || current == null)
                {
                    current = new SortGroup(item, [], index);
                    groups.Add(current);
                }

                current.Items.Add(item);
            }

            return groups;
        }

        private static object? GetPropertyValue(object? item, string path)
        {
            var value = item;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (value == null) return null;
                var property = value.GetType().GetProperty(
                    segment,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null) return null;
                value = property.GetValue(value);
            }

            return value;
        }

        private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? predicate = null)
            where T : DependencyObject
        {
            foreach (var element in DescendantsAndSelf(root).Skip(1).OfType<T>())
                if (predicate == null || predicate(element))
                    return element;
            return null;
        }

        private static DependencyObject? FindAncestor(
            DependencyObject? element,
            Func<DependencyObject, bool> predicate)
        {
            while (element != null)
            {
                if (predicate(element)) return element;
                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        private static IEnumerable<DependencyObject> DescendantsAndSelf(DependencyObject root)
        {
            yield return root;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                foreach (var descendant in DescendantsAndSelf(child))
                    yield return descendant;
            }
        }

        private sealed record HeaderRegistration(Button Button, string Label, string Property);

        private sealed record SortGroup(object? SortItem, List<object?> Items, int OriginalIndex);

        private sealed record KeyedSortGroup(SortGroup Group, object? Value);

        /// <summary>
        ///     Read-only, observable projection over a tab-owned collection.
        ///     Sorting the UI view instead of the source keeps background
        ///     converters safe when they are already enumerating that source.
        /// </summary>
        private sealed class SortedObservableView : IList, INotifyCollectionChanged, IDisposable
        {
            private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
            private readonly Func<object?> _getSelectedItem;
            private readonly IList _source;
            private readonly HashSet<INotifyPropertyChanged> _observedItems =
                new(ReferenceEqualityComparer.Instance);
            private List<object?> _items = [];
            private string _property = "";
            private bool _ascending = true;
            private bool _disposed;
            private bool _rebuildQueued;

            public SortedObservableView(
                IList source,
                Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
                Func<object?> getSelectedItem)
            {
                _source = source;
                _dispatcher = dispatcher;
                _getSelectedItem = getSelectedItem;
                if (source is INotifyCollectionChanged observable)
                    observable.CollectionChanged += Source_CollectionChanged;
                Rebuild(raiseChanged: false, preserveByMoves: false);
            }

            public event NotifyCollectionChangedEventHandler? CollectionChanged;

            public int Count => _items.Count;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot { get; } = new();

            public object? this[int index]
            {
                get => _items[index];
                set => throw new NotSupportedException();
            }

            public void SetSort(string property, bool ascending, bool raiseChanged = true)
            {
                _property = property;
                _ascending = ascending;
                // A Reset transiently clears ListView selection and triggers
                // tab preview teardown before TableState can restore it. Keep
                // a selected row alive with identity Move notifications; when
                // no row is selected Rebuild still uses one efficient Reset.
                Rebuild(raiseChanged, preserveByMoves: true);
            }

            public bool Contains(object? value) => _items.Contains(value);

            public int IndexOf(object? value) => _items.IndexOf(value);

            public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

            public IEnumerator GetEnumerator() => _items.GetEnumerator();

            public int Add(object? value) => throw new NotSupportedException();

            public void Clear() => throw new NotSupportedException();

            public void Insert(int index, object? value) => throw new NotSupportedException();

            public void Remove(object? value) => throw new NotSupportedException();

            public void RemoveAt(int index) => throw new NotSupportedException();

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_source is INotifyCollectionChanged observable)
                    observable.CollectionChanged -= Source_CollectionChanged;
                foreach (var item in _observedItems)
                    item.PropertyChanged -= Item_PropertyChanged;
                _observedItems.Clear();
            }

            private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                QueueRebuild();
            }

            private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(_property)) return;

                var rootProperty = _property.Split('.', 2)[0];
                if (!string.IsNullOrEmpty(e.PropertyName) &&
                    !string.Equals(e.PropertyName, rootProperty, StringComparison.Ordinal))
                    return;

                QueueRebuild();
            }

            private void QueueRebuild()
            {
                if (_disposed || _rebuildQueued) return;
                _rebuildQueued = true;
                if (!_dispatcher.TryEnqueue(() =>
                    {
                        if (_disposed) return;
                        _rebuildQueued = false;
                        Rebuild(raiseChanged: true, preserveByMoves: true);
                    }))
                    _rebuildQueued = false;
            }

            private void Rebuild(bool raiseChanged, bool preserveByMoves)
            {
                if (_disposed) return;
                RefreshItemSubscriptions();
                var groups = BuildSortGroups(_source.Cast<object?>().ToList());
                List<object?> newItems;
                if (!string.IsNullOrEmpty(_property))
                {
                    var keyedGroups = groups
                        .Select(group => new KeyedSortGroup(group, GetPropertyValue(group.SortItem, _property)))
                        .ToList();
                    keyedGroups.Sort((left, right) =>
                    {
                        var comparison = DisplayValueComparer.Instance.Compare(left.Value, right.Value);
                        if (!_ascending) comparison = -comparison;
                        return comparison != 0
                            ? comparison
                            : left.Group.OriginalIndex.CompareTo(right.Group.OriginalIndex);
                    });
                    newItems = keyedGroups.SelectMany(group => group.Group.Items).ToList();
                }
                else
                    newItems = groups.SelectMany(group => group.Items).ToList();

                if (!raiseChanged)
                {
                    _items = newItems;
                    return;
                }

                // A Reset can make WinUI clear SelectedItem even when the same
                // row still exists. That is especially disruptive when a
                // preview publishes TriangleCount and the active sort moves its
                // own row. Reconcile by identity while a retained row is
                // selected so ListView receives Move/Add/Remove notifications
                // and keeps selection; use one efficient Reset otherwise.
                var selected = _getSelectedItem();
                if (preserveByMoves &&
                    selected != null &&
                    _items.Any(item => ReferenceEquals(item, selected)) &&
                    newItems.Any(item => ReferenceEquals(item, selected)))
                {
                    ReconcileItems(newItems);
                }
                else
                {
                    _items = newItems;
                    CollectionChanged?.Invoke(
                        this,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                }
            }

            private void ReconcileItems(IReadOnlyList<object?> desired)
            {
                // Remove vanished rows first. Otherwise a single removal near
                // the top would be expressed as a Move for every following row.
                var desiredItems = desired
                    .Where(static item => item != null)
                    .ToHashSet(ReferenceEqualityComparer.Instance);
                for (var index = _items.Count - 1; index >= 0; index--)
                {
                    var item = _items[index];
                    if (item != null && desiredItems.Contains(item)) continue;
                    if (item == null && desired.Any(static desiredItem => desiredItem == null)) continue;

                    _items.RemoveAt(index);
                    CollectionChanged?.Invoke(
                        this,
                        new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Remove,
                            item,
                            index));
                }

                for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
                {
                    var desiredItem = desired[targetIndex];
                    if (targetIndex < _items.Count &&
                        ReferenceEquals(_items[targetIndex], desiredItem))
                        continue;

                    var currentIndex = IndexOfReference(desiredItem, targetIndex + 1);
                    if (currentIndex >= 0)
                    {
                        var moved = _items[currentIndex];
                        _items.RemoveAt(currentIndex);
                        _items.Insert(targetIndex, moved);
                        CollectionChanged?.Invoke(
                            this,
                            new NotifyCollectionChangedEventArgs(
                                NotifyCollectionChangedAction.Move,
                                moved,
                                targetIndex,
                                currentIndex));
                    }
                    else
                    {
                        _items.Insert(targetIndex, desiredItem);
                        CollectionChanged?.Invoke(
                            this,
                            new NotifyCollectionChangedEventArgs(
                                NotifyCollectionChangedAction.Add,
                                desiredItem,
                                targetIndex));
                    }
                }

                for (var index = _items.Count - 1; index >= desired.Count; index--)
                {
                    var removed = _items[index];
                    _items.RemoveAt(index);
                    CollectionChanged?.Invoke(
                        this,
                        new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Remove,
                            removed,
                            index));
                }
            }

            private int IndexOfReference(object? value, int startIndex)
            {
                for (var index = Math.Max(0, startIndex); index < _items.Count; index++)
                    if (ReferenceEquals(_items[index], value))
                        return index;
                return -1;
            }

            private void RefreshItemSubscriptions()
            {
                var current = _source.Cast<object?>()
                    .OfType<INotifyPropertyChanged>()
                    .ToHashSet<INotifyPropertyChanged>(ReferenceEqualityComparer.Instance);

                foreach (var removed in _observedItems.Except(current).ToList())
                {
                    removed.PropertyChanged -= Item_PropertyChanged;
                    _observedItems.Remove(removed);
                }

                foreach (var added in current.Except(_observedItems))
                {
                    added.PropertyChanged += Item_PropertyChanged;
                    _observedItems.Add(added);
                }
            }
        }
    }

    private sealed class DisplayValueComparer : IComparer<object?>
    {
        public static DisplayValueComparer Instance { get; } = new();

        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            if (left is string leftString && right is string rightString)
                return CompareStrings(leftString, rightString);

            if (left.GetType().IsEnum && right.GetType() == left.GetType())
                return Convert.ToInt64(left, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToInt64(right, CultureInfo.InvariantCulture));

            if (left is IComparable comparable && left.GetType().IsInstanceOfType(right))
                return comparable.CompareTo(right);

            return CompareStrings(left.ToString() ?? "", right.ToString() ?? "");
        }

        private static int CompareStrings(string left, string right)
        {
            if (TryParseByteSize(left, out var leftBytes) && TryParseByteSize(right, out var rightBytes))
                return leftBytes.CompareTo(rightBytes);

            if (TryParseDisplayDuration(left, out var leftTime)
                && TryParseDisplayDuration(right, out var rightTime))
                return leftTime.CompareTo(rightTime);

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                    var leftDigits = left.AsSpan(leftStart, leftIndex - leftStart).TrimStart('0');
                    var rightDigits = right.AsSpan(rightStart, rightIndex - rightStart).TrimStart('0');
                    var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (lengthComparison != 0) return lengthComparison;

                    var digitComparison = leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
                    if (digitComparison != 0) return digitComparison;
                    continue;
                }

                var characterComparison = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (characterComparison != 0) return characterComparison;
                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }

        /// <summary>
        ///     Video durations are displayed as m:ss below one hour and h:mm:ss
        ///     above it. TimeSpan.TryParse interprets a two-part value as h:mm,
        ///     so parse the app's display contract explicitly.
        /// </summary>
        private static bool TryParseDisplayDuration(string value, out TimeSpan duration)
        {
            duration = default;
            var parts = value.Trim().Split(':');
            if (parts.Length is not (2 or 3)) return false;
            if (!parts.All(static part => int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _)))
            {
                return false;
            }

            var values = parts.Select(static part => int.Parse(
                part,
                NumberStyles.None,
                CultureInfo.InvariantCulture)).ToArray();
            var hours = parts.Length == 3 ? values[0] : 0;
            var minutes = parts.Length == 3 ? values[1] : values[0];
            var seconds = values[^1];
            if (hours < 0 || minutes < 0 || seconds is < 0 or > 59)
                return false;
            if (parts.Length == 3 && minutes > 59)
                return false;

            var totalSeconds = (long)hours * 3600L + (long)minutes * 60L + seconds;
            if (totalSeconds > TimeSpan.MaxValue.TotalSeconds)
                return false;

            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        private static bool TryParseByteSize(string value, out double bytes)
        {
            bytes = 0;
            var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            var number = parts[0].Replace(",", "", StringComparison.Ordinal);
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                && !double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out amount))
                return false;

            var multiplier = parts[1].ToUpperInvariant() switch
            {
                "B" => 1d,
                "KB" => 1024d,
                "MB" => 1024d * 1024,
                "GB" => 1024d * 1024 * 1024,
                _ => 0
            };
            if (multiplier <= 0) return false;
            bytes = amount * multiplier;
            return true;
        }
    }
}
