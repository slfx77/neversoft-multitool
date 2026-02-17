using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class HashReviewerTab : UserControl
{
    private const int PageSize = 10;
    private const string SessionFileName = "review_session.json";

    private string _buildsDir = "";
    private string _candidatesPath = "";
    private List<HashReviewEntry> _allEntries = [];
    private List<HashReviewEntry> _reviewQueue = [];
    private ReviewSession _session = new();
    private int _currentIndex = -1;
    private int _candidatePage;
    private string _candidateFilter = "";

    public HashReviewerTab()
    {
        InitializeComponent();
    }

    // ── File Pickers ────────────────────────────────────────────────────

    private async void BuildsBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _buildsDir = folder.Path;
        BuildsDirText.Text = _buildsDir;
        _session.BuildsDir = _buildsDir;

        TryLoadAndStart();
    }

    private async void CandidatesBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        _candidatesPath = file.Path;
        CandidatesPathText.Text = _candidatesPath;
        _session.CandidatesPath = _candidatesPath;

        TryLoadAndStart();
    }

    // ── Loading ─────────────────────────────────────────────────────────

    private void TryLoadAndStart()
    {
        if (string.IsNullOrEmpty(_buildsDir) || string.IsNullOrEmpty(_candidatesPath))
            return;

        try
        {
            LoadCandidates();
            LoadSession();
            BuildReviewQueue();
            NavigateToCurrentHash();
            UpdateUiState();
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Error loading candidates: {ex.Message}");
        }
    }

    private void LoadCandidates()
    {
        var json = File.ReadAllText(_candidatesPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hashes = root.GetProperty("hashes");

        _allEntries = [];

        foreach (var prop in hashes.EnumerateObject())
        {
            var hashHex = prop.Name;
            var entry = prop.Value;

            var candidates = new List<HashCandidate>();
            if (entry.TryGetProperty("candidates", out var candidatesArr))
            {
                foreach (var c in candidatesArr.EnumerateArray())
                {
                    candidates.Add(new HashCandidate
                    {
                        Name = c.GetProperty("name").GetString() ?? "",
                        Score = c.GetProperty("score").GetInt32(),
                        Length = c.GetProperty("length").GetInt32(),
                    });
                }
            }

            var files = Array.Empty<string>();
            if (entry.TryGetProperty("files", out var filesArr))
            {
                files = filesArr.EnumerateArray()
                    .Select(f => f.GetString() ?? "")
                    .ToArray();
            }

            _allEntries.Add(new HashReviewEntry
            {
                HashHex = hashHex,
                HashValue = Convert.ToUInt32(hashHex.Replace("0x", ""), 16),
                Type = entry.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown",
                Files = files,
                Candidates = candidates,
            });
        }

        MainWindow.Instance?.SetStatus($"Loaded {_allEntries.Count} hashes");
    }

    private void LoadSession()
    {
        var sessionPath = GetSessionPath();
        if (!File.Exists(sessionPath))
        {
            _session = new ReviewSession
            {
                BuildsDir = _buildsDir,
                CandidatesPath = _candidatesPath,
            };
            return;
        }

        try
        {
            var json = File.ReadAllText(sessionPath);
            _session = JsonSerializer.Deserialize<ReviewSession>(json) ?? new ReviewSession();
            _session.BuildsDir = _buildsDir;
            _session.CandidatesPath = _candidatesPath;
        }
        catch
        {
            _session = new ReviewSession
            {
                BuildsDir = _buildsDir,
                CandidatesPath = _candidatesPath,
            };
        }
    }

    private void BuildReviewQueue()
    {
        // Filter out already confirmed/skipped, and already in QbKey dictionary
        _reviewQueue = _allEntries
            .Where(e => !_session.Confirmed.ContainsKey(e.HashHex)
                        && !_session.Skipped.Contains(e.HashHex)
                        && QbKey.TryResolve(e.HashValue) == null)
            .OrderByDescending(e => e.Type == "texture" || e.Type == "both" ? 1 : 0)
            .ThenByDescending(e => e.Candidates.Count > 0 ? e.Candidates[0].Score : -999)
            .ThenByDescending(e => e.Candidates.Count)
            .ToList();

        // Resume from current hash if saved
        _currentIndex = 0;
        if (_session.CurrentHash != null)
        {
            var idx = _reviewQueue.FindIndex(e => e.HashHex == _session.CurrentHash);
            if (idx >= 0)
                _currentIndex = idx;
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────

    private void NavigateToCurrentHash()
    {
        _candidatePage = 0;
        _candidateFilter = "";
        CandidateFilterText.Text = "";

        if (_reviewQueue.Count == 0 || _currentIndex < 0 || _currentIndex >= _reviewQueue.Count)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            ReviewCard.Visibility = Visibility.Collapsed;
            ReviewProgress.Visibility = Visibility.Collapsed;
            UpdateNavigationButtons();
            return;
        }

        var entry = _reviewQueue[_currentIndex];
        _session.CurrentHash = entry.HashHex;

        // Update info card
        HashValueText.Text = entry.HashHex;
        HashTypeText.Text = entry.Type;
        HashFilesText.Text = entry.FilesDisplay;
        HashFilesText.SetValue(ToolTipService.ToolTipProperty, string.Join("\n", entry.Files));

        var totalReviewed = _session.Confirmed.Count + _session.Skipped.Count;
        var total = _allEntries.Count;
        ProgressText.Text = $"Confirmed: {_session.Confirmed.Count} | Reviewed: {totalReviewed} / {total}";

        // Update progress bar
        ReviewProgress.Visibility = Visibility.Visible;
        ReviewProgress.Value = total > 0 ? (double)totalReviewed / total * 100 : 0;

        // Update candidates
        UpdateCandidatesPage();

        // Load texture preview
        LoadTexturePreview(entry);

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ReviewCard.Visibility = Visibility.Visible;
        UpdateNavigationButtons();

        ManualEntryText.Text = "";
    }

    private void CandidateFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _candidateFilter = CandidateFilterText.Text.Trim();
        _candidatePage = 0;
        UpdateCandidatesPage();
    }

    private void UpdateCandidatesPage()
    {
        var entry = _reviewQueue[_currentIndex];
        var allCandidates = entry.Candidates;

        var filtered = string.IsNullOrEmpty(_candidateFilter)
            ? allCandidates
            : allCandidates
                .Where(c => c.Name.Contains(_candidateFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var totalFiltered = filtered.Count;
        var startIdx = _candidatePage * PageSize;
        var pageItems = filtered.Skip(startIdx).Take(PageSize).ToList();

        CandidatesListView.ItemsSource = pageItems;

        if (totalFiltered == 0)
        {
            CandidatesHeaderText.Text = string.IsNullOrEmpty(_candidateFilter)
                ? "Candidates: (none — use manual entry)"
                : $"Candidates: no matches for \"{_candidateFilter}\"";
            PageInfoText.Text = "";
        }
        else
        {
            var endIdx = Math.Min(startIdx + PageSize, totalFiltered);
            var filterSuffix = string.IsNullOrEmpty(_candidateFilter)
                ? ""
                : $", filtered from {allCandidates.Count}";
            CandidatesHeaderText.Text = $"Candidates ({startIdx + 1}-{endIdx} of {totalFiltered}{filterSuffix}):";
            PageInfoText.Text = $"Page {_candidatePage + 1} of {(totalFiltered + PageSize - 1) / PageSize}";
        }

        PrevPageButton.IsEnabled = _candidatePage > 0;
        NextPageButton.IsEnabled = startIdx + PageSize < totalFiltered;
    }

    private async void LoadTexturePreview(HashReviewEntry entry)
    {
        TexturePreview.Source = null;
        TextureDimensionsText.Text = "";
        NoPreviewIcon.Visibility = Visibility.Collapsed;

        // Mesh-only hashes can't be previewed as textures
        if (entry.Type == "mesh")
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            TextureDimensionsText.Text = "Mesh only";
            return;
        }

        if (entry.Files.Length == 0 || string.IsNullOrEmpty(_buildsDir))
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            return;
        }

        // Find all matching PSX files and try each until one decodes
        var diagnostics = new List<string>();
        var result = await Task.Run(() => TryExtractFromAllFiles(entry, diagnostics));

        if (result == null)
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            var fullDiag = string.Join("\n", diagnostics);
            TextureDimensionsText.Text = diagnostics.Count switch
            {
                0 => "No PSX files found",
                1 => diagnostics[0],
                _ => string.Join("; ", diagnostics)
            };
            TextureDimensionsText.SetValue(ToolTipService.ToolTipProperty, fullDiag);
            return;
        }

        var (rgba, width, height) = result.Value;

        // Convert RGBA to BGRA WriteableBitmap
        var bitmap = new WriteableBitmap(width, height);
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];     // B
            bgra[i + 1] = rgba[i + 1]; // G
            bgra[i + 2] = rgba[i];     // R
            bgra[i + 3] = rgba[i + 3]; // A
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        TexturePreview.Source = bitmap;
        TextureDimensionsText.Text = $"{width} x {height}";
    }

    private (byte[] Rgba, int Width, int Height)? TryExtractFromAllFiles(
        HashReviewEntry entry, List<string> diagnostics)
    {
        var filesFound = 0;
        foreach (var psxPath in FindPsxFiles(entry.Files))
        {
            filesFound++;
            var result = PsxLibrary.ExtractTextureByHash(psxPath, entry.HashValue, diagnostics);
            if (result != null)
                return result;
        }

        if (filesFound == 0)
            diagnostics.Insert(0, $"No PSX files found for: {string.Join(", ", entry.Files)}");

        return null;
    }

    private IEnumerable<string> FindPsxFiles(string[] fileNames)
    {
        // Search builds directory for all matching PSX filenames
        foreach (var fileName in fileNames)
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(_buildsDir, fileName, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
                yield return match;
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private void AcceptCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            AcceptName(name);
        }
    }

    private void ManualAccept_Click(object sender, RoutedEventArgs e)
    {
        var name = ManualEntryText.Text.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(name))
        {
            // Verify the hash matches
            var entry = _reviewQueue[_currentIndex];
            var computedHash = QbKey.Hash(name);
            if (computedHash != entry.HashValue)
            {
                MainWindow.Instance?.SetStatus(
                    $"Hash mismatch: '{name}' hashes to 0x{computedHash:X8}, expected {entry.HashHex}");
                return;
            }

            AcceptName(name);
        }
    }

    private void AcceptName(string name)
    {
        if (_currentIndex < 0 || _currentIndex >= _reviewQueue.Count)
            return;

        var entry = _reviewQueue[_currentIndex];
        _session.Confirmed[entry.HashHex] = name;

        MainWindow.Instance?.SetStatus($"Confirmed: {entry.HashHex} = \"{name}\"");

        // Remove from queue and advance
        _reviewQueue.RemoveAt(_currentIndex);
        if (_currentIndex >= _reviewQueue.Count)
            _currentIndex = _reviewQueue.Count - 1;

        SaveSession();
        NavigateToCurrentHash();
    }

    private void SkipHash_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _reviewQueue.Count)
            return;

        var entry = _reviewQueue[_currentIndex];
        _session.Skipped.Add(entry.HashHex);

        _reviewQueue.RemoveAt(_currentIndex);
        if (_currentIndex >= _reviewQueue.Count)
            _currentIndex = _reviewQueue.Count - 1;

        SaveSession();
        NavigateToCurrentHash();
    }

    private void PrevHash_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            NavigateToCurrentHash();
        }
    }

    private void NextHash_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < _reviewQueue.Count - 1)
        {
            _currentIndex++;
            NavigateToCurrentHash();
        }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_candidatePage > 0)
        {
            _candidatePage--;
            UpdateCandidatesPage();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var entry = _reviewQueue[_currentIndex];
        var filteredCount = string.IsNullOrEmpty(_candidateFilter)
            ? entry.Candidates.Count
            : entry.Candidates.Count(c => c.Name.Contains(_candidateFilter, StringComparison.OrdinalIgnoreCase));
        if ((_candidatePage + 1) * PageSize < filteredCount)
        {
            _candidatePage++;
            UpdateCandidatesPage();
        }
    }

    // ── Export ───────────────────────────────────────────────────────────

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Confirmed.Count == 0)
        {
            MainWindow.Instance?.SetStatus("No confirmed names to export");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// Paste into QbKey.cs KnownNames dictionary:");
        foreach (var (hashHex, name) in _session.Confirmed.OrderBy(kv => kv.Value))
        {
            sb.AppendLine($"        [{hashHex}] = \"{name}\",");
        }

        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);

        MainWindow.Instance?.SetStatus(
            $"Copied {_session.Confirmed.Count} dictionary entries to clipboard");
    }

    // ── Session Persistence ─────────────────────────────────────────────

    private void SaveSession()
    {
        try
        {
            var json = JsonSerializer.Serialize(_session, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(GetSessionPath(), json);
        }
        catch
        {
            // Best-effort save
        }
    }

    private string GetSessionPath()
    {
        if (!string.IsNullOrEmpty(_candidatesPath))
        {
            var dir = Path.GetDirectoryName(_candidatesPath) ?? "";
            return Path.Combine(dir, SessionFileName);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeversoftMultitool", SessionFileName);
    }

    // ── UI State ────────────────────────────────────────────────────────

    private void UpdateUiState()
    {
        ExportButton.IsEnabled = _session.Confirmed.Count > 0;
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        var hasData = _reviewQueue.Count > 0 && _currentIndex >= 0;
        PrevHashButton.IsEnabled = hasData && _currentIndex > 0;
        NextHashButton.IsEnabled = hasData && _currentIndex < _reviewQueue.Count - 1;
        SkipButton.IsEnabled = hasData;
    }
}
