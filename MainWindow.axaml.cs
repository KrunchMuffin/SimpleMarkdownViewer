using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using AvaloniaWebView;
using Markdig;
using TextMateSharp.Grammars;
using WebViewCore.Events;

namespace SimpleMarkdownViewer;

public partial class MainWindow : Window
{
    private readonly WebView _webView;
    private readonly TextBlock _statusText;
    private readonly Border _statusBar;
    private readonly Border _tabStrip;
    private readonly StackPanel _tabPanel;
    private readonly MenuItem _themeMenuItem;
    private readonly DockPanel _mainPanel;
    private readonly MenuItem _recentMenu = null!;
    private readonly MarkdownPipeline _pipeline;
    
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleMarkdownViewer",
        "settings.json");
    
    private bool _isDarkMode = false;
    private bool _webViewReady = false;
    private string? _pendingHtml = null;
    
    private readonly List<string> _recentFiles = new();
    private const int MaxRecentFiles = 10;
    
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
    private int _untitledCounter;

    // Single-instance pipe server
    private CancellationTokenSource? _pipeCts;

    private class TabState
    {
        public string FilePath { get; set; } = "";
        public string FileName => IsNewFile ? (DisplayName ?? "Untitled") : Path.GetFileName(FilePath);
        public string TempHtmlPath { get; set; } = "";
        public string? CachedHtml { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public Button? TabButton { get; set; }
        public TextBlock? TabText { get; set; }

        // Edit mode fields
        public bool IsModified { get; set; }
        public string OriginalContent { get; set; } = "";
        public string EditContent { get; set; } = "";
        public bool HasLoadedEditor { get; set; }
        public bool IsNewFile { get; set; }
        public string? DisplayName { get; set; }
    }

    private class AppSettings
    {
        public bool IsDarkMode { get; set; } = false;
        public List<string> RecentFiles { get; set; } = new();
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
                    _recentFiles.Clear();
                    _recentFiles.AddRange(settings.RecentFiles.Where(File.Exists).Take(MaxRecentFiles));
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
                RecentFiles = _recentFiles.ToList()
            };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private void AddToRecentFiles(string filePath)
    {
        // Remove if already exists (will re-add at top)
        _recentFiles.Remove(filePath);
        
        // Insert at beginning
        _recentFiles.Insert(0, filePath);
        
        // Trim to max
        while (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);
        
        SaveSettings();
        UpdateRecentMenu();
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
        RequestedThemeVariant = _isDarkMode ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        _mainPanel.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#1e1e1e" : "#ffffff"));
        _statusBar.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#1e1e1e" : "#f0f0f0"));
        _statusText.Foreground = _isDarkMode ? Brushes.White : Brushes.Black;
        _tabStrip.Background = new SolidColorBrush(Color.Parse(_isDarkMode ? "#252525" : "#e0e0e0"));
    }

    public MainWindow()
    {
        InitializeComponent();

        _webView = this.FindControl<WebView>("WebView")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _statusBar = this.FindControl<Border>("StatusBar")!;
        _tabStrip = this.FindControl<Border>("TabStrip")!;
        _tabPanel = this.FindControl<StackPanel>("TabPanel")!;
        _themeMenuItem = this.FindControl<MenuItem>("ThemeMenuItem")!;
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

        // Load settings
        LoadSettings();
        UpdateRecentMenu();

        // Configure Markdig
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseTaskLists()
            .UseDiagrams()
            .Build();

        // Set up AvaloniaEdit TextMate for markdown highlighting
        SetupTextMateTheme();

        // Set up editor context menu
        SetupEditorContextMenu();

        // Wire up editor text change events
        _textEditor.TextChanged += OnEditorTextChanged;

        // Wire up WebView events
        _webView.WebViewCreated += OnWebViewCreated;
        _webView.NavigationStarting += OnNavigationStarting;
        _webView.NavigationCompleted += OnNavigationCompleted;

        // Show welcome page after window loads
        this.Loaded += OnWindowLoaded;
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

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        // Give WebView time to initialize
        await Task.Delay(500);

        if (_tabs.Count == 0)
        {
            _webViewReady = true;
            RenderHtml(GetWelcomePage());
        }
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
#pragma warning disable CS0618 // Type or member is obsolete
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
#pragma warning restore CS0618
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        if (!e.Data.Contains(DataFormats.Files)) return;

        e.Handled = true;

        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null) return;
        
        var mdExtensions = new[] { ".md", ".markdown", ".mdown", ".mkd" };
        
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            
            if (mdExtensions.Contains(ext))
            {
                await OpenFileInNewTab(path);
            }
        }
    }

    private async void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        _webViewReady = true;
        _statusText.Text = "Ready - Open a markdown file (Ctrl+O)";

        // Set WebView background color to match theme (fixes dark mode initial render)
        try
        {
            var bgColor = _isDarkMode ? "#0d1117" : "#ffffff";
            await _webView.ExecuteScriptAsync($"document.body.style.backgroundColor = '{bgColor}';");
        }
        catch { /* Ignore if not ready */ }

        if (_pendingHtml != null)
        {
            RenderHtml(_pendingHtml);
            _pendingHtml = null;
        }
        else if (_tabs.Count == 0)
        {
            // Show welcome page
            RenderHtml(GetWelcomePage());
        }

        // Force re-render after short delay to fix WebView2 dark mode rendering issue
        if (_isDarkMode)
        {
            await Task.Delay(50);
            var cachedHtml = _tabs.Count > 0 && _selectedTabIndex >= 0 ? _tabs[_selectedTabIndex].CachedHtml : null;
            RenderHtml(cachedHtml ?? GetWelcomePage());
        }
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
    <p>or drag and drop markdown files here</p>
</div></body></html>";
    }

    private void OnNavigationStarting(object? sender, WebViewCore.Events.WebViewUrlLoadingEventArg e)
    {
        var url = e.Url?.ToString() ?? "";

        // Handle app:// commands from JavaScript context menu
        if (url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (url.Contains("toggle-edit"))
            {
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    OnToggleEditModeClick(null, null!));
            }
            return;
        }

        // Check if this is a file being dragged onto WebView
        if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            // Extract the file path
            var filePath = url.Substring(8).Replace('/', '\\');
            
            // URL decode
            filePath = Uri.UnescapeDataString(filePath);
            
            // Skip our own temp HTML files
            if (filePath.Contains("mdviewer_") && filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return;
            
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mdExtensions = new[] { ".md", ".markdown", ".mdown", ".mkd" };
            
            if (mdExtensions.Contains(ext) && File.Exists(filePath))
            {
                // Cancel the WebView navigation
                e.Cancel = true;
                
                // Open in a new tab instead
                _ = OpenFileInNewTab(filePath);
            }
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        if (e.IsSuccess)
        {
            try
            {
                await _webView.ExecuteScriptAsync("if(typeof renderContent === 'function') renderContent();");
            }
            catch { }
        }
    }

    private string GetTemplate()
    {
        var bgColor = _isDarkMode ? "#0d1117" : "#ffffff";
        var textColor = _isDarkMode ? "#e6edf3" : "#24292f";
        var codeBg = _isDarkMode ? "#161b22" : "#f6f8fa";
        var borderColor = _isDarkMode ? "#30363d" : "#d0d7de";
        var linkColor = _isDarkMode ? "#58a6ff" : "#0969da";
        var blockquoteColor = _isDarkMode ? "#8b949e" : "#656d76";
        var headingColor = _isDarkMode ? "#e6edf3" : "#1f2328";
        var hljsTheme = _isDarkMode ? "github-dark" : "github";
        var mermaidTheme = _isDarkMode ? "dark" : "default";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    
    <script src=""https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js""></script>
    <script src=""https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js""></script>
    <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/{hljsTheme}.min.css"">
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js""></script>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/sql.min.js""></script>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/powershell.min.js""></script>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/csharp.min.js""></script>
    
    <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.css"">
    <script src=""https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.js""></script>
    
    <style>
        * {{ box-sizing: border-box; }}
        
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
        
        .task-list-item {{ list-style-type: none; margin-left: -1.5em; }}
        .task-list-item input {{ margin-right: 0.5em; }}
        
        img {{ max-width: 100%; height: auto; }}
        
        hr {{ border: 0; height: 1px; background-color: {borderColor}; margin: 24px 0; }}
        
        .mermaid {{
            text-align: center;
            margin: 16px 0;
            cursor: pointer;
            position: relative;
            border: 1px solid {borderColor};
            border-radius: 6px;
            padding: 16px;
            transition: box-shadow 0.2s, transform 0.3s, top 0.3s, left 0.3s, width 0.3s, height 0.3s;
        }}
        .mermaid:hover {{
            box-shadow: 0 0 8px {linkColor}40;
        }}
        .mermaid::after {{
            content: '🔍 Click to expand';
            position: absolute;
            top: 4px;
            right: 8px;
            font-size: 11px;
            color: {blockquoteColor};
            opacity: 0;
            transition: opacity 0.2s;
        }}
        .mermaid:hover::after {{
            opacity: 1;
        }}
        .mermaid svg {{
            max-width: 100%;
            height: auto;
        }}

        /* Fullscreen mode for diagrams */
        .mermaid.fullscreen {{
            position: fixed !important;
            top: 60px !important;
            left: 0 !important;
            width: 100vw !important;
            height: calc(100vh - 60px) !important;
            z-index: 10000 !important;
            background: {bgColor} !important;
            margin: 0 !important;
            border-radius: 0 !important;
            border: none !important;
            padding: 20px !important;
            overflow: auto !important;
            display: flex !important;
            align-items: center !important;
            justify-content: center !important;
        }}
        .mermaid.fullscreen::after {{
            display: none;
        }}
        .mermaid.fullscreen svg {{
            max-width: none !important;
            max-height: none !important;
            transform-origin: center center;
        }}

        /* Fullscreen controls bar */
        .fullscreen-controls {{
            display: none;
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 60px;
            background: {codeBg};
            border-bottom: 1px solid {borderColor};
            z-index: 10001;
            align-items: center;
            justify-content: space-between;
            padding: 0 20px;
        }}
        .fullscreen-controls.active {{
            display: flex;
        }}
        .fullscreen-controls-title {{
            font-weight: 600;
            color: {headingColor};
        }}
        .fullscreen-controls-buttons {{
            display: flex;
            gap: 8px;
        }}
        .fullscreen-controls button {{
            padding: 8px 16px;
            border: 1px solid {borderColor};
            border-radius: 4px;
            background: {bgColor};
            color: {textColor};
            cursor: pointer;
            font-size: 14px;
        }}
        .fullscreen-controls button:hover {{
            background: {borderColor};
        }}

        /* Overlay behind fullscreen diagram */
        .fullscreen-overlay {{
            display: none;
            position: fixed;
            top: 0;
            left: 0;
            width: 100vw;
            height: 100vh;
            background: {bgColor};
            z-index: 9999;
        }}
        .fullscreen-overlay.active {{
            display: block;
        }}
        
        kbd {{
            display: inline-block;
            padding: 3px 5px;
            font-size: 11px;
            color: {textColor};
            background-color: {codeBg};
            border: 1px solid {borderColor};
            border-radius: 6px;
        }}

        /* Custom context menu */
        .ctx-menu {{
            display: none;
            position: fixed;
            z-index: 20000;
            background: {(_isDarkMode ? "#2d2d2d" : "#ffffff")};
            border: 1px solid {borderColor};
            border-radius: 6px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
            padding: 4px 0;
            min-width: 160px;
            font-size: 13px;
        }}
        .ctx-menu.active {{ display: block; }}
        .ctx-menu-item {{
            padding: 6px 16px;
            cursor: pointer;
            color: {textColor};
            display: flex;
            justify-content: space-between;
        }}
        .ctx-menu-item:hover {{
            background: {(_isDarkMode ? "#3a3a3a" : "#f0f0f0")};
        }}
        .ctx-menu-item .shortcut {{
            color: {blockquoteColor};
            margin-left: 24px;
            font-size: 12px;
        }}
        .ctx-menu-sep {{
            height: 1px;
            background: {borderColor};
            margin: 4px 0;
        }}

        @media print {{
            body {{
                color: #000000 !important;
                background-color: #ffffff !important;
            }}
            h1, h2, h3, h4, h5, h6 {{
                color: #000000 !important;
                border-color: #d0d7de !important;
            }}
            a {{ color: #0969da !important; }}
            code, pre {{
                background-color: #f6f8fa !important;
                color: #000000 !important;
            }}
            th, td {{
                border-color: #d0d7de !important;
                color: #000000 !important;
            }}
            th {{ background-color: #f6f8fa !important; }}
            blockquote {{ color: #656d76 !important; border-color: #d0d7de !important; }}
            kbd {{ color: #000000 !important; background-color: #f6f8fa !important; border-color: #d0d7de !important; }}
            hr {{ background-color: #d0d7de !important; }}
            .mermaid {{ border-color: #d0d7de !important; }}
            .mermaid::after {{ display: none; }}
            .fullscreen-overlay, .fullscreen-controls {{ display: none !important; }}
        }}
    </style>
</head>
<body>
    <article id=""content"">
        {{{{CONTENT}}}}
    </article>

    <!-- Fullscreen overlay and controls -->
    <div id=""fullscreenOverlay"" class=""fullscreen-overlay""></div>
    <div id=""fullscreenControls"" class=""fullscreen-controls"">
        <span class=""fullscreen-controls-title"">Diagram Viewer</span>
        <div class=""fullscreen-controls-buttons"">
            <button onclick=""zoomIn()"">Zoom +</button>
            <button onclick=""zoomOut()"">Zoom -</button>
            <button onclick=""resetZoom()"">Reset</button>
            <button onclick=""fitToPage()"">Fit</button>
            <button onclick=""closeFullscreen()"">✕ Close (Esc)</button>
        </div>
    </div>

    <!-- Custom context menu -->
    <div id=""ctxMenu"" class=""ctx-menu"">
        <div class=""ctx-menu-item"" id=""ctxEdit"">Edit<span class=""shortcut"">Ctrl+E</span></div>
        <div class=""ctx-menu-sep""></div>
        <div class=""ctx-menu-item"" id=""ctxCopy"">Copy<span class=""shortcut"">Ctrl+C</span></div>
        <div class=""ctx-menu-item"" id=""ctxSelectAll"">Select All<span class=""shortcut"">Ctrl+A</span></div>
    </div>
    
    <script>
        let currentFullscreenEl = null;
        let currentZoom = 1;
        let panX = 0, panY = 0;
        let isDragging = false;
        let dragStartX = 0, dragStartY = 0;
        let panStartX = 0, panStartY = 0;

        function renderContent() {{
            mermaid.initialize({{
                startOnLoad: false,
                theme: '{mermaidTheme}',
                securityLevel: 'loose'
            }});

            try {{ mermaid.run({{ querySelector: '.mermaid' }}); }} catch (e) {{ console.error(e); }}

            // Add click handlers to mermaid diagrams for fullscreen
            setTimeout(() => {{
                document.querySelectorAll('.mermaid').forEach((el) => {{
                    el.addEventListener('click', (e) => {{
                        if (!el.classList.contains('fullscreen')) {{
                            openFullscreen(el);
                        }}
                    }});
                }});
            }}, 500);

            document.querySelectorAll('pre code').forEach((block) => {{
                if (!block.closest('.mermaid')) hljs.highlightElement(block);
            }});

            // Render preprocessed math elements
            if (typeof katex !== 'undefined') {{
                document.querySelectorAll('.math-display').forEach((el) => {{
                    try {{
                        const math = el.getAttribute('data-math');
                        if (math) {{
                            const decoded = new DOMParser().parseFromString(math, 'text/html').body.textContent;
                            katex.render(decoded, el, {{ displayMode: true, throwOnError: false }});
                        }}
                    }} catch (e) {{ console.error('Math render error:', e); }}
                }});
                document.querySelectorAll('.math-inline').forEach((el) => {{
                    try {{
                        const math = el.getAttribute('data-math');
                        if (math) {{
                            const decoded = new DOMParser().parseFromString(math, 'text/html').body.textContent;
                            katex.render(decoded, el, {{ displayMode: false, throwOnError: false }});
                        }}
                    }} catch (e) {{ console.error('Math render error:', e); }}
                }});
            }}
        }}

        function openFullscreen(mermaidEl) {{
            currentFullscreenEl = mermaidEl;
            currentZoom = 1;
            panX = 0;
            panY = 0;

            // Show overlay and controls
            document.getElementById('fullscreenOverlay').classList.add('active');
            document.getElementById('fullscreenControls').classList.add('active');

            // Make the diagram fullscreen (no cloning - original element!)
            mermaidEl.classList.add('fullscreen');

            // Reset SVG transform and enable dragging cursor
            const svg = mermaidEl.querySelector('svg');
            if (svg) {{
                svg.style.transform = 'scale(1) translate(0px, 0px)';
                svg.style.cursor = 'grab';
            }}
        }}

        function closeFullscreen() {{
            if (!currentFullscreenEl) return;

            // Hide overlay and controls
            document.getElementById('fullscreenOverlay').classList.remove('active');
            document.getElementById('fullscreenControls').classList.remove('active');

            // Reset SVG transform before closing
            const svg = currentFullscreenEl.querySelector('svg');
            if (svg) {{
                svg.style.transform = '';
                svg.style.cursor = '';
            }}

            // Remove fullscreen class
            currentFullscreenEl.classList.remove('fullscreen');
            currentFullscreenEl = null;
            currentZoom = 1;
            panX = 0;
            panY = 0;
        }}

        function zoomIn() {{
            if (!currentFullscreenEl) return;
            currentZoom *= 1.25;
            applyZoom();
        }}

        function zoomOut() {{
            if (!currentFullscreenEl) return;
            currentZoom *= 0.8;
            applyZoom();
        }}

        function resetZoom() {{
            if (!currentFullscreenEl) return;
            currentZoom = 1;
            panX = 0;
            panY = 0;
            applyTransform();
        }}

        function fitToPage() {{
            if (!currentFullscreenEl) return;
            const svg = currentFullscreenEl.querySelector('svg');
            if (!svg) return;

            // Get the container dimensions (viewport minus controls bar)
            const containerWidth = window.innerWidth - 40;
            const containerHeight = window.innerHeight - 100;

            // Get SVG natural dimensions
            const svgRect = svg.getBoundingClientRect();
            const svgWidth = svgRect.width / currentZoom;
            const svgHeight = svgRect.height / currentZoom;

            // Calculate zoom to fit
            const scaleX = containerWidth / svgWidth;
            const scaleY = containerHeight / svgHeight;
            currentZoom = Math.min(scaleX, scaleY, 1) * 0.9; // 90% to add padding

            // Center it
            panX = 0;
            panY = 0;
            applyTransform();
        }}

        function applyZoom() {{
            applyTransform();
        }}

        function applyTransform() {{
            if (!currentFullscreenEl) return;
            const svg = currentFullscreenEl.querySelector('svg');
            if (svg) {{
                svg.style.transform = `translate(${{panX}}px, ${{panY}}px) scale(${{currentZoom}})`;
            }}
        }}

        // Mouse wheel zoom in fullscreen
        document.addEventListener('wheel', (e) => {{
            if (!currentFullscreenEl) return;
            e.preventDefault();
            if (e.deltaY < 0) {{
                currentZoom *= 1.1;
            }} else {{
                currentZoom *= 0.9;
            }}
            currentZoom = Math.max(0.1, Math.min(currentZoom, 20));
            applyZoom();
        }}, {{ passive: false }});

        // Mouse drag for panning
        document.addEventListener('mousedown', (e) => {{
            if (!currentFullscreenEl) return;
            const svg = currentFullscreenEl.querySelector('svg');
            if (svg && svg.contains(e.target)) {{
                isDragging = true;
                dragStartX = e.clientX;
                dragStartY = e.clientY;
                panStartX = panX;
                panStartY = panY;
                svg.style.cursor = 'grabbing';
                e.preventDefault();
            }}
        }});

        document.addEventListener('mousemove', (e) => {{
            if (!isDragging || !currentFullscreenEl) return;
            const dx = e.clientX - dragStartX;
            const dy = e.clientY - dragStartY;
            panX = panStartX + dx;
            panY = panStartY + dy;
            applyTransform();
        }});

        document.addEventListener('mouseup', () => {{
            if (isDragging && currentFullscreenEl) {{
                const svg = currentFullscreenEl.querySelector('svg');
                if (svg) svg.style.cursor = 'grab';
            }}
            isDragging = false;
        }});

        // Close with Escape key
        document.addEventListener('keydown', (e) => {{
            if (e.key === 'Escape') closeFullscreen();
        }});

        // Custom right-click context menu
        (function() {{
            const menu = document.getElementById('ctxMenu');
            const ctxEdit = document.getElementById('ctxEdit');
            const ctxCopy = document.getElementById('ctxCopy');
            const ctxSelectAll = document.getElementById('ctxSelectAll');

            function hideMenu() {{ menu.classList.remove('active'); }}

            document.addEventListener('contextmenu', (e) => {{
                e.preventDefault();
                const sel = window.getSelection();
                const hasSelection = sel && sel.toString().length > 0;
                ctxCopy.style.opacity = hasSelection ? '1' : '0.4';
                ctxCopy.style.pointerEvents = hasSelection ? 'auto' : 'none';

                menu.style.left = Math.min(e.clientX, window.innerWidth - 180) + 'px';
                menu.style.top = Math.min(e.clientY, window.innerHeight - 80) + 'px';
                menu.classList.add('active');
            }});

            document.addEventListener('click', hideMenu);
            document.addEventListener('scroll', hideMenu);
            window.addEventListener('blur', hideMenu);

            ctxEdit.addEventListener('click', () => {{
                hideMenu();
                window.location.href = 'app://toggle-edit';
            }});

            ctxCopy.addEventListener('click', () => {{
                const sel = window.getSelection();
                if (sel && sel.toString().length > 0) {{
                    navigator.clipboard.writeText(sel.toString());
                }}
                hideMenu();
            }});

            ctxSelectAll.addEventListener('click', () => {{
                const range = document.createRange();
                range.selectNodeContents(document.getElementById('content'));
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
                hideMenu();
            }});
        }})();

        document.addEventListener('DOMContentLoaded', renderContent);
    </script>
</body>
</html>";
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Markdown File",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Markdown Files") { Patterns = new[] { "*.md", "*.markdown", "*.mdown", "*.mkd" } },
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
            
#pragma warning disable CS0618 // Type or member is obsolete
            var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (string.IsNullOrWhiteSpace(text))
            {
                _statusText.Text = "Clipboard is empty or contains no text";
                return;
            }
            
            // Create a temp file with the clipboard content
            var tempPath = Path.Combine(Path.GetTempPath(), $"Clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(tempPath, text, new UTF8Encoding(true));

            // Open it in a new tab
            await OpenFileInNewTab(tempPath);
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
        
        closeBtn.Click += (s, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };
        
        return button;
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
        if (tab.CachedHtml != null)
        {
            if (_webViewReady)
            {
                RenderHtml(tab.CachedHtml);
            }
            else
            {
                _pendingHtml = tab.CachedHtml;
            }
        }
        else if (tab.IsNewFile)
        {
            var template = GetTemplate();
            tab.CachedHtml = template.Replace("{{CONTENT}}", "<p><em>Start typing in the editor...</em></p>");
            RenderHtml(tab.CachedHtml);
        }
    }

    private async void CloseTab(TabState tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        // Check for unsaved changes
        if (tab.IsModified)
        {
            var result = await ShowUnsavedChangesDialog(tab.FileName);
            if (result == "Save")
            {
                if (tab.IsNewFile || string.IsNullOrEmpty(tab.FilePath))
                {
                    // Need Save As for new files
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Save Markdown File",
                        DefaultExtension = ".md",
                        FileTypeChoices = new[]
                        {
                            new FilePickerFileType("Markdown Files") { Patterns = new[] { "*.md", "*.markdown" } },
                            new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                        },
                        SuggestedFileName = "untitled.md"
                    });
                    if (file == null) return; // User cancelled
                    await SaveTabToFile(tab, file.Path.LocalPath);
                }
                else
                {
                    await SaveTabToFile(tab, tab.FilePath);
                }
            }
            else if (result == "Cancel")
            {
                return;
            }
        }

        // Clean up
        tab.Watcher?.Dispose();
        try { if (File.Exists(tab.TempHtmlPath)) File.Delete(tab.TempHtmlPath); } catch { }

        // Remove from UI
        if (tab.TabButton != null)
            _tabPanel.Children.Remove(tab.TabButton);

        _tabs.RemoveAt(index);

        // Select another tab
        if (_tabs.Count == 0)
        {
            _selectedTabIndex = -1;
            _statusText.Text = "Ready - Open a markdown file (Ctrl+O)";
            Title = "Simple Markdown Viewer";
            RenderHtml(GetWelcomePage());

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
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            CloseTab(_tabs[_selectedTabIndex]);
        }
    }

    // ===================== Edit Mode =====================

    private void OnToggleEditModeClick(object? sender, RoutedEventArgs e)
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
        }

        // Update WebView context menu label
        try
        {
            var label = _isEditMode ? "Exit Edit Mode" : "Edit";
            _webView.ExecuteScriptAsync($"document.getElementById('ctxEdit').childNodes[0].textContent = '{label}';");
        }
        catch { }
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

    private void UpdatePreviewFromEditor(TabState tab)
    {
        if (_selectedTabIndex < 0 || _tabs[_selectedTabIndex] != tab) return;

        try
        {
            var htmlContent = ConvertMarkdownToHtml(tab.EditContent);
            var template = GetTemplate();
            tab.CachedHtml = template.Replace("{{CONTENT}}", htmlContent);
            RenderHtml(tab.CachedHtml);
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
            OnSaveAsClick(sender, e);
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

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Markdown File",
                DefaultExtension = ".md",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Markdown Files") { Patterns = new[] { "*.md", "*.markdown" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                },
                SuggestedFileName = tab.IsNewFile ? "untitled.md" : tab.FileName
            });

            if (file != null)
            {
                var newPath = file.Path.LocalPath;
                await SaveTabToFile(tab, newPath);

                tab.FilePath = newPath;
                tab.IsNewFile = false;
                tab.DisplayName = null;

                SetupFileWatcher(tab);
                UpdateTabTitle(tab);
                Title = $"Simple Markdown Viewer - {tab.FileName}";
                _statusText.Text = tab.FilePath;

                AddToRecentFiles(newPath);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private async Task SaveTabToFile(TabState tab, string filePath)
    {
        try
        {
            // Temporarily disable file watcher to avoid re-render loop
            if (tab.Watcher != null)
                tab.Watcher.EnableRaisingEvents = false;

            await File.WriteAllTextAsync(filePath, tab.EditContent, new UTF8Encoding(false));

            tab.OriginalContent = tab.EditContent;
            tab.IsModified = false;
            UpdateTabTitle(tab);

            _statusText.Text = $"Saved: {filePath}";

            // Re-enable file watcher after filesystem settles
            if (tab.Watcher != null)
            {
                await Task.Delay(200);
                tab.Watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        _untitledCounter++;
        var displayName = _untitledCounter == 1 ? "Untitled" : $"Untitled {_untitledCounter}";

        var tab = new TabState
        {
            FilePath = "",
            TempHtmlPath = Path.Combine(Path.GetTempPath(), $"mdviewer_{Guid.NewGuid():N}.html"),
            IsNewFile = true,
            HasLoadedEditor = true,
            DisplayName = displayName,
            EditContent = "",
            OriginalContent = ""
        };

        var button = CreateTabButton(tab);
        tab.TabButton = button;
        _tabPanel.Children.Add(button);
        _tabs.Add(tab);

        // Auto-enable edit mode if not already
        if (!_isEditMode)
        {
            OnToggleEditModeClick(null, null!);
        }

        SelectTab(_tabs.Count - 1);

        // Show empty preview
        var template = GetTemplate();
        tab.CachedHtml = template.Replace("{{CONTENT}}", "<p><em>Start typing in the editor...</em></p>");
        RenderHtml(tab.CachedHtml);

        _textEditor.Focus();
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
            var markdown = _isEditMode ? tab.EditContent : await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
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

    private async void OnSaveAsPdfClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count)
        {
            _statusText.Text = "No document open";
            return;
        }
        
        try
        {
            // Open print dialog - user can select "Microsoft Print to PDF" to save as PDF
            await _webView.ExecuteScriptAsync("window.print();");
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
            if (_isEditMode)
            {
                // In edit mode, re-render from editor content
                UpdatePreviewFromEditor(tab);
            }
            else if (!tab.IsNewFile)
            {
                await GenerateHtml(tab);
                if (tab.CachedHtml != null)
                    RenderHtml(tab.CachedHtml);
            }
        }
    }

    private void OnDevToolsClick(object? sender, RoutedEventArgs e)
    {
        _webView.OpenDevToolsWindow();
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
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.Full,
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
        info.Children.Add(new TextBlock { Text = "Version 1.2.1", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        info.Children.Add(new TextBlock { Text = "A lightweight markdown viewer with\nlive reload, tabs, and dark mode.", TextAlignment = Avalonia.Media.TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        info.Children.Add(new TextBlock { Text = "Built with Avalonia UI, WebView2, and Markdig", FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        
        Grid.SetRow(info, 0);
        grid.Children.Add(info);
        
        okButton.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(okButton, 1);
        grid.Children.Add(okButton);
        
        dialog.Content = grid;
        okButton.Click += (s, args) => dialog.Close();
        
        await dialog.ShowDialog(this);
    }

    private async void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SaveSettings();

        // Update editor theme
        SetupTextMateTheme();

        // Regenerate all tabs with new theme
        foreach (var tab in _tabs)
        {
            if (tab.IsNewFile && _isEditMode)
            {
                // New files generate HTML from editor content, not from disk
                var htmlContent = ConvertMarkdownToHtml(tab.EditContent);
                var template = GetTemplate();
                tab.CachedHtml = template.Replace("{{CONTENT}}", htmlContent);
            }
            else if (!tab.IsNewFile)
            {
                await GenerateHtml(tab);
            }
        }

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

        // Re-render current tab or welcome page
        if (_selectedTabIndex >= 0 && _selectedTabIndex < _tabs.Count)
        {
            var tab = _tabs[_selectedTabIndex];
            if (tab.CachedHtml != null)
                RenderHtml(tab.CachedHtml);
        }
        else
        {
            RenderHtml(GetWelcomePage());
        }
    }

    private void SetupFileWatcher(TabState tab)
    {
        tab.Watcher?.Dispose();
        
        var directory = Path.GetDirectoryName(tab.FilePath);
        var fileName = Path.GetFileName(tab.FilePath);
        
        if (directory == null) return;
        
        tab.Watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };
        
        tab.Watcher.Changed += async (s, e) =>
        {
            await Task.Delay(100);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (tab.IsModified && _isEditMode)
                {
                    // File changed externally while user has unsaved edits -- don't overwrite
                    _statusText.Text = $"Warning: {tab.FileName} changed on disk. Save to overwrite or Refresh (F5) to reload.";
                    return;
                }

                await GenerateHtml(tab);

                // Also update the editor content if in edit mode
                if (_isEditMode && File.Exists(tab.FilePath))
                {
                    var content = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
                    tab.OriginalContent = content;
                    tab.EditContent = content;

                    var idx = _tabs.IndexOf(tab);
                    if (idx == _selectedTabIndex)
                    {
                        _textEditor.TextChanged -= OnEditorTextChanged;
                        _textEditor.Text = content;
                        _textEditor.TextChanged += OnEditorTextChanged;
                    }
                }

                // If this is the selected tab, re-render
                var tabIdx = _tabs.IndexOf(tab);
                if (tabIdx == _selectedTabIndex && tab.CachedHtml != null)
                {
                    RenderHtml(tab.CachedHtml);
                }
            });
        };
        
        tab.Watcher.EnableRaisingEvents = true;
    }

    private string ConvertMarkdownToHtml(string markdown)
    {
        // Extract Mermaid blocks BEFORE Markdig processing to protect from typography transforms
        var mermaidBlocks = new List<string>();
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"```mermaid\s*\n([\s\S]*?)```",
            m => {
                mermaidBlocks.Add(m.Groups[1].Value);
                return $"<!--MERMAID_PLACEHOLDER_{mermaidBlocks.Count - 1}-->";
            },
            System.Text.RegularExpressions.RegexOptions.Multiline
        );

        // Ensure tables have a blank line before them (for Markdig compatibility)
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"(\n[^\n\|]+)\n(\|[^\n]+\|)",
            "$1\n\n$2",
            System.Text.RegularExpressions.RegexOptions.Multiline
        );

        markdown = PreprocessMath(markdown);

        var htmlContent = Markdown.ToHtml(markdown, _pipeline);

        // Restore Mermaid blocks AFTER Markdig processing
        for (int i = 0; i < mermaidBlocks.Count; i++)
        {
            htmlContent = htmlContent.Replace(
                $"<!--MERMAID_PLACEHOLDER_{i}-->",
                $"<div class=\"mermaid\">\n{mermaidBlocks[i]}</div>"
            );
        }

        return htmlContent;
    }

    private async Task GenerateHtml(TabState tab)
    {
        try
        {
            var markdown = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
            var htmlContent = ConvertMarkdownToHtml(markdown);
            var template = GetTemplate();
            tab.CachedHtml = template.Replace("{{CONTENT}}", htmlContent);
        }
        catch (Exception ex)
        {
            tab.CachedHtml = $"<html><head><meta charset=\"UTF-8\"></head><body><h1>Error</h1><p>{ex.Message}</p></body></html>";
        }
    }

    private async void RenderHtml(string html)
    {
        if (!_webViewReady)
        {
            _pendingHtml = html;
            return;
        }

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"mdviewer_current_{Guid.NewGuid():N}.html");
            File.WriteAllText(tempPath, html, new UTF8Encoding(true));
            _webView.Url = new Uri(tempPath);

            // Force focus and visual update after navigation
            await Task.Delay(100);
            _webView.Focus();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Render error: {ex.Message}";
        }
    }


    private string PreprocessMath(string markdown)
    {
        // Process display math first ($...$)
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"\$\$(.+?)\$\$",
            m => {
                var math = System.Net.WebUtility.HtmlEncode(m.Groups[1].Value.Trim());
                return $"<div class=\"math-display\" data-math=\"{math}\"></div>";
            },
            System.Text.RegularExpressions.RegexOptions.Singleline
        );
        
        // Process inline math ($...$) - simple pattern, non-greedy
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"(?<!\$)\$([^$\n]+?)\$(?!\$)",
            m => {
                var math = System.Net.WebUtility.HtmlEncode(m.Groups[1].Value.Trim());
                return $"<span class=\"math-inline\" data-math=\"{math}\"></span>";
            }
        );
        
        return markdown;
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

                if (result == "Save")
                {
                    foreach (var tab in modifiedTabs)
                    {
                        if (!tab.IsNewFile && !string.IsNullOrEmpty(tab.FilePath))
                        {
                            await SaveTabToFile(tab, tab.FilePath);
                        }
                    }
                }

                if (result != "Cancel")
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
            tab.Watcher?.Dispose();
            try { if (File.Exists(tab.TempHtmlPath)) File.Delete(tab.TempHtmlPath); } catch { }
        }

        base.OnClosed(e);
    }
}
