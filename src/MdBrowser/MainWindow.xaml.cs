using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Markdig;
using MdBrowser.Services;
using MdBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MdBrowser;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly MarkdownRenderer _renderer = new();

    // Markdig pipeline reused by the validator so behaviour matches the preview.
    private readonly MarkdownPipeline _validatorPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.RequestOpenFolder = OpenFolderDialog;
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

        ThemeManager.FlavorChanged += OnFlavorChanged;
        App.RemoteFileRequested += OnRemoteFileRequested;

        // Wire editor events
        Editor.TextChanged += Editor_TextChanged;
        Editor.TextArea.TextEntered += Editor_TextEntered;
        Editor.KeyDown += Editor_KeyDown;

        // Ctrl+S anywhere in the window saves
        InputBindings.Add(new KeyBinding(_vm.SaveCommand, new KeyGesture(Key.S, ModifierKeys.Control)));

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
                "MdBrowser",
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
        // Allow our own NavigateToString loads (those come through as about:blank or data:)
        // and local file references (relative images via the <base href>).
        if (string.IsNullOrEmpty(uri)
            || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // External: cancel and hand off to the default browser / mail client.
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
        Preview.NavigateToString(html);
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

        var html = _renderer.Render(text, _currentFile, ThemeManager.Current);
        NavigateToString(html);

        if (_currentFile is not null) UpdateStatusForFile(_currentFile, markdown.Length, truncated);
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
        var issues = MarkdownValidator.Validate(markdown, _currentFile, _validatorPipeline);
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

    private sealed record ThemeOption(CatppuccinFlavor Flavor, string Label);
}
