using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using TandemHdr.Native;
using TandemHdr.Services;
// WinForms and WPF are both enabled in this project and share the name Brush.
using Brush = System.Windows.Media.Brush;

namespace TandemHdr.Settings;

/// <summary>
/// Lets the user pick from games already installed, instead of hunting through Program Files
/// for an executable whose name they may not know. Two tabs, because the launchers cannot
/// account for everything: scanned libraries, and executables this user has actually run.
/// </summary>
internal partial class GamePickerWindow : Window
{
    /// <summary>One candidate executable, from either source.</summary>
    internal sealed class PickerEntry(string name, string path, string source, string detail)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string Source { get; } = source;

        /// <summary>Always leads with the executable file name: that is what gets matched
        /// against running processes, and the heuristics choosing it are not infallible.</summary>
        public string Detail { get; } = detail;

        public bool IsSelected { get; set; }
    }

    private readonly ObservableCollection<PickerEntry> _visibleGames = [];
    private readonly ObservableCollection<PickerEntry> _visibleRecent = [];
    private List<PickerEntry> _allGames = [];
    private List<PickerEntry> _allRecent = [];

    private readonly HashSet<string> _alreadyAdded;
    private bool _loaded;

    /// <summary>Executables the user chose, empty if they cancelled.</summary>
    public List<string> SelectedPaths { get; private set; } = [];

    public GamePickerWindow(IEnumerable<string> alreadyAdded)
    {
        InitializeComponent();

        _alreadyAdded = alreadyAdded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameList.ItemsSource = _visibleGames;
        RecentList.ItemsSource = _visibleRecent;

        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        DwmApi.SetRoundedCorners(hwnd);

        bool backdropApplied = DwmApi.TrySetAcrylicBackdrop(hwnd) || DwmApi.TrySetMicaBackdrop(hwnd);
        RootGrid.Background = (Brush)FindResource(backdropApplied ? "WindowTintBrush" : "WindowBackgroundBrush");
    }

    private async Task LoadAsync()
    {
        // The recent list is a registry read and lands almost immediately; the library scan
        // walks install directories and takes a couple of seconds. Show each as it arrives
        // rather than making the fast one wait for the slow one.
        var recentTask = RecentPrograms.ListAsync();
        var gamesTask = GameScanner.ScanAsync();

        foreach (var program in await recentTask)
        {
            if (_alreadyAdded.Contains(program.ExecutablePath)) continue;

            string detail = program.LastRun is { } run
                ? $"{System.IO.Path.GetFileName(program.ExecutablePath)}  ·  last run {Describe(run)}"
                : System.IO.Path.GetFileName(program.ExecutablePath);

            // No source chip: the tab itself already says where these came from.
            _allRecent.Add(new PickerEntry(program.Name, program.ExecutablePath, string.Empty, detail));
        }

        _loaded = true;
        ApplyFilter();

        foreach (var game in await gamesTask)
        {
            if (_alreadyAdded.Contains(game.ExecutablePath)) continue;

            string detail = game.LastPlayed is { } played
                ? $"{System.IO.Path.GetFileName(game.ExecutablePath)}  ·  last played {Describe(played)}"
                : System.IO.Path.GetFileName(game.ExecutablePath);

            _allGames.Add(new PickerEntry(game.Name, game.ExecutablePath, game.Source, detail));
        }

        ScanningText.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private static string Describe(DateTime when)
    {
        var ago = DateTime.Now - when;
        return ago.TotalDays switch
        {
            < 1 => "today",
            < 2 => "yesterday",
            < 30 => $"{(int)ago.TotalDays} days ago",
            < 365 => $"{(int)(ago.TotalDays / 30)} months ago",
            _ => when.ToString("MMM yyyy"),
        };
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        // TextChanged fires while the template is still being built, before Loaded runs.
        if (!_loaded) return;

        string term = FilterBox.Text.Trim();

        Fill(_visibleGames, _allGames, term);
        Fill(_visibleRecent, _allRecent, term);

        bool scanning = ScanningText.Visibility == Visibility.Visible;

        GamesScroller.Visibility = _visibleGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGamesText.Visibility = _visibleGames.Count > 0 || scanning ? Visibility.Collapsed : Visibility.Visible;
        NoGamesText.Text = _allGames.Count > 0
            ? "No games match that search."
            : "No games found in your libraries. Try the Recently run tab, or Browse on the Programs tab.";

        RecentScroller.Visibility = _visibleRecent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRecentText.Visibility = _visibleRecent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NoRecentText.Text = _allRecent.Count > 0
            ? "No programs match that search."
            : "Nothing recorded yet. Windows remembers programs you launch, so this fills in over time.";

        UpdateCount();
    }

    private static void Fill(ObservableCollection<PickerEntry> target, List<PickerEntry> source, string term)
    {
        target.Clear();
        foreach (var entry in source)
        {
            if (term.Length == 0 ||
                entry.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Path.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                target.Add(entry);
        }
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        // Selection spans both tabs, and survives filtering: a search that hides a checked row
        // must not silently drop it from what gets added.
        int selected = _allGames.Count(c => c.IsSelected) + _allRecent.Count(c => c.IsSelected);

        AddButton.IsEnabled = selected > 0;
        CountText.Text = selected > 0
            ? $"{selected} selected"
            : $"{_allGames.Count} games · {_allRecent.Count} recent programs";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        SelectedPaths = _allGames.Concat(_allRecent)
            .Where(c => c.IsSelected)
            .Select(c => c.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
