using System.IO;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One entry of the Recent list. An entry whose file is gone cannot be opened, only removed.</summary>
public sealed class RecentProjectViewModel
{
    public RecentProjectViewModel(string path, bool exists, Func<Task> open, Action remove)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        IsMissing = !exists;
        OpenCommand = new AsyncRelayCommand(_ => open(), _ => exists);
        RemoveCommand = new RelayCommand(_ => remove());
    }

    public string Path { get; }

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public bool IsMissing { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public RelayCommand RemoveCommand { get; }
}
