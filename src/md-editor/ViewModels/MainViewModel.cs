using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MdEditor.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdown", ".mkdn", ".mkd" };

    private FileNodeViewModel _root = new(string.Empty, isDirectory: true);
    private string _currentFolderPath = "No folder open. Click 'Open folder…' to begin.";
    private string _statusText = "Ready";
    private bool _markdownOnly = true;
    private bool _isDirty;
    private string _issuesSummary = "No file open";

    public MainViewModel()
    {
        OpenFolderCommand = new RelayCommand(_ => RequestOpenFolder?.Invoke());
        NewFileCommand = new RelayCommand(_ => RequestNewFile?.Invoke());
        RefreshCommand = new RelayCommand(_ => RequestRefresh?.Invoke(), _ => !string.IsNullOrEmpty(RootPath));
        SaveCommand = new RelayCommand(_ => RequestSave?.Invoke(), _ => IsDirty);
        ToggleExplorerCommand = new RelayCommand(_ => ExplorerVisible = !ExplorerVisible);
    }

    public ICommand OpenFolderCommand { get; }
    public ICommand NewFileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ToggleExplorerCommand { get; }

    public Action? RequestOpenFolder { get; set; }
    public Action? RequestNewFile { get; set; }
    public Action? RequestRefresh { get; set; }
    public Action? RequestSave { get; set; }

    public bool IsDirty
    {
        get => _isDirty;
        set { if (_isDirty == value) return; _isDirty = value; OnPropertyChanged(); }
    }

    public string IssuesSummary
    {
        get => _issuesSummary;
        set { _issuesSummary = value; OnPropertyChanged(); }
    }

    public FileNodeViewModel Root
    {
        get => _root;
        set { _root = value; OnPropertyChanged(); }
    }

    public string? RootPath { get; private set; }

    public string CurrentFolderPath
    {
        get => _currentFolderPath;
        set { _currentFolderPath = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool MarkdownOnly
    {
        get => _markdownOnly;
        set
        {
            if (_markdownOnly == value) return;
            _markdownOnly = value;
            OnPropertyChanged();
            if (!string.IsNullOrEmpty(RootPath))
            {
                LoadRoot(RootPath);
            }
        }
    }

    private bool _explorerVisible = true;
    public bool ExplorerVisible
    {
        get => _explorerVisible;
        set
        {
            if (_explorerVisible == value) return;
            _explorerVisible = value;
            OnPropertyChanged();
            Services.LayoutSettings.SaveExplorerVisible(value);
        }
    }

    public Func<string, bool> CurrentFileFilter =>
        path => !MarkdownOnly || IsMarkdownFile(path);

    public static bool IsMarkdownFile(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in MarkdownExtensions)
        {
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void LoadRoot(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            StatusText = $"Folder not found: {folderPath}";
            return;
        }
        RootPath = folderPath;
        CurrentFolderPath = folderPath;
        var newRoot = new FileNodeViewModel(folderPath, isDirectory: true, CurrentFileFilter);
        newRoot.LoadChildren();
        newRoot.IsExpanded = true;
        Root = newRoot;
        StatusText = $"Opened {folderPath}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
