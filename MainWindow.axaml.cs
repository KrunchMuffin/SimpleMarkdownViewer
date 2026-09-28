using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace SimpleMarkdownViewer;

public partial class MainWindow : Window
{
    private readonly NativeWebView _webView;
    private readonly TextBlock _statusText;
    private readonly Border _statusBar;
    private readonly Border _tabStrip;
    private readonly StackPanel _tabPanel;
    private readonly ScrollViewer _tabScrollViewer;
    private readonly Button _tabScrollLeft;
    private readonly Button _tabScrollRight;
    private readonly Button _tabDropdown;
    private readonly MenuItem _themeMenuItem;
    private readonly MenuItem _lineNumbersMenuItem;
    private readonly MenuItem _autoUpdateCheckMenuItem;
    private readonly Border _updateBanner;
    private readonly TextBlock _updateBannerText;
    private readonly DockPanel _mainPanel;
    private readonly MenuItem _recentMenu = null!;
    private readonly MarkdownRenderer _markdownRenderer = new();
    private readonly object _markdownRenderLock = new();
    
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleMarkdownViewer");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private bool _isDarkMode = false;
    private bool _showPreviewLineNumbers = false;

    // Update check state, persisted in settings
    private bool _checkForUpdates = true;
    private DateTime? _lastUpdateCheckUtc;
    private string? _latestKnownVersion;
    private string? _latestKnownReleaseUrl;
    private string? _dismissedUpdateVersion;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);
    private bool _webViewReady = false;
    private string? _welcomeTempHtmlPath;
    private int _renderVersion;

    // Identifies the page the WebView has loaded, so re-renders of the same tab
    // can swap content in place instead of reloading (keeps scroll, skips
    // re-rendering unchanged diagrams)
    private sealed record PreviewPageKey(TabState Tab, string FilePath, bool IsDarkMode, bool ShowLineNumbers, string CustomCss);
    private PreviewPageKey? _loadedPageKey;
    private int _readyRenderVersion = -1;
    private const string NewFilePlaceholderHtml = "<p><em>Start typing in the editor...</em></p>";
    
    private readonly List<string> _recentFiles = new();
    private const int MaxRecentFiles = 10;
    private static readonly string[] MarkdownFilePatterns = { "*.md", "*.markdown", "*.mdown", "*.mkd", "*.mkdn", "*.mdwn", "*.mdtxt", "*.mdtext", "*.mdx", "*.rmd" };
    private static readonly string[] MermaidFilePatterns = { "*.mmd", "*.mermaid" };
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext", ".mdx", ".rmd" };
    private static readonly string[] MermaidExtensions = { ".mmd", ".mermaid" };
    
    private readonly List<TabState> _tabs = new();
    private int _selectedTabIndex = -1;

    // Editor controls
    private readonly TextEditor _textEditor;
    private readonly GridSplitter _editorSplitter;
    private readonly ColumnDefinition _editorColumn;
    private readonly ColumnDefinition _splitterColumn;
    private readonly MenuItem _editModeMenuItem;
    private readonly MenuItem _saveMenuItem;
    private readonly MenuItem _saveAsMenuItem;

    // Edit mode state
    private bool _isEditMode;
    private TextMate.Installation? _textMateInstallation;
    private Timer? _previewDebounceTimer;
    private const int PreviewDebounceMs = 300;
    private const int WatcherDebounceMs = 250;
    private int _untitledCounter;
    private int _editorRenderRequestId;

    // CSP nonce for preview pages; also authenticates app:// commands from them
    private readonly string _pageNonce = PreviewPage.CreateNonce();

    // Single-instance pipe server
    private CancellationTokenSource? _pipeCts;

    private class TabState
    {
        public string FilePath { get; set; } = "";
        public string FileName => IsNewFile ? (DisplayName ?? "Untitled") : Path.GetFileName(FilePath);
        public string TempHtmlPath { get; set; } = "";
        public string? CachedBody { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public CancellationTokenSource? WatcherDebounce;
        public Button? TabButton { get; set; }
        public TextBlock? TabText { get; set; }

        // Edit mode fields
        public bool IsModified { get; set; }
        public string OriginalContent { get; set; } = "";  // last content read from or saved to disk
        public string EditContent { get; set; } = "";
        public bool HasLoadedEditor { get; set; }
        public bool IsNewFile { get; set; }
        public string? DisplayName { get; set; }
    }

    private class AppSettings
    {
        public bool IsDarkMode { get; set; } = false;
        public bool ShowPreviewLineNumbers { get; set; } = false;
        public List<string> RecentFiles { get; set; } = new();
        public bool CheckForUpdates { get; set; } = true;
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string? LatestKnownVersion { get; set; }
        public string? LatestKnownReleaseUrl { get; set; }
        public string? DismissedUpdateVersion { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    _isDarkMode = settings.IsDarkMode;
                    _showPreviewLineNumbers = settings.ShowPreviewLineNumbers;
                    _recentFiles.Clear();
                    _recentFiles.AddRange(settings.RecentFiles.Where(File.Exists).Take(MaxRecentFiles));
                    _checkForUpdates = settings.CheckForUpdates;
                    _lastUpdateCheckUtc = settings.LastUpdateCheckUtc;
                    _latestKnownVersion = settings.LatestKnownVersion;
                    _latestKnownReleaseUrl = settings.LatestKnownReleaseUrl;
                    _dismissedUpdateVersion = settings.DismissedUpdateVersion;
                }
            }
        }
        catch { }

        // Apply theme
        ApplyTheme();
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            var settings = new AppSettings 
            { 
                IsDarkMode = _isDarkMode,
                ShowPreviewLineNumbers = _showPreviewLineNumbers,
                RecentFiles = _recentFiles.ToList(),
                CheckForUpdates = _checkForUpdates,
                LastUpdateCheckUtc = _lastUpdateCheckUtc,
                LatestKnownVersion = _latestKnownVersion,
                LatestKnownReleaseUrl = _latestKnownReleaseUrl,
                DismissedUpdateVersion = _dismissedUpdateVersion
            };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private void AddToRecentFiles(string filePath)
    {
        var existingIndex = _recentFiles.FindIndex(path =>
            string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex == 0)
            return;

        if (existingIndex > 0)
            _recentFiles.RemoveAt(existingIndex);
        
        // Insert at beginning
        _recentFiles.Insert(0, filePath);
        
        // Trim to max
        while (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);
        
        SaveSettings();
        Dispatcher.UIThread.Post(UpdateRecentMenu, DispatcherPriority.Background);
    }

    private void UpdateRecentMenu()
    {
        _recentMenu.Items.Clear();
        
        if (_recentFiles.Count == 0)
        {
            var emptyItem = new MenuItem { Header = "(No recent files)", IsEnabled = false };
            _recentMenu.Items.Add(emptyItem);
            return;
        }
        
        foreach (var filePath in _recentFiles)
        {
            var item = new MenuItem { Header = filePath };
            var path = filePath; // Capture for closure
            item.Click += async (s, e) =>
            {
                if (File.Exists(path))
                    await OpenFileInNewTab(path);
                else
                {
                    _recentFiles.Remove(path);
                    SaveSettings();
                    UpdateRecentMenu();
                    _statusText.Text = "File not found: " + path;
                }
            };
            _recentMenu.Items.Add(item);
        }
        
        // Add separator and clear option
        _recentMenu.Items.Add(new Separator());
        var clearItem = new MenuItem { Header = "Clear Recent Files" };
        clearItem.Click += (s, e) =>
        {
            _recentFiles.Clear();
            SaveSettings();
            UpdateRecentMenu();
        };
        _recentMenu.Items.Add(clearItem);
    }

    private void ApplyTheme()
    {
        _themeMenuItem.Header = _isDarkMode ? "_Light Mode" : "_Dark Mode";
        _lineNumbersMenuItem.Header = _showPreviewLineNumbers ? "Hide Preview _Line Numbers" : "Preview _Line Numbers";
        RequestedThemeVariant = _isDarkMode ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        _mainPanel.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#1e1e1e" : "#ffffff"));
        _statusBar.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#1e1e1e" : "#f0f0f0"));
        _statusText.Foreground = _isDarkMode ? Brushes.White : Brushes.Black;
        _tabStrip.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#252525" : "#e0e0e0"));
        var tabBtnFg = new SolidColorBrush(Color.Parse(_isDarkMode ? "#aaaaaa" : "#555555"));
        _tabScrollLeft.Foreground = tabBtnFg;
        _tabScrollRight.Foreground = tabBtnFg;
        _tabDropdown.Foreground = tabBtnFg;
        _updateBanner.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#0c2d4a" : "#ddf4ff"));
        _updateBannerText.Foreground = new SolidColorBrush(Color.Parse(_isDarkMode ? "#e6edf3" : "#1f2328"));
        _autoUpdateCheckMenuItem.IsChecked = _checkForUpdates;
    }

    public MainWindow()
    {
        InitializeComponent();

        _webView = this.FindControl<NativeWebView>("WebView")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _statusBar = this.FindControl<Border>("StatusBar")!;
        _tabStrip = this.FindControl<Border>("TabStrip")!;
        _tabPanel = this.FindControl<StackPanel>("TabPanel")!;
        _tabScrollViewer = this.FindControl<ScrollViewer>("TabScrollViewer")!;
        _tabScrollLeft = this.FindControl<Button>("TabScrollLeft")!;
        _tabScrollRight = this.FindControl<Button>("TabScrollRight")!;
        _tabDropdown = this.FindControl<Button>("TabDropdown")!;
        _themeMenuItem = this.FindControl<MenuItem>("ThemeMenuItem")!;
        _lineNumbersMenuItem = this.FindControl<MenuItem>("LineNumbersMenuItem")!;
        _autoUpdateCheckMenuItem = this.FindControl<MenuItem>("AutoUpdateCheckMenuItem")!;
        _updateBanner = this.FindControl<Border>("UpdateBanner")!;
        _updateBannerText = this.FindControl<TextBlock>("UpdateBannerText")!;
        _mainPanel = this.FindControl<DockPanel>("MainPanel")!;
        _recentMenu = this.FindControl<MenuItem>("RecentMenu")!;

        // Editor controls
        _textEditor = this.FindControl<TextEditor>("TextEditor")!;
        _editorSplitter = this.FindControl<GridSplitter>("EditorSplitter")!;
        var contentGrid = this.FindControl<Grid>("ContentGrid")!;
        _editorColumn = contentGrid.ColumnDefinitions[0];
        _splitterColumn = contentGrid.ColumnDefinitions[1];
        _editModeMenuItem = this.FindControl<MenuItem>("EditModeMenuItem")!;
        _saveMenuItem = this.FindControl<MenuItem>("SaveMenuItem")!;
        _saveAsMenuItem = this.FindControl<MenuItem>("SaveAsMenuItem")!;

        // Tab scroll overflow detection
        _tabScrollViewer.ScrollChanged += (s, e) => UpdateTabScrollButtons();
        this.SizeChanged += (s, e) => UpdateTabScrollButtons();

        // Load settings
        LoadSettings();
        UpdateRecentMenu();

        // Set up AvaloniaEdit TextMate for markdown highlighting
        SetupTextMateTheme();

        // Set up editor context menu
        SetupEditorContextMenu();

        // Wire up editor text change events
        _textEditor.TextChanged += OnEditorTextChanged;

        // Wire up WebView events
        _webView.EnvironmentRequested += OnWebViewEnvironmentRequested;
        _webView.AdapterCreated += OnWebViewCreated;
        _webView.NavigationStarted += OnNavigationStarting;
        _webView.WebMessageReceived += OnWebMessageReceived;
        // Links are routed through the host by the preview script; never open popups
        _webView.NewWindowRequested += (s, e) => e.Handled = true;

        this.KeyDown += OnKeyDown;

        // Enable drag and drop (use Tunnel to intercept before WebView)
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel);

        // Handle command line args
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            _ = OpenFileInNewTab(args[1]);
        }

        // Start named pipe server for single-instance file opening
        StartPipeServer();

        _ = Task.Run(DeleteOrphanedTempFiles);

        Opened += (s, e) => _ = CheckForUpdatesOnStartupAsync();
    }

    // ===================== Update Check =====================

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_checkForUpdates) return;

        // Show what the last check found right away; only ask GitHub again once a day
        ShowUpdateBannerIfNewer();
        if (_lastUpdateCheckUtc is { } last && DateTime.UtcNow - last < UpdateCheckInterval)
            return;

        try
        {
            await RefreshLatestReleaseAsync();
            ShowUpdateBannerIfNewer();
        }
        catch
        {
            // Offline or GitHub unreachable; try again next launch
        }
    }

    private async Task RefreshLatestReleaseAsync()
    {
        var release = await UpdateChecker.GetLatestReleaseAsync();
        _latestKnownVersion = release.Version.ToString();
        _latestKnownReleaseUrl = release.PageUrl;
        _lastUpdateCheckUtc = DateTime.UtcNow;
        SaveSettings();
    }

    private bool ShowUpdateBannerIfNewer(bool ignoreDismissed = false)
    {
        if (!UpdateChecker.TryParseVersion(_latestKnownVersion, out var latest) || !UpdateChecker.IsNewerThanCurrent(latest))
        {
            _updateBanner.IsVisible = false;
            return false;
        }

        if (!ignoreDismissed && _dismissedUpdateVersion == latest.ToString())
            return true;

        _updateBannerText.Text = $"Simple Markdown Viewer {latest} is available. You have {UpdateChecker.CurrentVersion}.";
        _updateBanner.IsVisible = true;
        return true;
    }

    private async void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
    {
        _statusText.Text = "Checking for updates...";
        try
        {
            await RefreshLatestReleaseAsync();
            _statusText.Text = ShowUpdateBannerIfNewer(ignoreDismissed: true)
                ? $"Version {_latestKnownVersion} is available."
                : $"You're up to date (version {UpdateChecker.CurrentVersion}).";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Couldn't check for updates: {ex.Message}";
        }
    }

    private void OnToggleAutoUpdateCheckClick(object? sender, RoutedEventArgs e)
    {
        _checkForUpdates = !_checkForUpdates;
        _autoUpdateCheckMenuItem.IsChecked = _checkForUpdates;
        SaveSettings();
        _statusText.Text = _checkForUpdates
            ? "Automatic update checks turned on."
            : "Automatic update checks turned off. Use Help > Check for Updates to check manually.";
    }

    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e)
    {
        OpenLink(_latestKnownReleaseUrl ?? UpdateChecker.ReleasesPageUrl);
    }

    private void OnDismissUpdateClick(object? sender, RoutedEventArgs e)
    {
        // Stay quiet about this version; a newer release will show the notice again
        _dismissedUpdateVersion = _latestKnownVersion;
        _updateBanner.IsVisible = false;
        SaveSettings();
    }

    private static void DeleteOrphanedTempFiles()
    {
        // Preview files are normally removed on close; a crash or kill leaves them behind
        try
        {
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "mdviewer_*.html"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff)
                        File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var ct = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        "SimpleMarkdownViewer_Pipe",
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server);
                    var filePath = await reader.ReadLineAsync();

                    if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await OpenFileInNewTab(filePath);

                            // Bring window to front
                            if (WindowState == WindowState.Minimized)
                                WindowState = WindowState.Normal;
                            Activate();
                            Topmost = true;
                            Topmost = false;
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Brief delay before retrying on error
                    try { await Task.Delay(100, ct); } catch { break; }
                }
            }
        }, ct);
    }

    private void SetupTextMateTheme()
    {
        _textMateInstallation?.Dispose();
        var registryOptions = new RegistryOptions(
            _isDarkMode ? ThemeName.DarkPlus : ThemeName.LightPlus);
        _textMateInstallation = _textEditor.InstallTextMate(registryOptions);
        var mdLang = registryOptions.GetLanguageByExtension(".md");
        if (mdLang != null)
        {
            _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId(mdLang.Id));
        }
        _textEditor.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#1e1e1e" : "#ffffff"));
        _textEditor.Foreground = new SolidColorBrush(Color.Parse(_isDarkMode ? "#d4d4d4" : "#1e1e1e"));
    }

    private void SetupEditorContextMenu()
    {
        var cutItem = new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cutItem.Click += (s, e) => _textEditor.Cut();

        var copyItem = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copyItem.Click += (s, e) => _textEditor.Copy();

        var pasteItem = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        pasteItem.Click += (s, e) => _textEditor.Paste();

        var selectAllItem = new MenuItem { Header = "Select All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAllItem.Click += (s, e) => _textEditor.SelectAll();

        // Format submenu
        var boldItem = new MenuItem { Header = "Bold", InputGesture = new KeyGesture(Key.B, KeyModifiers.Control) };
        boldItem.Click += (s, e) => WrapSelection("**", "**", "bold text");

        var italicItem = new MenuItem { Header = "Italic", InputGesture = new KeyGesture(Key.I, KeyModifiers.Control) };
        italicItem.Click += (s, e) => WrapSelection("*", "*", "italic text");

        var strikeItem = new MenuItem { Header = "Strikethrough" };
        strikeItem.Click += (s, e) => WrapSelection("~~", "~~", "strikethrough");

        var codeItem = new MenuItem { Header = "Inline Code" };
        codeItem.Click += (s, e) => WrapSelection("`", "`", "code");

        var codeBlockItem = new MenuItem { Header = "Code Block" };
        codeBlockItem.Click += (s, e) => WrapSelection("```\n", "\n```", "code");

        var linkItem = new MenuItem { Header = "Link" };
        linkItem.Click += (s, e) => InsertLink();

        var imageItem = new MenuItem { Header = "Image" };
        imageItem.Click += (s, e) => InsertMarkdown("![alt text](url)");

        var h1Item = new MenuItem { Header = "Heading 1" };
        h1Item.Click += (s, e) => PrefixLine("# ");

        var h2Item = new MenuItem { Header = "Heading 2" };
        h2Item.Click += (s, e) => PrefixLine("## ");

        var h3Item = new MenuItem { Header = "Heading 3" };
        h3Item.Click += (s, e) => PrefixLine("### ");

        var bulletItem = new MenuItem { Header = "Bullet List" };
        bulletItem.Click += (s, e) => PrefixLine("- ");

        var numberItem = new MenuItem { Header = "Numbered List" };
        numberItem.Click += (s, e) => PrefixLine("1. ");

        var quoteItem = new MenuItem { Header = "Blockquote" };
        quoteItem.Click += (s, e) => PrefixLine("> ");

        var taskItem = new MenuItem { Header = "Task List" };
        taskItem.Click += (s, e) => PrefixLine("- [ ] ");

        var hrItem = new MenuItem { Header = "Horizontal Rule" };
        hrItem.Click += (s, e) => InsertMarkdown("\n---\n");

        var formatMenu = new MenuItem
        {
            Header = "Format",
            Items =
            {
                boldItem, italicItem, strikeItem,
                new Separator(),
                codeItem, codeBlockItem,
                new Separator(),
                linkItem, imageItem,
                new Separator(),
                h1Item, h2Item, h3Item,
                new Separator(),
                bulletItem, numberItem, quoteItem, taskItem,
                new Separator(),
                hrItem
            }
        };

        _textEditor.ContextMenu = new ContextMenu
        {
            Items = { cutItem, copyItem, pasteItem, new Separator(), selectAllItem, new Separator(), formatMenu }
        };
    }

    private void WrapSelection(string before, string after, string placeholder)
    {
        var doc = _textEditor.Document;
        var offset = _textEditor.SelectionStart;
        var length = _textEditor.SelectionLength;

        if (length > 0)
        {
            var selected = doc.GetText(offset, length);
            var replacement = before + selected + after;
            doc.Replace(offset, length, replacement);
            // Select the wrapped text (without the markers)
            _textEditor.Select(offset + before.Length, selected.Length);
        }
        else
        {
            var text = before + placeholder + after;
            doc.Insert(offset, text);
            // Select the placeholder so user can type over it
            _textEditor.Select(offset + before.Length, placeholder.Length);
        }
        _textEditor.Focus();
    }

    private void InsertLink()
    {
        var doc = _textEditor.Document;
        var offset = _textEditor.SelectionStart;
        var length = _textEditor.SelectionLength;

        if (length > 0)
        {
            var selected = doc.GetText(offset, length);
            var replacement = $"[{selected}](url)";
            doc.Replace(offset, length, replacement);
            // Select "url" so user can type the URL
            _textEditor.Select(offset + selected.Length + 3, 3);
        }
        else
        {
            var text = "[link text](url)";
            doc.Insert(offset, text);
            _textEditor.Select(offset + 1, 9); // Select "link text"
        }
        _textEditor.Focus();
    }

    private void InsertMarkdown(string markdown)
    {
        var doc = _textEditor.Document;
        var offset = _textEditor.SelectionStart;
        doc.Insert(offset, markdown);
        _textEditor.CaretOffset = offset + markdown.Length;
        _textEditor.Focus();
    }

    private void PrefixLine(string prefix)
    {
        var doc = _textEditor.Document;
        var offset = _textEditor.SelectionStart;
        var length = _textEditor.SelectionLength;

        if (length > 0)
        {
            // Prefix each selected line
            var startLine = doc.GetLineByOffset(offset);
            var endLine = doc.GetLineByOffset(offset + length);
            // Work backwards to preserve offsets
            for (int lineNum = endLine.LineNumber; lineNum >= startLine.LineNumber; lineNum--)
            {
                var line = doc.GetLineByNumber(lineNum);
                doc.Insert(line.Offset, prefix);
            }
        }
        else
        {
            // Prefix the current line
            var line = doc.GetLineByOffset(offset);
            doc.Insert(line.Offset, prefix);
            _textEditor.CaretOffset = offset + prefix.Length;
        }
        _textEditor.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        
        switch (e.Key)
        {
            case Key.N when ctrl && !shift:
                OnNewFileClick(null, null!);
                e.Handled = true;
                break;
            case Key.O when ctrl:
                OnOpenClick(null, null!);
                e.Handled = true;
                break;
            case Key.S when ctrl && !shift:
                OnSaveClick(null, null!);
                e.Handled = true;
                break;
            case Key.S when ctrl && shift:
                OnSaveAsClick(null, null!);
                e.Handled = true;
                break;
            case Key.E when ctrl:
                OnToggleEditModeClick(null, null!);
                e.Handled = true;
                break;
            case Key.B when ctrl && _isEditMode:
                WrapSelection("**", "**", "bold text");
                e.Handled = true;
                break;
            case Key.I when ctrl && _isEditMode:
                WrapSelection("*", "*", "italic text");
                e.Handled = true;
                break;
            case Key.K when ctrl && _isEditMode:
                InsertLink();
                e.Handled = true;
                break;
            case Key.W when ctrl:
                OnCloseTabClick(null, null!);
                e.Handled = true;
                break;
            case Key.C when ctrl && shift:
                OnCopyMarkdownClick(null, null!);
                e.Handled = true;
                break;
            case Key.V when ctrl && shift:
                OnNewFromClipboardClick(null, null!);
                e.Handled = true;
                break;
            case Key.P when ctrl:
                OnSaveAsPdfClick(null, null!);
                e.Handled = true;
                break;
            case Key.F5:
                OnRefreshClick(null, null!);
                e.Handled = true;
                break;
            case Key.F12:
                OnDevToolsClick(null, null!);
                e.Handled = true;
                break;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        e.Handled = true;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            
            if (IsSupportedDocumentFile(path))
            {
                await OpenFileInNewTab(path);
            }
        }
    }

    private static void OnWebViewEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = true;

        // Keep the browser profile with our settings; the default location next to the
        // exe is not writable when installed under Program Files
        if (e is WindowsWebView2EnvironmentRequestedEventArgs webView2)
            webView2.UserDataFolder = Path.Combine(SettingsDir, "WebView2Data");
    }

    private void OnWebViewCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _webViewReady = true;
        if (_tabs.Count == 0)
            _statusText.Text = "Ready - Open a markdown or Mermaid file (Ctrl+O)";

        RefreshPreview();
    }

    private string GetWelcomePage()
    {
        var bgColor = _isDarkMode ? "#0d1117" : "#ffffff";
        var textColor = _isDarkMode ? "#8b949e" : "#656d76";
        return $@"<!DOCTYPE html>
<html><head><meta charset=""UTF-8""><style>
    body {{ background-color: {bgColor}; color: {textColor}; font-family: -apple-system, sans-serif; 
           display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; }}
    .welcome {{ text-align: center; }}
    kbd {{ padding: 2px 6px; background: {(_isDarkMode ? "#21262d" : "#f6f8fa")}; border-radius: 4px; }}
    p {{ margin: 8px 0; }}
</style></head>
<body><div class='welcome'>
    <h2>Simple Markdown Viewer</h2>
    <p>Press <kbd>Ctrl</kbd>+<kbd>O</kbd> to open a file</p>
    <p>or drag and drop markdown or Mermaid files here</p>
</div></body></html>";
    }

    private static bool HasExtension(string filePath, IEnumerable<string> extensions)
    {
        var ext = Path.GetExtension(filePath);
        return extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMarkdownFile(string filePath) => HasExtension(filePath, MarkdownExtensions);

    private static bool IsMermaidFile(string filePath) => HasExtension(filePath, MermaidExtensions);

    private static bool IsSupportedDocumentFile(string filePath) => IsMarkdownFile(filePath) || IsMermaidFile(filePath);

    private void OnNavigationStarting(object? sender, WebViewNavigationStartingEventArgs e)
    {
        var url = e.Request?.ToString() ?? "";

        // Handle app:// commands from the preview's JavaScript (context menu, link clicks)
        if (url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(() => HandleAppCommand(url));
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        // Never let the preview itself navigate to the web; link clicks are routed
        // through app://open-link so only user-initiated clicks open a browser
        if (uri.Scheme is "http" or "https")
        {
            e.Cancel = true;
            return;
        }

        // Check if this is a file being dragged onto WebView
        if (uri.IsFile)
        {
            var filePath = uri.LocalPath;

            // Skip our own temp HTML files
            if (filePath.Contains("mdviewer_") && filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return;

            if (IsSupportedDocumentFile(filePath) && File.Exists(filePath))
            {
                // Cancel the WebView navigation
                e.Cancel = true;

                // Open in a new tab instead
                _ = OpenFileInNewTab(filePath);
            }
        }
    }

    // The preview sends its app:// commands as web messages where the WebView supports
    // them; WebKitGTK shows an error page for app:// navigations instead of letting us cancel
    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var message = e.Body;
        if (message != null && message.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            Dispatcher.UIThread.Post(() => HandleAppCommand(message));
    }

    private void HandleAppCommand(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        // Reject commands that did not come from our own preview script
        if (GetQueryValue(uri, "token") != _pageNonce)
            return;

        switch (uri.Host.ToLowerInvariant())
        {
            case "toggle-edit":
                OnToggleEditModeClick(null, null!);
                break;
            case "ready":
                if (int.TryParse(GetQueryValue(uri, "v"), out var version))
                    _readyRenderVersion = version;
                break;
            case "open-link":
                var target = GetQueryValue(uri, "url");
                if (target != null)
                    OpenLink(target);
                break;
        }
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key)
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private void OpenLink(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return;

        if (uri.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Could not open link: {ex.Message}";
            }
            return;
        }

        if (uri.IsFile)
        {
            // Only open documents we can display; never shell-execute arbitrary local files from a link
            var path = uri.LocalPath;
            if (IsSupportedDocumentFile(path) && File.Exists(path))
                _ = OpenFileInNewTab(path);
            else
                _statusText.Text = $"Link target not opened: {path}";
        }
    }

    private string GetCustomCssTag()
    {
        var cssFile = _isDarkMode ? "custom-dark.css" : "custom-light.css";
        var cssPath = Path.Combine(SettingsDir, cssFile);
        if (File.Exists(cssPath))
        {
            try
            {
                var css = File.ReadAllText(cssPath, Encoding.UTF8);
                return $"<style>\n/* Custom CSS: {cssFile} */\n{css}\n</style>";
            }
            catch { }
        }
        return "";
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Markdown or Mermaid File",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Markdown and Mermaid Files") { Patterns = MarkdownFilePatterns.Concat(MermaidFilePatterns).ToArray() },
                    new FilePickerFileType("Markdown Files") { Patterns = MarkdownFilePatterns },
                    new FilePickerFileType("Mermaid Files") { Patterns = MermaidFilePatterns },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                }
            });

            foreach (var file in files)
            {
                await OpenFileInNewTab(file.Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Open failed: {ex.Message}";
        }
    }

    private async void OnNewFromClipboardClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                _statusText.Text = "Clipboard not available";
                return;
            }
            
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                _statusText.Text = "Clipboard is empty or contains no text";
                return;
            }
            
            // Open as an untitled document; Save prompts for a location
            var tab = CreateUntitledTab("Clipboard", text);
            await GenerateHtml(tab);
            SelectTab(_tabs.IndexOf(tab));
            _statusText.Text = $"Opened markdown from clipboard ({text.Length} chars)";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Clipboard paste failed: {ex.Message}";
        }
    }

    private async Task OpenFileInNewTab(string filePath)
    {
        // Check if already open
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].FilePath == filePath)
            {
                SelectTab(i);
                return;
            }
        }
        
        var tab = new TabState
        {
            FilePath = filePath,
            TempHtmlPath = Path.Combine(Path.GetTempPath(), $"mdviewer_{Guid.NewGuid():N}.html")
        };
        
        // Create tab button
        var button = CreateTabButton(tab);
        tab.TabButton = button;
        _tabPanel.Children.Add(button);
        UpdateTabScrollButtons();

        _tabs.Add(tab);
        
        // Set up file watcher
        SetupFileWatcher(tab);
        
        // Generate initial HTML
        await GenerateHtml(tab);
        
        // Select this tab
        SelectTab(_tabs.Count - 1);
        
        // Add to recent files
        AddToRecentFiles(filePath);
    }

    private Button CreateTabButton(TabState tab)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var textBlock = new TextBlock 
        { 
            Text = tab.FileName, 
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(_isDarkMode ? "#ffffff" : "#000000"))
        };
        tab.TabText = textBlock;
        var closeBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(2, 0),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        panel.Children.Add(textBlock);
        panel.Children.Add(closeBtn);
        
        var button = new Button
        {
            Content = panel,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0),
            Background = new SolidColorBrush(Color.Parse("#d0d0d0"))
        };
        
        button.Click += (s, e) =>
        {
            var idx = _tabs.IndexOf(tab);
            if (idx >= 0) SelectTab(idx);
        };

        closeBtn.Click += async (s, e) =>
        {
            e.Handled = true;
            await CloseTab(tab);
        };

        // Right-click context menu
        var ctxClose = new MenuItem { Header = "Close" };
        ctxClose.Click += async (s, e) => await CloseTab(tab);

        var ctxCloseOthers = new MenuItem { Header = "Close Others" };
        ctxCloseOthers.Click += async (s, e) => await CloseOtherTabs(tab);

        var ctxCloseRight = new MenuItem { Header = "Close to the Right" };
        ctxCloseRight.Click += async (s, e) => await CloseTabsToTheRight(tab);

        var ctxCloseAll = new MenuItem { Header = "Close All" };
        ctxCloseAll.Click += async (s, e) => await CloseAllTabs();

        button.ContextMenu = new ContextMenu
        {
            Items = { ctxClose, ctxCloseOthers, ctxCloseRight, new Separator(), ctxCloseAll }
        };

        return button;
    }

    private void UpdateTabScrollButtons()
    {
        var hasOverflow = _tabScrollViewer.Extent.Width > _tabScrollViewer.Viewport.Width;
        _tabScrollLeft.IsVisible = hasOverflow;
        _tabScrollRight.IsVisible = hasOverflow;
    }

    private void OnTabScrollLeftClick(object? sender, RoutedEventArgs e)
    {
        var newOffset = Math.Max(0, _tabScrollViewer.Offset.X - 150);
        _tabScrollViewer.Offset = new Vector(newOffset, 0);
    }

    private void OnTabScrollRightClick(object? sender, RoutedEventArgs e)
    {
        var maxOffset = _tabScrollViewer.Extent.Width - _tabScrollViewer.Viewport.Width;
        var newOffset = Math.Min(maxOffset, _tabScrollViewer.Offset.X + 150);
        _tabScrollViewer.Offset = new Vector(newOffset, 0);
    }

    private void OnTabDropdownClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var idx = i;
            var name = tab.FileName;
            if (tab.IsModified) name = "* " + name;
            var item = new MenuItem { Header = name };
            if (i == _selectedTabIndex)
                item.Icon = new TextBlock { Text = "●", FontSize = 8, VerticalAlignment = VerticalAlignment.Center };
            item.Click += (s, args) => SelectTab(idx);
            menu.Items.Add(item);
        }
        menu.Open(_tabDropdown);
    }

    private void ScrollSelectedTabIntoView()
    {
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            var btn = _tabs[_selectedTabIndex].TabButton;
            btn?.BringIntoView();
        }
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        // Save current editor content back to the outgoing tab before switching
        if (_isEditMode && _selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            _tabs[_selectedTabIndex].EditContent = _textEditor.Text ?? "";
        }

        _selectedTabIndex = index;
        var tab = _tabs[index];

        // Update tab button appearances
        for (int i = 0; i < _tabs.Count; i++)
        {
            var btn = _tabs[i].TabButton;
            if (btn != null)
            {
                btn.Background = i == index
                    ? new SolidColorBrush(Color.Parse(_isDarkMode ? "#3a3a3a" : "#ffffff"))
                    : new SolidColorBrush(Color.Parse(_isDarkMode ? "#2a2a2a" : "#d0d0d0"));
            }
        }

        // Update status and title
        _statusText.Text = tab.IsNewFile ? (tab.DisplayName ?? "Untitled") : tab.FilePath;
        Title = $"Simple Markdown Viewer - {(tab.IsModified ? "* " : "")}{tab.FileName}";

        // Load editor content if in edit mode
        if (_isEditMode)
        {
            LoadCurrentTabIntoEditor();
        }

        // Load preview content
        if (tab.CachedBody != null || tab.IsNewFile)
            ShowTabPreview(tab);

        ScrollSelectedTabIntoView();
    }

    private async Task<bool> CloseTab(TabState tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return true;

        // Check for unsaved changes
        if (tab.IsModified)
        {
            var result = await ShowUnsavedChangesDialog(tab.FileName);
            if (result == "Save")
            {
                var saved = await SaveTab(tab);
                if (!saved) return false;
            }
            else if (result == "Cancel")
            {
                return false;
            }
        }

        index = _tabs.IndexOf(tab);
        if (index < 0) return true;

        // Clean up
        tab.WatcherDebounce?.Cancel();
        tab.Watcher?.Dispose();
        try { if (File.Exists(tab.TempHtmlPath)) File.Delete(tab.TempHtmlPath); } catch { }

        // Remove from UI
        if (tab.TabButton != null)
            _tabPanel.Children.Remove(tab.TabButton);
        UpdateTabScrollButtons();

        _tabs.RemoveAt(index);

        // Select another tab
        if (_tabs.Count == 0)
        {
            _selectedTabIndex = -1;
            _statusText.Text = "Ready - Open a markdown or Mermaid file (Ctrl+O)";
            Title = "Simple Markdown Viewer";
            ShowWelcome();

            // Hide editor if no tabs
            if (_isEditMode)
            {
                OnToggleEditModeClick(null, null!);
            }
        }
        else
        {
            SelectTab(Math.Min(index, _tabs.Count - 1));
        }

        return true;
    }

    private async Task CloseOtherTabs(TabState keepTab)
    {
        var toClose = _tabs.Where(t => t != keepTab).ToList();
        foreach (var t in toClose)
        {
            if (!await CloseTab(t))
                break;
        }
    }

    private async Task CloseTabsToTheRight(TabState tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        var toClose = _tabs.Skip(idx + 1).ToList();
        foreach (var t in toClose)
        {
            if (!await CloseTab(t))
                break;
        }
    }

    private async Task CloseAllTabs()
    {
        var toClose = _tabs.ToList();
        foreach (var t in toClose)
        {
            if (!await CloseTab(t))
                break;
        }
    }

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            await CloseTab(_tabs[_selectedTabIndex]);
        }
    }

    // ===================== Edit Mode =====================

    private async void OnToggleEditModeClick(object? sender, RoutedEventArgs e)
    {
        _isEditMode = !_isEditMode;

        if (_isEditMode)
        {
            _editorColumn.Width = new GridLength(1, GridUnitType.Star);
            _splitterColumn.Width = new GridLength(4);
            _textEditor.IsVisible = true;
            _editorSplitter.IsVisible = true;
            _editModeMenuItem.Header = "Exit _Edit Mode";

            LoadCurrentTabIntoEditor();
            FocusEditorWhenShown();
            UpdatePreviewEditModeLabel();
        }
        else
        {
            // Save current editor content back to tab before hiding
            if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
            {
                _tabs[_selectedTabIndex].EditContent = _textEditor.Text ?? "";
            }

            _editorColumn.Width = new GridLength(0);
            _splitterColumn.Width = new GridLength(0);
            _textEditor.IsVisible = false;
            _editorSplitter.IsVisible = false;
            _editModeMenuItem.Header = "_Edit Mode";
            UpdateSaveMenuState();

            // Render edits made since the last debounced preview update
            if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
            {
                var tab = _tabs[_selectedTabIndex];
                var content = tab.EditContent ?? "";
                tab.CachedBody = await BuildBodyForTabAsync(tab, content);
                if (_selectedTabIndex >= 0 && _tabs[_selectedTabIndex] == tab)
                    ShowTabPreview(tab);
            }
        }
    }

    private void LoadCurrentTabIntoEditor()
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;
        var tab = _tabs[_selectedTabIndex];

        if (!tab.HasLoadedEditor && !tab.IsNewFile)
        {
            // First time entering edit mode for this tab -- load from file
            if (File.Exists(tab.FilePath))
            {
                var content = File.ReadAllText(tab.FilePath, Encoding.UTF8);
                tab.OriginalContent = content;
                tab.EditContent = content;
                tab.HasLoadedEditor = true;
            }
        }

        // Suppress TextChanged while loading
        _textEditor.TextChanged -= OnEditorTextChanged;
        _textEditor.Document.Text = tab.EditContent ?? "";
        _textEditor.TextChanged += OnEditorTextChanged;
    }

    private void UpdateSaveMenuState()
    {
        // Keep menu items always enabled so Ctrl+S/Ctrl+Shift+S shortcuts work
        // and can show informative status message; guards in OnSaveClick/OnSaveAsClick
        // prevent saving when not in edit mode
        _saveMenuItem.IsEnabled = true;
        _saveAsMenuItem.IsEnabled = true;
    }


    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;
        var tab = _tabs[_selectedTabIndex];

        tab.EditContent = _textEditor.Text ?? "";

        var wasModified = tab.IsModified;
        tab.IsModified = tab.EditContent != tab.OriginalContent;

        if (wasModified != tab.IsModified)
        {
            UpdateTabTitle(tab);
        }

        // Debounce preview update
        _previewDebounceTimer?.Dispose();
        _previewDebounceTimer = new Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdatePreviewFromEditor(tab)),
            null,
            PreviewDebounceMs,
            Timeout.Infinite);
    }

    private async void UpdatePreviewFromEditor(TabState tab)
    {
        if (_selectedTabIndex < 0 || _tabs[_selectedTabIndex] != tab) return;

        var requestId = Interlocked.Increment(ref _editorRenderRequestId);
        var content = tab.EditContent;

        try
        {
            var body = await BuildBodyForTabAsync(tab, content);
            if (requestId != _editorRenderRequestId || _selectedTabIndex < 0 || _tabs[_selectedTabIndex] != tab)
                return;

            tab.CachedBody = body;
            ShowTabPreview(tab);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Preview error: {ex.Message}";
        }
    }

    private void UpdateTabTitle(TabState tab)
    {
        if (tab.TabText == null) return;
        var name = tab.FileName;
        if (tab.IsModified)
            name = "* " + name;
        tab.TabText.Text = name;

        // Also update window title if this is the selected tab
        var idx = _tabs.IndexOf(tab);
        if (idx == _selectedTabIndex)
        {
            Title = $"Simple Markdown Viewer - {(tab.IsModified ? "* " : "")}{tab.FileName}";
        }
    }

    // ===================== Save / Save As / New =====================

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;
        var tab = _tabs[_selectedTabIndex];

        if (!_isEditMode || !tab.HasLoadedEditor)
        {
            _statusText.Text = "Enter edit mode (Ctrl+E) before saving.";
            return;
        }

        if (tab.IsNewFile || string.IsNullOrEmpty(tab.FilePath))
        {
            await SaveTabAsFile(tab);
            return;
        }

        await SaveTabToFile(tab, tab.FilePath);
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;
        var tab = _tabs[_selectedTabIndex];

        if (!_isEditMode || !tab.HasLoadedEditor)
        {
            _statusText.Text = "Enter edit mode (Ctrl+E) before saving.";
            return;
        }

        await SaveTabAsFile(tab);
    }

    private async Task<bool> SaveTab(TabState tab)
    {
        if (tab.IsNewFile || string.IsNullOrEmpty(tab.FilePath))
            return await SaveTabAsFile(tab);

        return await SaveTabToFile(tab, tab.FilePath);
    }

    private async Task<bool> SaveTabAsFile(TabState tab)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Markdown or Mermaid File",
                DefaultExtension = GetDefaultSaveExtension(tab),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Markdown Files") { Patterns = MarkdownFilePatterns },
                    new FilePickerFileType("Mermaid Files") { Patterns = MermaidFilePatterns },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                },
                SuggestedFileName = tab.IsNewFile ? "untitled.md" : tab.FileName
            });

            if (file == null)
                return false;

            var newPath = file.Path.LocalPath;
            if (!await SaveTabToFile(tab, newPath))
                return false;

            tab.FilePath = newPath;
            tab.IsNewFile = false;
            tab.DisplayName = null;
            tab.HasLoadedEditor = true;

            SetupFileWatcher(tab);
            UpdateTabTitle(tab);
            Title = $"Simple Markdown Viewer - {tab.FileName}";
            _statusText.Text = tab.FilePath;

            AddToRecentFiles(newPath);
            return true;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Save failed: {ex.Message}";
            return false;
        }
    }

    private static string GetDefaultSaveExtension(TabState tab)
    {
        if (!tab.IsNewFile && IsMermaidFile(tab.FilePath))
            return Path.GetExtension(tab.FilePath);

        return ".md";
    }

    private async Task<bool> SaveTabToFile(TabState tab, string filePath)
    {
        var watcher = tab.Watcher;
        var reenableWatcher = watcher?.EnableRaisingEvents == true;

        try
        {
            // Temporarily disable file watcher to avoid re-render loop
            if (reenableWatcher && watcher != null)
                watcher.EnableRaisingEvents = false;

            await File.WriteAllTextAsync(filePath, tab.EditContent, new UTF8Encoding(false));

            tab.OriginalContent = tab.EditContent;
            tab.IsModified = false;
            UpdateTabTitle(tab);

            _statusText.Text = $"Saved: {filePath}";

            return true;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Save failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (reenableWatcher && watcher != null)
            {
                try
                {
                    await Task.Delay(200);
                    watcher.EnableRaisingEvents = true;
                }
                catch { }
            }
        }
    }

    private TabState CreateUntitledTab(string displayName, string content)
    {
        var tab = new TabState
        {
            FilePath = "",
            TempHtmlPath = Path.Combine(Path.GetTempPath(), $"mdviewer_{Guid.NewGuid():N}.html"),
            IsNewFile = true,
            HasLoadedEditor = true,
            DisplayName = displayName,
            EditContent = content,
            OriginalContent = content
        };

        var button = CreateTabButton(tab);
        tab.TabButton = button;
        _tabPanel.Children.Add(button);
        UpdateTabScrollButtons();
        _tabs.Add(tab);
        return tab;
    }

    private void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        _untitledCounter++;
        var displayName = _untitledCounter == 1 ? "Untitled" : $"Untitled {_untitledCounter}";

        var tab = CreateUntitledTab(displayName, "");

        // Auto-enable edit mode if not already
        if (!_isEditMode)
        {
            OnToggleEditModeClick(null, null!);
        }

        SelectTab(_tabs.Count - 1);

        FocusEditorWhenShown();
    }

    private void FocusEditorWhenShown()
    {
        // The editor was just made visible and can't take focus until it has been laid
        // out; focusing immediately leaves keyboard input with the WebView
        Dispatcher.UIThread.Post(() => _textEditor.TextArea.Focus(), DispatcherPriority.Loaded);
    }

    // ===================== Unsaved Changes Dialog =====================

    private async Task<string> ShowUnsavedChangesDialog(string fileName)
    {
        var result = "Cancel";

        var dialog = new Window
        {
            Title = "Unsaved Changes",
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Save changes to {fileName}?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var saveBtn = new Button { Content = "Save", Width = 80 };
        saveBtn.Click += (s, ev) => { result = "Save"; dialog.Close(); };

        var dontSaveBtn = new Button { Content = "Don't Save", Width = 100 };
        dontSaveBtn.Click += (s, ev) => { result = "DontSave"; dialog.Close(); };

        var cancelBtn = new Button { Content = "Cancel", Width = 80 };
        cancelBtn.Click += (s, ev) => { result = "Cancel"; dialog.Close(); };

        buttons.Children.Add(saveBtn);
        buttons.Children.Add(dontSaveBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        await dialog.ShowDialog(this);

        return result;
    }

    // ===================== End Edit Mode =====================

    private async void OnCopyMarkdownClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;

        var tab = _tabs[_selectedTabIndex];
        try
        {
            var markdown = tab.HasLoadedEditor ? tab.EditContent : await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(markdown);
                _statusText.Text = "Markdown copied to clipboard";
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void OnSaveAsPdfClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count)
        {
            _statusText.Text = "No document open";
            return;
        }
        
        try
        {
            // Open print dialog - user can select "Microsoft Print to PDF" to save as PDF
            _webView.ShowPrintUI();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Print failed: {ex.Message}";
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            var tab = _tabs[_selectedTabIndex];
            if (!tab.IsNewFile && (_isEditMode || tab.IsModified))
            {
                await ReloadTabFromDisk(tab);
            }
            else if (_isEditMode)
            {
                UpdatePreviewFromEditor(tab);
            }
            else if (!tab.IsNewFile)
            {
                await GenerateHtml(tab, forceDiskReload: true);
                ShowTabPreview(tab);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoreWebView2OpenDevToolsWindow(IntPtr self);

    private void OnDevToolsClick(object? sender, RoutedEventArgs e)
    {
        // NativeWebView has no managed DevTools API; call ICoreWebView2::OpenDevToolsWindow
        // (vtable slot 51) on Windows. Elsewhere F12 inside the preview opens them.
        if (_webView.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle webView2 && webView2.CoreWebView2 != IntPtr.Zero)
        {
            var vtable = Marshal.ReadIntPtr(webView2.CoreWebView2);
            var openDevTools = Marshal.GetDelegateForFunctionPointer<CoreWebView2OpenDevToolsWindow>(
                Marshal.ReadIntPtr(vtable, 51 * IntPtr.Size));
            openDevTools(webView2.CoreWebView2);
        }
        else
        {
            _statusText.Text = "Click the preview and press F12 to open developer tools.";
        }
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var okButton = new Button 
        { 
            Content = "OK", 
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        
        var dialog = new Window
        {
            Title = "About",
            Width = 400,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
            ExtendClientAreaToDecorationsHint = false
        };

        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            Margin = new Thickness(20)
        };
        
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8
        };
        info.Children.Add(new TextBlock { Text = "Simple Markdown Viewer", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        info.Children.Add(new TextBlock { Text = "Version 1.5.1", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        info.Children.Add(new TextBlock { Text = "A lightweight markdown and Mermaid viewer/editor\nwith live preview, tabs, custom CSS, and dark mode.", TextAlignment = Avalonia.Media.TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        info.Children.Add(new TextBlock { Text = "Built with Avalonia UI, WebView2, and Markdig", FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });

        var dabWorxLink = new TextBlock { Text = "An open source project by DAB Worx Inc.", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0066cc")), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TextDecorations = Avalonia.Media.TextDecorations.Underline };
        dabWorxLink.Tapped += (s, args) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://dabworx.com", UseShellExecute = true }); } catch { } };
        info.Children.Add(dabWorxLink);

        var repoLink = new TextBlock { Text = "GitHub Repository", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0066cc")), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), TextDecorations = Avalonia.Media.TextDecorations.Underline };
        repoLink.Tapped += (s, args) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://github.com/KrunchMuffin/SimpleMarkdownViewer", UseShellExecute = true }); } catch { } };
        info.Children.Add(repoLink);

        Grid.SetRow(info, 0);
        grid.Children.Add(info);
        
        okButton.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(okButton, 1);
        grid.Children.Add(okButton);
        
        dialog.Content = grid;
        okButton.Click += (s, args) => dialog.Close();
        
        await dialog.ShowDialog(this);
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SaveSettings();

        // Update editor theme
        SetupTextMateTheme();

        // Update tab button colors and text
        for (int i = 0; i < _tabs.Count; i++)
        {
            var btn = _tabs[i].TabButton;
            if (btn != null)
            {
                btn.Background = i == _selectedTabIndex
                    ? new SolidColorBrush(Color.Parse(_isDarkMode ? "#3a3a3a" : "#ffffff"))
                    : new SolidColorBrush(Color.Parse(_isDarkMode ? "#2a2a2a" : "#d0d0d0"));
            }
            var txt = _tabs[i].TabText;
            if (txt != null)
            {
                txt.Foreground = new SolidColorBrush(Color.Parse(_isDarkMode ? "#ffffff" : "#000000"));
            }
        }

        // Reload the page shell for the new theme
        RefreshPreview();
    }

    private async void OnToggleLineNumbersClick(object? sender, RoutedEventArgs e)
    {
        _showPreviewLineNumbers = !_showPreviewLineNumbers;
        _lineNumbersMenuItem.Header = _showPreviewLineNumbers ? "Hide Preview _Line Numbers" : "Preview _Line Numbers";
        SaveSettings();

        // Line numbers are data attributes in the rendered markdown, so every tab re-renders
        foreach (var tab in _tabs)
        {
            await GenerateHtml(tab);
        }

        RefreshPreview();
    }

    private void OnOpenCustomCssClick(object? sender, RoutedEventArgs e)
    {
        var cssFile = _isDarkMode ? "custom-dark.css" : "custom-light.css";
        var cssPath = Path.Combine(SettingsDir, cssFile);

        if (!Directory.Exists(SettingsDir))
            Directory.CreateDirectory(SettingsDir);

        if (!File.Exists(cssPath))
        {
            // Export built-in styles as starting point
            var defaults = GetBuiltInCss();
            File.WriteAllText(cssPath, defaults, Encoding.UTF8);
        }

        // Open in default editor
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = cssPath,
                UseShellExecute = true
            });
            _statusText.Text = $"Opened {cssFile} — edit, save, then Refresh (F5) to apply.";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Could not open {cssFile}: {ex.Message}";
        }
    }

    private string GetBuiltInCss()
    {
        var mode = _isDarkMode ? "dark" : "light";
        var bgColor = _isDarkMode ? "#0d1117" : "#ffffff";
        var textColor = _isDarkMode ? "#e6edf3" : "#24292f";
        var codeBg = _isDarkMode ? "#161b22" : "#f6f8fa";
        var borderColor = _isDarkMode ? "#30363d" : "#d0d7de";
        var linkColor = _isDarkMode ? "#58a6ff" : "#0969da";
        var blockquoteColor = _isDarkMode ? "#8b949e" : "#656d76";
        var headingColor = _isDarkMode ? "#e6edf3" : "#1f2328";

        return $@"/* SimpleMarkdownViewer Custom CSS ({mode} mode)
   Edit this file to override the built-in styles.
   Save and press F5 (Refresh) in the viewer to apply.
   Delete this file to revert to defaults. */

body {{
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif;
    font-size: 16px;
    line-height: 1.6;
    color: {textColor};
    background-color: {bgColor};
    max-width: 980px;
    margin: 0 auto;
    padding: 32px;
}}

h1, h2, h3, h4, h5, h6 {{
    color: {headingColor};
    margin-top: 24px;
    margin-bottom: 16px;
    font-weight: 600;
    line-height: 1.25;
}}

h1 {{ font-size: 2em; padding-bottom: 0.3em; border-bottom: 1px solid {borderColor}; }}
h2 {{ font-size: 1.5em; padding-bottom: 0.3em; border-bottom: 1px solid {borderColor}; }}
h3 {{ font-size: 1.25em; }}

a {{ color: {linkColor}; text-decoration: none; }}
a:hover {{ text-decoration: underline; }}

code {{
    font-family: ui-monospace, 'Cascadia Code', 'Consolas', monospace;
    font-size: 85%;
    background-color: {codeBg};
    padding: 0.2em 0.4em;
    border-radius: 6px;
}}

pre {{
    background-color: {codeBg};
    padding: 16px;
    border-radius: 6px;
    overflow-x: auto;
}}

pre code {{
    background-color: transparent;
    padding: 0;
    font-size: 100%;
}}

table {{ border-collapse: collapse; width: 100%; margin: 16px 0; }}
th, td {{ border: 1px solid {borderColor}; padding: 8px 13px; text-align: left; }}
th {{ background-color: {codeBg}; font-weight: 600; }}

blockquote {{
    margin: 16px 0;
    padding: 0 1em;
    color: {blockquoteColor};
    border-left: 4px solid {borderColor};
}}

ul, ol {{ padding-left: 2em; margin: 16px 0; }}

img {{ max-width: 100%; height: auto; }}

hr {{ border: 0; height: 1px; background-color: {borderColor}; margin: 24px 0; }}
";
    }

    private void SetupFileWatcher(TabState tab)
    {
        tab.Watcher?.Dispose();
        tab.Watcher = null;

        var directory = Path.GetDirectoryName(tab.FilePath);
        var fileName = Path.GetFileName(tab.FilePath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName)) return;

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        // Many editors save by writing a temp file and renaming it over the original,
        // which raises Created/Renamed instead of Changed.
        watcher.Changed += (s, e) => OnWatchedFileChanged(tab);
        watcher.Created += (s, e) => OnWatchedFileChanged(tab);
        watcher.Renamed += (s, e) =>
        {
            if (string.Equals(e.FullPath, tab.FilePath, StringComparison.OrdinalIgnoreCase))
                OnWatchedFileChanged(tab);
        };

        watcher.EnableRaisingEvents = true;
        tab.Watcher = watcher;
    }

    private void OnWatchedFileChanged(TabState tab)
    {
        // A single save usually raises several events; coalesce them into one reload.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref tab.WatcherDebounce, cts)?.Cancel();
        _ = ReloadAfterExternalChangeAsync(tab, cts.Token);
    }

    private async Task ReloadAfterExternalChangeAsync(TabState tab, CancellationToken ct)
    {
        try
        {
            await Task.Delay(WatcherDebounceMs, ct);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyExternalChangeAsync(tab));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _statusText.Text = $"Reload failed: {ex.Message}");
        }
    }

    private async Task ApplyExternalChangeAsync(TabState tab)
    {
        if (!_tabs.Contains(tab) || !File.Exists(tab.FilePath)) return;

        if (tab.IsModified)
        {
            // File changed externally while user has unsaved edits -- don't overwrite
            _statusText.Text = $"Warning: {tab.FileName} changed on disk. Save to overwrite or Refresh (F5) to reload.";
            return;
        }

        var content = await ReadFileWithRetryAsync(tab.FilePath);

        // Our own saves and duplicate events land here with nothing new to show
        if (tab.CachedBody != null && content == tab.OriginalContent)
            return;

        tab.OriginalContent = content;

        // Keep the editor copy in sync even when edit mode is off, otherwise a later
        // re-render (theme toggle etc.) would bring back the stale text
        if (tab.HasLoadedEditor)
        {
            tab.EditContent = content;

            if (_isEditMode && _tabs.IndexOf(tab) == _selectedTabIndex)
            {
                _textEditor.TextChanged -= OnEditorTextChanged;
                _textEditor.Text = content;
                _textEditor.TextChanged += OnEditorTextChanged;
            }
        }

        tab.CachedBody = await BuildBodyForTabAsync(tab, content);
        if (_tabs.IndexOf(tab) == _selectedTabIndex)
            ShowTabPreview(tab);
    }

    private static async Task<string> ReadFileWithRetryAsync(string path)
    {
        // The writer may still hold the file open right after the change notification
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path, Encoding.UTF8);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }

    private string BuildBodyForTab(TabState tab, string content)
    {
        var markdown = IsMermaidFile(tab.FilePath) && !MarkdownRenderer.LooksLikeMarkdownDocument(content)
            ? MarkdownRenderer.WrapMermaidSource(content)
            : content;
        return _markdownRenderer.ToHtml(markdown, _showPreviewLineNumbers);
    }

    private Task<string> BuildBodyForTabAsync(TabState tab, string content)
    {
        return Task.Run(() =>
        {
            lock (_markdownRenderLock)
            {
                return BuildBodyForTab(tab, content);
            }
        });
    }

    private async Task GenerateHtml(TabState tab, bool forceDiskReload = false)
    {
        try
        {
            var renderFromEditor = !forceDiskReload && (tab.IsNewFile || tab.HasLoadedEditor || tab.IsModified);
            var markdown = renderFromEditor
                ? tab.EditContent
                : await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
            if (!renderFromEditor && !tab.HasLoadedEditor)
                tab.OriginalContent = markdown;

            tab.CachedBody = await BuildBodyForTabAsync(tab, markdown);
        }
        catch (Exception ex)
        {
            tab.CachedBody = $"<h1>Error</h1><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>";
        }
    }

    private async Task<bool> ReloadTabFromDisk(TabState tab)
    {
        if (tab.IsNewFile || string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath))
        {
            UpdatePreviewFromEditor(tab);
            return true;
        }

        if (tab.IsModified)
        {
            var result = await ShowUnsavedChangesDialog(tab.FileName);
            if (result == "Cancel")
                return false;

            if (result == "Save" && !await SaveTab(tab))
                return false;
        }

        try
        {
            var content = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
            tab.OriginalContent = content;
            tab.EditContent = content;
            tab.HasLoadedEditor = true;
            tab.IsModified = false;
            UpdateTabTitle(tab);

            if (_isEditMode && _tabs.IndexOf(tab) == _selectedTabIndex)
            {
                _textEditor.TextChanged -= OnEditorTextChanged;
                _textEditor.Text = content;
                _textEditor.TextChanged += OnEditorTextChanged;
            }

            tab.CachedBody = await BuildBodyForTabAsync(tab, content);
            if (_tabs.IndexOf(tab) == _selectedTabIndex)
                ShowTabPreview(tab);

            _statusText.Text = $"Reloaded: {tab.FilePath}";
            return true;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Refresh failed: {ex.Message}";
            return false;
        }
    }

    private void RefreshPreview()
    {
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
            ShowTabPreview(_tabs[_selectedTabIndex]);
        else
            ShowWelcome();
    }

    private void ShowWelcome()
    {
        // OnWebViewCreated calls RefreshPreview once the WebView is up
        if (!_webViewReady)
            return;

        _welcomeTempHtmlPath ??= Path.Combine(Path.GetTempPath(), $"mdviewer_welcome_{Guid.NewGuid():N}.html");
        _loadedPageKey = null;
        NavigateToPage(_welcomeTempHtmlPath, GetWelcomePage());
    }

    private async void ShowTabPreview(TabState tab)
    {
        // OnWebViewCreated calls RefreshPreview once the WebView is up
        if (!_webViewReady)
            return;

        var body = tab.CachedBody ?? (tab.IsNewFile ? NewFilePlaceholderHtml : "");
        var customCss = GetCustomCssTag();
        var key = new PreviewPageKey(tab, tab.FilePath, _isDarkMode, _showPreviewLineNumbers, customCss);

        // Same page already loaded: swap the content in place
        if (key == _loadedPageKey && _readyRenderVersion == _renderVersion)
        {
            try
            {
                // The version check makes the update a no-op if a newer page loaded meanwhile
                var script = $"(function () {{ if (new URLSearchParams(location.search).get('v') !== '{_renderVersion}') return false; " +
                             $"window.mdviewer.setEditMode({(_isEditMode ? "true" : "false")}); " +
                             $"return window.mdviewer.setContent({JsonSerializer.Serialize(body)}); }})()";
                var result = await _webView.InvokeScript(script);
                if (result?.Trim() == "true")
                    return;
            }
            catch { /* fall back to a full reload */ }

            // The page moved on while we awaited; a newer render owns it now
            if (_loadedPageKey != key)
                return;
        }

        var baseDirectory = !tab.IsNewFile && !string.IsNullOrEmpty(tab.FilePath)
            ? Path.GetDirectoryName(tab.FilePath)
            : null;
        var html = PreviewPage.Build(
            new PreviewPage.Options(_isDarkMode, _showPreviewLineNumbers, _isEditMode, baseDirectory, customCss, _pageNonce),
            body);

        _loadedPageKey = key;
        NavigateToPage(tab.TempHtmlPath, html);
    }

    private void UpdatePreviewEditModeLabel()
    {
        if (!_webViewReady) return;
        try
        {
            _ = _webView.InvokeScript($"window.mdviewer && window.mdviewer.setEditMode({(_isEditMode ? "true" : "false")});");
        }
        catch { }
    }

    private async void NavigateToPage(string tempPath, string html)
    {
        try
        {
            File.WriteAllText(tempPath, html, new UTF8Encoding(true));
            var renderVersion = Interlocked.Increment(ref _renderVersion);
            var uriBuilder = new UriBuilder(new Uri(tempPath))
            {
                Query = $"v={renderVersion}"
            };
            _webView.Navigate(uriBuilder.Uri);

            // Navigation takes focus; hand it back to the editor
            await Task.Delay(100);
            if (renderVersion == _renderVersion && _isEditMode)
                _textEditor.Focus();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Render error: {ex.Message}";
        }
    }

    private bool _isClosingConfirmed;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isClosingConfirmed)
        {
            var modifiedTabs = _tabs.Where(t => t.IsModified).ToList();
            if (modifiedTabs.Count > 0)
            {
                e.Cancel = true;
                var names = string.Join(", ", modifiedTabs.Select(t => t.FileName));
                var result = await ShowUnsavedChangesDialog(names);

                var canClose = result != "Cancel";

                if (result == "Save")
                {
                    foreach (var tab in modifiedTabs)
                    {
                        if (!await SaveTab(tab))
                        {
                            canClose = false;
                            break;
                        }
                    }
                }

                if (canClose)
                {
                    _isClosingConfirmed = true;
                    Close();
                }
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _previewDebounceTimer?.Dispose();
        _textMateInstallation?.Dispose();

        foreach (var tab in _tabs)
        {
            tab.WatcherDebounce?.Cancel();
            tab.Watcher?.Dispose();
            try { if (File.Exists(tab.TempHtmlPath)) File.Delete(tab.TempHtmlPath); } catch { }
        }
        try { if (_welcomeTempHtmlPath != null && File.Exists(_welcomeTempHtmlPath)) File.Delete(_welcomeTempHtmlPath); } catch { }

        base.OnClosed(e);
    }
}
