using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Markdig;
using Markdig.Extensions.Yaml;
using MdEditor.Services;
using MdEditor.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MdEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly MarkdownRenderer _renderer = new();

    // Markdig pipeline reused by the validator so behaviour matches the preview.
    // YAML front-matter is parsed as its own block so it doesn't get mis-interpreted
    // as a Setext heading - that mis-parse silently broke heading-based rules on
    // Hugo content files.
    private readonly MarkdownPipeline _validatorPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UsePreciseSourceLocation()
        .Build();

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _fsDebounce;
    private DispatcherTimer? _editDebounce;
    private CompletionWindow? _completionWindow;

    private string? _currentFile;
    private string? _currentFileEncoding;
    private bool _webViewReady;
    private string? _pendingHtml;
    private bool _suppressThemeChange;
    private bool _suppressEditorChange;
    private bool _suppressEditorLayoutChange;

    private EditorPosition _editorPosition = EditorPosition.Bottom;
    // Remembers the last non-Hidden position so Ctrl+E can restore it.
    private EditorPosition _lastVisibleEditorPosition = EditorPosition.Bottom;

    // Per-user temp dir mapped to MarkdownRenderer.PreviewVirtualHost.
    // The preview HTML is written here as a real file so we Navigate(url) instead of
    // NavigateToString(html) - the latter has a 2 MB limit which breaks files with
    // many inline base64 images.
    private string _previewDir = string.Empty;
    private string _previewFile = string.Empty;
    private long _previewCounter;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.RequestOpenFolder = OpenFolderDialog;
        _vm.RequestNewFile = CreateNewFile;
        _vm.RequestRefresh = RefreshCurrent;
        _vm.RequestSave = SaveCurrent;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDirty))
            {
                DirtyIndicator.Visibility = _vm.IsDirty ? Visibility.Visible : Visibility.Collapsed;
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySavedTheme();
        PopulateThemePicker();
        ApplyTitleBarForCurrentFlavor();
        ApplyEditorHighlighting(ThemeManager.Current);

        _vm.ExplorerVisible = LayoutSettings.LoadExplorerVisibleOrDefault();
        var savedPos = LayoutSettings.LoadOrDefault();
        if (savedPos != EditorPosition.Hidden) _lastVisibleEditorPosition = savedPos;
        PopulateEditorLayoutPicker(savedPos);
        ApplyEditorLayout(savedPos);

        ThemeManager.FlavorChanged += OnFlavorChanged;
        App.RemoteFileRequested += OnRemoteFileRequested;

        // Wire editor events
        Editor.TextChanged += Editor_TextChanged;
        Editor.TextArea.TextEntered += Editor_TextEntered;
        Editor.KeyDown += Editor_KeyDown;

        // Ctrl+S anywhere in the window saves
        InputBindings.Add(new KeyBinding(_vm.SaveCommand, new KeyGesture(Key.S, ModifierKeys.Control)));
        // Ctrl+N creates a new markdown file
        InputBindings.Add(new KeyBinding(_vm.NewFileCommand, new KeyGesture(Key.N, ModifierKeys.Control)));
        // Ctrl+E toggles the editor pane (hide <-> last visible position)
        InputBindings.Add(new KeyBinding(
            new RelayCommand(_ => ToggleEditorVisibility()),
            new KeyGesture(Key.E, ModifierKeys.Control)));

        await InitializeWebViewAsync();

        // Startup file (from explorer file association / CLI)
        if (!string.IsNullOrEmpty(App.StartupFilePath) && File.Exists(App.StartupFilePath))
        {
            OpenFileExternally(App.StartupFilePath);
        }
        else
        {
            ShowWelcome();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.FlavorChanged -= OnFlavorChanged;
        App.RemoteFileRequested -= OnRemoteFileRequested;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnRemoteFileRequested(string path)
    {
        // A second instance forwarded this file; we're already on the UI thread because
        // App posts via Dispatcher.BeginInvoke.
        if (!File.Exists(path)) return;
        OpenFileExternally(path);
    }

    /// <summary>
    /// Open an arbitrary file path: ensure its containing folder is loaded in the tree,
    /// then load the file into the editor + preview.
    /// </summary>
    private void OpenFileExternally(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) &&
            (string.IsNullOrEmpty(_vm.RootPath) ||
             !string.Equals(_vm.RootPath, dir, StringComparison.OrdinalIgnoreCase)))
        {
            _vm.LoadRoot(dir);
            AttachWatcher(dir);
        }
        LoadFileIntoEditor(path);
    }

    private void ApplySavedTheme()
    {
        var flavor = ThemeManager.LoadSavedOrDefault();
        ThemeManager.Apply(flavor);
    }

    private void PopulateThemePicker()
    {
        _suppressThemeChange = true;
        ThemePicker.ItemsSource = ThemeManager.All
            .Select(f => new ThemeOption(f, ThemeManager.DisplayName(f)))
            .ToList();
        ThemePicker.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemePicker.SelectedItem = ((IEnumerable<ThemeOption>)ThemePicker.ItemsSource)
            .FirstOrDefault(o => o.Flavor == ThemeManager.Current);
        _suppressThemeChange = false;
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange) return;
        if (ThemePicker.SelectedItem is ThemeOption opt && opt.Flavor != ThemeManager.Current)
        {
            ThemeManager.Apply(opt.Flavor);
        }
    }

    private void PopulateEditorLayoutPicker(EditorPosition current)
    {
        _suppressEditorLayoutChange = true;
        var options = new[]
        {
            EditorPosition.Hidden,
            EditorPosition.Bottom,
            EditorPosition.Right,
            EditorPosition.Top,
            EditorPosition.Left,
        }.Select(p => new EditorLayoutOption(p, LayoutSettings.DisplayName(p))).ToList();

        EditorLayoutPicker.ItemsSource = options;
        EditorLayoutPicker.DisplayMemberPath = nameof(EditorLayoutOption.Label);
        EditorLayoutPicker.SelectedItem = options.FirstOrDefault(o => o.Position == current);
        _suppressEditorLayoutChange = false;
    }

    private void EditorLayoutPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorLayoutChange) return;
        if (EditorLayoutPicker.SelectedItem is EditorLayoutOption opt && opt.Position != _editorPosition)
        {
            ApplyEditorLayout(opt.Position);
        }
    }

    private void ToggleEditorVisibility()
    {
        var next = _editorPosition == EditorPosition.Hidden
            ? _lastVisibleEditorPosition
            : EditorPosition.Hidden;
        // Selecting in the picker triggers SelectionChanged -> ApplyEditorLayout.
        SelectEditorLayout(next);
    }

    private void SelectEditorLayout(EditorPosition pos)
    {
        if (EditorLayoutPicker.ItemsSource is IEnumerable<EditorLayoutOption> items)
        {
            var match = items.FirstOrDefault(o => o.Position == pos);
            if (match is not null) EditorLayoutPicker.SelectedItem = match;
        }
    }

    private void ApplyEditorLayout(EditorPosition pos)
    {
        _editorPosition = pos;
        if (pos != EditorPosition.Hidden) _lastVisibleEditorPosition = pos;

        var grid = PreviewEditorGrid;
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();

        // Reset attached props before reassigning.
        foreach (var el in new System.Windows.FrameworkElement[] { Preview, EditorSplitter, EditorPane })
        {
            Grid.SetRow(el, 0);
            Grid.SetColumn(el, 0);
            Grid.SetRowSpan(el, 1);
            Grid.SetColumnSpan(el, 1);
        }

        EditorSplitter.Visibility = Visibility.Visible;
        EditorPane.Visibility = Visibility.Visible;
        // Splitter is a visual separator only - the editor/preview ratio is locked
        // to 50/50 so the user can't drag it out of balance.
        EditorSplitter.IsEnabled = false;

        const double minPane = 120;
        var half = new GridLength(1, GridUnitType.Star);

        switch (pos)
        {
            case EditorPosition.Hidden:
                grid.RowDefinitions.Add(new RowDefinition { Height = half });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half });
                EditorSplitter.Visibility = Visibility.Collapsed;
                EditorPane.Visibility = Visibility.Collapsed;
                break;

            case EditorPosition.Bottom:
                grid.RowDefinitions.Add(new RowDefinition { Height = half, MinHeight = minPane });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = half, MinHeight = minPane });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half });
                Grid.SetRow(Preview, 0);
                Grid.SetRow(EditorSplitter, 1);
                Grid.SetRow(EditorPane, 2);
                ConfigureSplitter(horizontal: true);
                break;

            case EditorPosition.Top:
                grid.RowDefinitions.Add(new RowDefinition { Height = half, MinHeight = minPane });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = half, MinHeight = minPane });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half });
                Grid.SetRow(EditorPane, 0);
                Grid.SetRow(EditorSplitter, 1);
                Grid.SetRow(Preview, 2);
                ConfigureSplitter(horizontal: true);
                break;

            case EditorPosition.Right:
                grid.RowDefinitions.Add(new RowDefinition { Height = half });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half, MinWidth = 200 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half, MinWidth = 200 });
                Grid.SetColumn(Preview, 0);
                Grid.SetColumn(EditorSplitter, 1);
                Grid.SetColumn(EditorPane, 2);
                ConfigureSplitter(horizontal: false);
                break;

            case EditorPosition.Left:
                grid.RowDefinitions.Add(new RowDefinition { Height = half });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half, MinWidth = 200 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = half, MinWidth = 200 });
                Grid.SetColumn(EditorPane, 0);
                Grid.SetColumn(EditorSplitter, 1);
                Grid.SetColumn(Preview, 2);
                ConfigureSplitter(horizontal: false);
                break;
        }

        LayoutSettings.Save(pos);
    }

    private void ConfigureSplitter(bool horizontal)
    {
        if (horizontal)
        {
            EditorSplitter.Height = 1;
            EditorSplitter.Width = double.NaN;
            EditorSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            EditorSplitter.VerticalAlignment = VerticalAlignment.Center;
            EditorSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            EditorSplitter.Width = 1;
            EditorSplitter.Height = double.NaN;
            EditorSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            EditorSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            EditorSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
    }

    private void OnFlavorChanged(CatppuccinFlavor flavor)
    {
        ApplyTitleBarForCurrentFlavor();
        ApplyEditorHighlighting(flavor);
        ThemeLabel.Text = $"Catppuccin {flavor}";

        if (_currentFile is not null && File.Exists(_currentFile))
        {
            RenderPreview(Editor.Text);
        }
        else
        {
            ShowWelcome();
        }
    }

    private void ApplyTitleBarForCurrentFlavor()
    {
        DarkTitleBar.Apply(this, dark: ThemeManager.IsDark(ThemeManager.Current));
    }

    private void ApplyEditorHighlighting(CatppuccinFlavor flavor)
    {
        Editor.SyntaxHighlighting = MarkdownHighlighting.Build(flavor);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "md-editor",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Preview.EnsureCoreWebView2Async(env);
            var settings = Preview.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = true;
            // Untrusted markdown can embed raw <script>. Defense in depth: turn JS off.
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;

            // Preview temp dir + virtual host. We write the rendered HTML here and
            // Navigate() to its URL rather than NavigateToString'ing a huge string.
            _previewDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "md-editor", "Preview");
            Directory.CreateDirectory(_previewDir);
            _previewFile = Path.Combine(_previewDir, MarkdownRenderer.PreviewFileName);
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MarkdownRenderer.PreviewVirtualHost,
                _previewDir,
                CoreWebView2HostResourceAccessKind.Allow);

            // Intercept external link clicks - open them in the system browser
            // instead of replacing the preview pane.
            Preview.CoreWebView2.NavigationStarting += OnPreviewNavigationStarting;
            Preview.CoreWebView2.NewWindowRequested += OnPreviewNewWindowRequested;

            _webViewReady = true;
            if (_pendingHtml is not null)
            {
                NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"WebView2 init failed: {ex.Message}";
        }
    }

    private void OnPreviewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? string.Empty;
        // Allow our own preview loads (file-backed Navigate to md-editor-preview.local,
        // plus the legacy NavigateToString welcome screen).
        var previewOrigin = $"https://{MarkdownRenderer.PreviewVirtualHost}/";
        if (string.IsNullOrEmpty(uri)
            || uri.StartsWith(previewOrigin, StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Virtual-host links (https://md-editor.local/...) are clicks on relative
        // markdown links inside the rendered preview. Map them back to local files
        // and open them in the editor (or, for non-md files, hand to the OS).
        var virtualOrigin = $"https://{MarkdownRenderer.VirtualHost}/";
        if (uri.StartsWith(virtualOrigin, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            HandleInternalLink(uri, virtualOrigin);
            return;
        }

        // Truly external: hand off to the default browser / mail client.
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            OpenExternal(uri);
            return;
        }
        // Anything else (custom protocols etc.) - block.
        e.Cancel = true;
        _vm.StatusText = $"Blocked navigation to {uri}";
    }

    private void HandleInternalLink(string uri, string virtualOrigin)
    {
        if (_currentFile is null) return;
        var sourceDir = Path.GetDirectoryName(_currentFile);
        if (string.IsNullOrEmpty(sourceDir)) return;

        // Strip the origin, then trim anchor (#) and query (?)
        var remainder = uri[virtualOrigin.Length..];
        var hashIdx = remainder.IndexOf('#');
        if (hashIdx >= 0) remainder = remainder[..hashIdx];
        var queryIdx = remainder.IndexOf('?');
        if (queryIdx >= 0) remainder = remainder[..queryIdx];
        if (string.IsNullOrEmpty(remainder)) return;

        var relPath = Uri.UnescapeDataString(remainder).Replace('/', Path.DirectorySeparatorChar);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(sourceDir, relPath));
        }
        catch
        {
            _vm.StatusText = $"Couldn't resolve internal link: {remainder}";
            return;
        }

        if (!File.Exists(fullPath))
        {
            _vm.StatusText = $"Internal link target not found: {remainder}";
            return;
        }

        if (MainViewModel.IsMarkdownFile(fullPath))
        {
            OpenFileExternally(fullPath);
        }
        else
        {
            // Non-markdown local file (PDF, image, etc.) - let the OS handle it.
            OpenExternal(fullPath);
        }
    }

    private void OnPreviewNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Couldn't open link: {ex.Message}";
        }
    }

    private void NavigateToString(string html)
    {
        if (!_webViewReady || Preview.CoreWebView2 is null)
        {
            _pendingHtml = html;
            return;
        }

        // Write the HTML to a real file and Navigate() to its virtual-host URL.
        // This sidesteps WebView2's documented 2 MB cap on NavigateToString, which
        // causes the app to appear to hang when a single .md inlines many large
        // base64 images.
        try
        {
            File.WriteAllText(_previewFile, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // Bust the WebView2 cache so re-renders (theme switch, live edit) actually reload.
            var bust = ++_previewCounter;
            var url = $"https://{MarkdownRenderer.PreviewVirtualHost}/{MarkdownRenderer.PreviewFileName}?v={bust}";
            Preview.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Preview render failed: {ex.Message}";
        }
    }

    private void ShowWelcome()
    {
        var html = _renderer.RenderEmpty(
            "Welcome to Markdown Viewer",
            "Click 'Open folder…' in the toolbar, pick a file, then edit it below.",
            ThemeManager.Current);
        NavigateToString(html);
        EditorTitle.Text = string.Empty;
        Editor.IsEnabled = false;
        IssuesList.ItemsSource = Array.Empty<ValidationIssue>();
        _vm.IssuesSummary = "No file open";
    }

    private void OpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder to browse" };
        if (dlg.ShowDialog(this) == true && Directory.Exists(dlg.FolderName))
        {
            _vm.LoadRoot(dlg.FolderName);
            AttachWatcher(dlg.FolderName);
        }
    }

    private void CreateNewFile()
    {
        if (_vm.IsDirty)
        {
            var answer = MessageBox.Show(
                this,
                $"'{Path.GetFileName(_currentFile)}' has unsaved changes. Discard them and create a new file?",
                "Unsaved changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Create a new Markdown file",
            Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn|All files|*.*",
            DefaultExt = ".md",
            FileName = "Untitled.md",
            OverwritePrompt = true,
            InitialDirectory = !string.IsNullOrEmpty(_vm.RootPath) && Directory.Exists(_vm.RootPath)
                ? _vm.RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            TextFileIO.WriteUtf8NoBom(dlg.FileName, string.Empty);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Couldn't create file: {ex.Message}";
            return;
        }

        // OpenFileExternally only reloads the tree when the new file's folder differs
        // from the currently open root. If it's inside the current root (same folder or
        // a subfolder), refresh explicitly so the new file actually shows up in the tree.
        var newFileDir = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(_vm.RootPath) && !string.IsNullOrEmpty(newFileDir) &&
            (string.Equals(_vm.RootPath, newFileDir, StringComparison.OrdinalIgnoreCase) ||
             newFileDir.StartsWith(_vm.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            _vm.LoadRoot(_vm.RootPath);
        }

        OpenFileExternally(dlg.FileName);
        _vm.StatusText = $"Created {Path.GetFileName(dlg.FileName)}";
    }

    private void AttachWatcher(string root)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                              | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnFsChange;
            _watcher.Created += OnFsChange;
            _watcher.Deleted += OnFsChange;
            _watcher.Renamed += OnFsChange;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Watcher disabled: {ex.Message}";
        }
    }

    private void OnFsChange(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_currentFile is null) return;
            if (!string.Equals(e.FullPath, _currentFile, StringComparison.OrdinalIgnoreCase)) return;
            // If the user has unsaved edits, don't clobber them; just note it.
            if (_vm.IsDirty)
            {
                _vm.StatusText = "File changed on disk - editor has unsaved changes, not reloading.";
                return;
            }
            ScheduleDiskReload();
        });
    }

    private void ScheduleDiskReload()
    {
        _fsDebounce?.Stop();
        _fsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fsDebounce.Tick += (_, _) =>
        {
            _fsDebounce?.Stop();
            if (_currentFile is not null) LoadFileIntoEditor(_currentFile);
        };
        _fsDebounce.Start();
    }

    private void RefreshCurrent()
    {
        if (!string.IsNullOrEmpty(_vm.RootPath))
        {
            _vm.LoadRoot(_vm.RootPath);
        }
        if (_currentFile is not null && File.Exists(_currentFile))
        {
            LoadFileIntoEditor(_currentFile);
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNodeViewModel node && !node.IsDirectory)
        {
            // Prompt-ish behavior: if dirty, ignore selection change silently keeping current file.
            if (_vm.IsDirty)
            {
                var answer = MessageBox.Show(
                    this,
                    $"'{Path.GetFileName(_currentFile)}' has unsaved changes. Discard them and open '{node.Name}'?",
                    "Unsaved changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
            LoadFileIntoEditor(node.FullPath);
        }
    }

    private void LoadFileIntoEditor(string path)
    {
        if (!File.Exists(path))
        {
            _vm.StatusText = $"File not found: {path}";
            return;
        }

        try
        {
            var (text, encodingName) = TextFileIO.ReadAutoDetect(path);

            _suppressEditorChange = true;
            Editor.Text = text;
            _suppressEditorChange = false;

            Editor.IsEnabled = true;
            _currentFile = path;
            _currentFileEncoding = encodingName;
            _vm.IsDirty = false;
            EditorTitle.Text = $"{path} · {encodingName}";

            RenderPreview(text);
            RunValidation(text);
            UpdateStatusForFile(path, text.Length);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Failed to open: {ex.Message}";
        }
    }

    private void RenderPreview(string markdown)
    {
        const int previewLimit = 2 * 1024 * 1024;
        var truncated = false;
        var text = markdown;
        if (text.Length > previewLimit)
        {
            text = text[..previewLimit] + "\n\n> ⚠️ *Preview truncated — only the first 2 MB rendered.*\n";
            truncated = true;
        }

        // Make sure the WebView2 virtual host points at the current file's directory
        // BEFORE we navigate. Otherwise relative <img> tags resolve against the old
        // directory (or nothing, on first paint).
        UpdateVirtualHostMapping();

        var html = _renderer.Render(text, _currentFile, ThemeManager.Current);
        NavigateToString(html);

        if (_currentFile is not null) UpdateStatusForFile(_currentFile, markdown.Length, truncated);
    }

    private void UpdateVirtualHostMapping()
    {
        if (Preview.CoreWebView2 is null) return;
        if (_currentFile is null) return;
        var dir = Path.GetDirectoryName(_currentFile);
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            // Replacing the mapping with the same hostname overrides the previous one,
            // so switching files just calls this again. CoreWebView2HostResourceAccessKind.Allow
            // permits cross-origin loads from our 'about:blank' document.
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MarkdownRenderer.VirtualHost,
                dir,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Virtual host mapping failed: {ex.Message}";
        }
    }

    private void UpdateStatusForFile(string path, int length, bool truncated = false)
    {
        string size = length switch
        {
            < 1024 => $"{length} B",
            < 1024 * 1024 => $"{length / 1024.0:F1} KB",
            _ => $"{length / (1024.0 * 1024.0):F2} MB"
        };
        var dirty = _vm.IsDirty ? " · modified" : string.Empty;
        var trunc = truncated ? " · truncated" : string.Empty;
        _vm.StatusText = $"{Path.GetFileName(path)} · {size}{dirty}{trunc}";
    }

    private void SaveCurrent()
    {
        if (_currentFile is null) return;
        try
        {
            // Suppress the watcher's incoming reload for our own write
            if (_watcher is not null) _watcher.EnableRaisingEvents = false;
            // Always save as UTF-8 without BOM. If the file was originally cp1252,
            // this is a deliberate upgrade - modern tooling expects UTF-8.
            TextFileIO.WriteUtf8NoBom(_currentFile, Editor.Text);
            _currentFileEncoding = "UTF-8";
            EditorTitle.Text = $"{_currentFile} · UTF-8";
            _vm.IsDirty = false;
            _vm.StatusText = $"Saved {Path.GetFileName(_currentFile)} as UTF-8";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            if (_watcher is not null) _watcher.EnableRaisingEvents = true;
        }
    }

    // ============== Editor events ==============

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange) return;
        if (!Editor.IsEnabled) return;

        _vm.IsDirty = true;

        _editDebounce?.Stop();
        _editDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _editDebounce.Tick += (_, _) =>
        {
            _editDebounce?.Stop();
            var text = Editor.Text;
            RenderPreview(text);
            RunValidation(text);
        };
        _editDebounce.Start();
    }

    private void Editor_TextEntered(object? sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Auto-open completion on '[' (link), '!' (image), or backtick (code/fence).
        switch (e.Text)
        {
            case "[":
                ShowCompletion(MarkdownCompletionProvider.Snippets()
                    .Where(d => d.Text.StartsWith("[") || d.Text.StartsWith("!")));
                break;
            case "!":
                ShowCompletion(MarkdownCompletionProvider.Snippets()
                    .Where(d => d.Text.StartsWith("!")));
                break;
            case "`":
                // If they just typed the third backtick at line start -> language picker
                var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
                var lineText = Editor.Document.GetText(line);
                if (lineText.TrimStart().StartsWith("```"))
                {
                    ShowCompletion(MarkdownCompletionProvider.CodeFenceLanguages());
                }
                break;
        }
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Space -> full snippet list
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowCompletion(MarkdownCompletionProvider.Snippets());
            e.Handled = true;
        }
    }

    private void ShowCompletion(IEnumerable<ICompletionData> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        _completionWindow = new CompletionWindow(Editor.TextArea);
        foreach (var item in list)
        {
            _completionWindow.CompletionList.CompletionData.Add(item);
        }
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    // ============== Validation ==============

    private void RunValidation(string markdown)
    {
        IReadOnlyList<ValidationIssue> issues;
        try
        {
            issues = MarkdownValidator.Validate(markdown, _currentFile, _validatorPipeline);
        }
        catch (Exception ex)
        {
            // The debounced TextChanged timer would otherwise swallow this and leave
            // the issues panel frozen with stale data. Surface it instead.
            _vm.StatusText = $"Validator crashed: {ex.Message}";
            var crash = new[] {
                new ValidationIssue(1, 1, IssueSeverity.Error, $"Validator crashed: {ex.Message}")
            };
            IssuesList.ItemsSource = crash;
            _vm.IssuesSummary = "ISSUES — validator error";
            return;
        }

        IssuesList.ItemsSource = issues;
        if (issues.Count == 0)
        {
            _vm.IssuesSummary = "ISSUES — no problems found";
        }
        else
        {
            int errors = 0, warns = 0, infos = 0;
            foreach (var i in issues)
            {
                switch (i.Severity)
                {
                    case IssueSeverity.Error: errors++; break;
                    case IssueSeverity.Warning: warns++; break;
                    case IssueSeverity.Info: infos++; break;
                }
            }
            _vm.IssuesSummary = $"ISSUES — {errors} error(s), {warns} warning(s), {infos} info";
        }
    }

    private void IssuesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssuesList.SelectedItem is ValidationIssue issue)
        {
            JumpToLine(issue.Line, issue.Column);
        }
    }

    private void JumpToLine(int line, int column)
    {
        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        var docLine = Editor.Document.GetLineByNumber(line);
        Editor.CaretOffset = Math.Min(docLine.Offset + Math.Max(0, column - 1), docLine.EndOffset);
        Editor.ScrollToLine(line);
        Editor.Focus();
    }

    // ============== Markdown formatting toolbar ==============

    private void MdBtn_Heading1_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(1);
    private void MdBtn_Heading2_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(2);
    private void MdBtn_Heading3_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(3);
    private void MdBtn_Heading4_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(4);
    private void MdBtn_Heading5_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(5);
    private void MdBtn_Heading6_Click(object sender, RoutedEventArgs e) => SetCurrentLineHeading(6);
    private void MdBtn_Bold_Click(object sender, RoutedEventArgs e) => WrapSelection("**", "**", "bold text");
    private void MdBtn_Italic_Click(object sender, RoutedEventArgs e) => WrapSelection("*", "*", "italic text");
    private void MdBtn_Strike_Click(object sender, RoutedEventArgs e) => WrapSelection("~~", "~~", "strikethrough");
    private void MdBtn_InlineCode_Click(object sender, RoutedEventArgs e) => WrapSelection("`", "`", "code");
    private void MdBtn_Underline_Click(object sender, RoutedEventArgs e) => WrapSelection("++", "++", "underline");
    private void MdBtn_Highlight_Click(object sender, RoutedEventArgs e) => WrapSelection("==", "==", "highlight");
    private void MdBtn_Superscript_Click(object sender, RoutedEventArgs e) => WrapSelection("^", "^", "sup");
    private void MdBtn_Subscript_Click(object sender, RoutedEventArgs e) => WrapSelection("~", "~", "sub");
    private void MdBtn_Footnote_Click(object sender, RoutedEventArgs e) => InsertFootnote();
    private void MdBtn_Link_Click(object sender, RoutedEventArgs e) => InsertLink();
    private void MdBtn_Image_Click(object sender, RoutedEventArgs e) => InsertImage();
    private void MdBtn_BulletList_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("- ");
    private void MdBtn_NumberedList_Click(object sender, RoutedEventArgs e) => NumberSelectedLines();
    private void MdBtn_TaskList_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("- [ ] ");
    private void MdBtn_Quote_Click(object sender, RoutedEventArgs e) => PrefixSelectedLines("> ");
    private void MdBtn_CodeBlock_Click(object sender, RoutedEventArgs e) => InsertCodeBlock();
    private void MdBtn_Table_Click(object sender, RoutedEventArgs e) => InsertTable();
    private void MdBtn_HorizontalRule_Click(object sender, RoutedEventArgs e) => InsertBlock("---");

    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            var start = Editor.SelectionStart;
            var len = Editor.SelectionLength;
            var text = doc.GetText(start, len);
            doc.Replace(start, len, prefix + text + suffix);
            Editor.Select(start + prefix.Length, len);
        }
        else
        {
            var caret = Editor.CaretOffset;
            doc.Insert(caret, prefix + placeholder + suffix);
            Editor.Select(caret + prefix.Length, placeholder.Length);
        }
        Editor.Focus();
    }

    private void SetCurrentLineHeading(int level)
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        var lineText = doc.GetText(line);
        // Strip any existing leading '#' run + one optional space - acts as a toggle/replace.
        var trimmed = lineText.TrimStart('#');
        if (trimmed.StartsWith(' ')) trimmed = trimmed[1..];
        var prefix = new string('#', level) + " ";
        doc.Replace(line.Offset, line.Length, prefix + trimmed);
        Editor.CaretOffset = line.Offset + prefix.Length + trimmed.Length;
        Editor.Focus();
    }

    private void PrefixSelectedLines(string prefix)
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        var (startLine, endLine) = GetSelectedLineRange();
        using (doc.RunUpdate())
        {
            for (int i = startLine; i <= endLine; i++)
            {
                var line = doc.GetLineByNumber(i);
                doc.Insert(line.Offset, prefix);
            }
        }
        Editor.Focus();
    }

    private void NumberSelectedLines()
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        var (startLine, endLine) = GetSelectedLineRange();
        using (doc.RunUpdate())
        {
            int n = 1;
            for (int i = startLine; i <= endLine; i++)
            {
                var line = doc.GetLineByNumber(i);
                doc.Insert(line.Offset, $"{n}. ");
                n++;
            }
        }
        Editor.Focus();
    }

    private (int startLine, int endLine) GetSelectedLineRange()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            var s = doc.GetLineByOffset(Editor.SelectionStart).LineNumber;
            var e = doc.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength).LineNumber;
            return (s, e);
        }
        var current = doc.GetLineByOffset(Editor.CaretOffset).LineNumber;
        return (current, current);
    }

    private void InsertLink()
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        var selText = Editor.SelectedText ?? string.Empty;
        var selectionLooksLikeUrl =
            selText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            selText.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        string textPart, urlPart;
        if (selectionLooksLikeUrl) { textPart = "link text"; urlPart = selText; }
        else if (!string.IsNullOrEmpty(selText)) { textPart = selText; urlPart = "https://"; }
        else { textPart = "link text"; urlPart = "https://"; }

        var snippet = $"[{textPart}]({urlPart})";
        int insertAt;
        if (Editor.SelectionLength > 0)
        {
            insertAt = Editor.SelectionStart;
            doc.Replace(insertAt, Editor.SelectionLength, snippet);
        }
        else
        {
            insertAt = Editor.CaretOffset;
            doc.Insert(insertAt, snippet);
        }

        // Select whichever part the user is most likely to edit next.
        if (selectionLooksLikeUrl)
        {
            Editor.Select(insertAt + 1, textPart.Length); // text part
        }
        else if (!string.IsNullOrEmpty(selText))
        {
            var urlStart = insertAt + 1 + textPart.Length + 2; // skip "[text]("
            Editor.Select(urlStart, urlPart.Length);
        }
        else
        {
            Editor.Select(insertAt + 1, textPart.Length);
        }
        Editor.Focus();
    }

    private void InsertImage()
    {
        if (!Editor.IsEnabled) return;
        var dlg = new OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg;*.bmp|All files|*.*",
            Multiselect = false
        };
        if (_currentFile is not null)
        {
            var dir = Path.GetDirectoryName(_currentFile);
            if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) != true) return;
        var picked = dlg.FileName;

        // Prefer a path relative to the current markdown file so the link survives a move.
        string href = picked;
        var srcDir = _currentFile is not null ? Path.GetDirectoryName(_currentFile) : null;
        if (!string.IsNullOrEmpty(srcDir))
        {
            try
            {
                href = Path.GetRelativePath(srcDir, picked).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch { /* fall back to the absolute path */ }
        }

        var alt = Path.GetFileNameWithoutExtension(picked);
        var snippet = $"![{alt}]({href})";

        int insertAt = Editor.CaretOffset;
        if (Editor.SelectionLength > 0)
        {
            insertAt = Editor.SelectionStart;
            Editor.Document.Replace(insertAt, Editor.SelectionLength, snippet);
        }
        else
        {
            Editor.Document.Insert(insertAt, snippet);
        }
        Editor.Select(insertAt + 2, alt.Length); // pre-select the alt text for quick edit
        Editor.Focus();
    }

    private void InsertCodeBlock()
    {
        // Cursor lands on the empty line inside the fence.
        InsertBlock("```\n\n```", caretOffsetWithinBlock: 4);
    }

    private void InsertTable()
    {
        var table =
            "| Column 1 | Column 2 | Column 3 |\n" +
            "|----------|----------|----------|\n" +
            "| Cell     | Cell     | Cell     |";
        InsertBlock(table);
    }

    /// <summary>
    /// Insert a footnote: a <c>[^n]</c> reference at the caret plus a matching
    /// <c>[^n]: </c> definition appended to the end of the document. The footnote
    /// number is the next unused integer label so repeated inserts don't collide.
    /// </summary>
    private void InsertFootnote()
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;

        // Pick the next free numeric footnote label across existing references/definitions.
        int next = 1;
        foreach (Match m in Regex.Matches(doc.Text, @"\[\^(\d+)\]"))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= next) next = n + 1;
        }

        var reference = $"[^{next}]";
        var caret = Editor.CaretOffset;
        doc.Insert(caret, reference);

        // Append the definition at the end of the document, ensuring a blank line before it.
        var text = doc.Text;
        var sb = new StringBuilder();
        if (text.Length > 0 && !text.EndsWith("\n")) sb.Append('\n');
        if (!text.EndsWith("\n\n")) sb.Append('\n');
        sb.Append($"[^{next}]: ");
        int defTextOffset = doc.TextLength + sb.Length; // caret lands after "[^n]: "
        doc.Insert(doc.TextLength, sb.ToString());

        Editor.CaretOffset = defTextOffset;
        Editor.Focus();
    }

    /// <summary>
    /// Insert a multi-line block at the caret. If the caret's line is empty the block
    /// replaces it; otherwise the block is appended after the current line with blank
    /// lines around it so it parses as its own markdown block.
    /// </summary>
    private void InsertBlock(string text, int? caretOffsetWithinBlock = null)
    {
        if (!Editor.IsEnabled) return;
        var doc = Editor.Document;
        var caret = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var lineText = doc.GetText(line);
        var lineIsEmpty = string.IsNullOrWhiteSpace(lineText);

        int finalCaret;
        if (lineIsEmpty)
        {
            doc.Replace(line.Offset, line.Length, text);
            finalCaret = line.Offset + (caretOffsetWithinBlock ?? text.Length);
        }
        else
        {
            var inserted = "\n\n" + text + "\n";
            doc.Insert(line.EndOffset, inserted);
            finalCaret = line.EndOffset + 2 + (caretOffsetWithinBlock ?? text.Length);
        }
        Editor.CaretOffset = Math.Min(finalCaret, doc.TextLength);
        Editor.Focus();
    }

    private sealed record ThemeOption(CatppuccinFlavor Flavor, string Label);
    private sealed record EditorLayoutOption(EditorPosition Position, string Label);
}
