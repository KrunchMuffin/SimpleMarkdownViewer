using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaWebView;
using Markdig;
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

    private class TabState
    {
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public string TempHtmlPath { get; set; } = "";
        public string? CachedHtml { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public Button? TabButton { get; set; }
        public TextBlock? TabText { get; set; }
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
            case Key.O when ctrl:
                OnOpenClick(null, null!);
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
        if (e.Data.Contains(DataFormats.Files))
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
        if (!e.Data.Contains(DataFormats.Files)) return;
        
        e.Handled = true;
        
        var files = e.Data.GetFiles();
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

    private void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        _webViewReady = true;
        _statusText.Text = "Ready - Open a markdown file (Ctrl+O)";

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
    }

    private string GetWelcomePage()
    {
        var bgColor = _isDarkMode ? "#0d1117" : "#ffffff";
        var textColor = _isDarkMode ? "#8b949e" : "#656d76";
        return $@"<!DOCTYPE html>
<html><head><style>
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
        
        .mermaid {{ text-align: center; margin: 16px 0; }}
        
        kbd {{
            display: inline-block;
            padding: 3px 5px;
            font-size: 11px;
            color: {textColor};
            background-color: {codeBg};
            border: 1px solid {borderColor};
            border-radius: 6px;
        }}
    </style>
</head>
<body>
    <article id=""content"">
        {{{{CONTENT}}}}
    </article>
    
    <script>
        function renderContent() {{
            mermaid.initialize({{ 
                startOnLoad: false, 
                theme: '{mermaidTheme}',
                securityLevel: 'loose'
            }});
            
            try {{ mermaid.run({{ querySelector: '.mermaid' }}); }} catch (e) {{ console.error(e); }}
            
            document.querySelectorAll('pre code').forEach((block) => {{
                if (!block.closest('.mermaid')) hljs.highlightElement(block);
            }});
            
            // Render preprocessed math elements
            if (typeof katex !== 'undefined') {{
                document.querySelectorAll('.math-display').forEach((el) => {{
                    try {{
                        const math = el.getAttribute('data-math');
                        if (math) {{
                            // Decode HTML entities
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
            
            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                _statusText.Text = "Clipboard is empty or contains no text";
                return;
            }
            
            // Create a temp file with the clipboard content
            var tempPath = Path.Combine(Path.GetTempPath(), $"Clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(tempPath, text);
            
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
        _statusText.Text = tab.FilePath;
        Title = $"Simple Markdown Viewer - {tab.FileName}";
        
        // Load content
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
    }

    private void CloseTab(TabState tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        
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

    private async void OnCopyMarkdownClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTabIndex < 0 || _selectedTabIndex >= _tabs.Count) return;
        
        var tab = _tabs[_selectedTabIndex];
        try
        {
            var markdown = await File.ReadAllTextAsync(tab.FilePath);
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
            await GenerateHtml(tab);
            if (tab.CachedHtml != null)
                RenderHtml(tab.CachedHtml);
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
        info.Children.Add(new TextBlock { Text = "Version 1.0.2", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
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
        
        // Regenerate all tabs with new theme
        foreach (var tab in _tabs)
        {
            await GenerateHtml(tab);
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
                await GenerateHtml(tab);
                
                // If this is the selected tab, re-render
                var idx = _tabs.IndexOf(tab);
                if (idx == _selectedTabIndex && tab.CachedHtml != null)
                {
                    RenderHtml(tab.CachedHtml);
                }
            });
        };
        
        tab.Watcher.EnableRaisingEvents = true;
    }

    private async Task GenerateHtml(TabState tab)
    {
        try
        {
            var markdown = await File.ReadAllTextAsync(tab.FilePath);
            markdown = PreprocessMermaid(markdown);
            markdown = PreprocessMath(markdown);
            
            var htmlContent = Markdown.ToHtml(markdown, _pipeline);
            var template = GetTemplate();
            tab.CachedHtml = template.Replace("{{CONTENT}}", htmlContent);
        }
        catch (Exception ex)
        {
            tab.CachedHtml = $"<html><body><h1>Error</h1><p>{ex.Message}</p></body></html>";
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
            File.WriteAllText(tempPath, html);
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

    private string PreprocessMermaid(string markdown)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"```mermaid\s*\n([\s\S]*?)```",
            m => $"<div class=\"mermaid\">\n{m.Groups[1].Value}</div>",
            System.Text.RegularExpressions.RegexOptions.Multiline
        );
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

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs)
        {
            tab.Watcher?.Dispose();
            try { if (File.Exists(tab.TempHtmlPath)) File.Delete(tab.TempHtmlPath); } catch { }
        }
        
        base.OnClosed(e);
    }
}
