using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PhotoScope.Services;

namespace PhotoScope.Views;

public partial class ImportDialog
{
    readonly AppSettings _settings;
    readonly ObservableCollection<string> _sources = new();

    public ImportOptions? Result { get; private set; }

    public ImportDialog(AppSettings settings, IReadOnlyList<string>? initialPaths)
    {
        InitializeComponent();
        _settings = settings;
        SourceList.ItemsSource = _sources;
        _sources.CollectionChanged += (_, _) => UpdateState();

        RecursiveBox.IsChecked = settings.ImportRecursive;
        SkipDuplicatesBox.IsChecked = settings.SkipDuplicates;
        ReferenceRadio.IsChecked = settings.DefaultImportMode == ImportMode.ReferenceInPlace;
        CopyRadio.IsChecked = settings.DefaultImportMode == ImportMode.CopyToLibrary;
        CopyHint.Text = $"Copies go to {settings.LibraryFolder}, sorted into Year\\Month folders. Originals are left untouched. (Change the folder in Settings.)";

        if (initialPaths != null) AddSources(initialPaths);
        UpdateState();

        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
        };
    }

    void AddSources(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (!_sources.Contains(p, StringComparer.OrdinalIgnoreCase)) _sources.Add(p);
        }
    }

    void UpdateState()
    {
        EmptyHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = _sources.Count > 0;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", ImageDecoder.Extensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Choose photos to import",
            Multiselect = true,
            Filter = $"Images|{exts}|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FileNames);
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to import", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FolderNames);
    }

    void OnRemoveSource(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) _sources.Remove(path);
    }

    void OnSourceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SourceList.SelectedItem is string path)
        {
            _sources.Remove(path);
            e.Handled = true;
        }
    }

    void OnImport(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0) return;
        var mode = CopyRadio.IsChecked == true ? ImportMode.CopyToLibrary : ImportMode.ReferenceInPlace;

        if (mode == ImportMode.CopyToLibrary)
        {
            try
            {
                var folder = System.IO.Path.GetFullPath(_settings.LibraryFolder);
                System.IO.Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                ErrorText.Text = "The library folder can't be used: " + Friendly.Describe(ex) + " Choose another one in Settings.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        _settings.ImportRecursive = RecursiveBox.IsChecked == true;
        _settings.SkipDuplicates = SkipDuplicatesBox.IsChecked == true;
        Result = new ImportOptions
        {
            Sources = _sources.ToList(),
            Recursive = RecursiveBox.IsChecked == true,
            Mode = mode,
            LibraryFolder = _settings.LibraryFolder,
            SkipDuplicates = SkipDuplicatesBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
