using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MdBrowser.ViewModels;

public sealed class FileNodeViewModel : INotifyPropertyChanged
{
    private static readonly FileNodeViewModel DummyChild = new(string.Empty, isDirectory: false, isDummy: true);

    private bool _isExpanded;
    private bool _isSelected;
    private readonly bool _isDummy;
    private readonly Func<string, bool>? _fileFilter;

    public FileNodeViewModel(string path, bool isDirectory, Func<string, bool>? fileFilter = null, bool isDummy = false)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        _isDummy = isDummy;
        _fileFilter = fileFilter;
        Children = new ObservableCollection<FileNodeViewModel>();
        if (isDirectory && !isDummy)
        {
            // Lazy load: insert dummy so the expand chevron shows.
            Children.Add(DummyChild);
        }
    }

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Name => string.IsNullOrEmpty(FullPath) ? string.Empty : Path.GetFileName(FullPath);
    public string Icon => IsDirectory ? "" : ""; // Segoe MDL2: Folder / Page

    public ObservableCollection<FileNodeViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (_isExpanded && Children.Count == 1 && Children[0]._isDummy)
            {
                LoadChildren();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public void LoadChildren()
    {
        Children.Clear();
        if (!IsDirectory) return;

        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue; // skip .git, .vs etc.
                Children.Add(new FileNodeViewModel(d, isDirectory: true, _fileFilter));
            }

            var files = Directory.EnumerateFiles(FullPath)
                .Where(f => _fileFilter?.Invoke(f) ?? true)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                Children.Add(new FileNodeViewModel(f, isDirectory: false, _fileFilter));
            }
        }
        catch (UnauthorizedAccessException) { /* skip restricted folders */ }
        catch (DirectoryNotFoundException) { /* removed during enum */ }
    }

    public void Refresh()
    {
        if (!IsDirectory) return;
        var wasExpanded = _isExpanded;
        Children.Clear();
        Children.Add(DummyChild);
        if (wasExpanded) LoadChildren();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
