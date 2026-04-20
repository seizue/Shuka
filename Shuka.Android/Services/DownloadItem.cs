using System.ComponentModel;

namespace Shuka.Android.Services;

public enum DownloadStatus
{
    Queued,
    Running,
    Done,
    Cancelled,
    Failed
}

/// <summary>
/// Represents a single novel download job.
/// </summary>
public class DownloadItem : INotifyPropertyChanged
{
    private string _statusText  = "Queued";
    private double _progress    = 0;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _epubPath;
    private string _logText = "";

    public Guid   Id       { get; } = Guid.NewGuid();
    public string Url      { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public int    Chapters { get; init; }

    // Resolved after GatherBookInfo
    public string Title  { get; set; } = "Loading...";
    public string Author { get; set; } = "";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressPct)); }
    }

    public string ProgressPct => $"{(int)(_progress * 100)}%";

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsCancelled));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusIcon));
        }
    }

    public string? EpubPath
    {
        get => _epubPath;
        set { _epubPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasEpub)); }
    }

    public string LogText
    {
        get => _logText;
        set { _logText = value; OnPropertyChanged(); }
    }

    public bool IsRunning   => Status == DownloadStatus.Running || Status == DownloadStatus.Queued;
    public bool IsDone      => Status == DownloadStatus.Done;
    public bool IsFailed    => Status == DownloadStatus.Failed;
    public bool IsCancelled => Status == DownloadStatus.Cancelled;
    public bool IsFinished  => Status is DownloadStatus.Done or DownloadStatus.Cancelled or DownloadStatus.Failed;
    public bool HasEpub     => Status == DownloadStatus.Done && !string.IsNullOrEmpty(EpubPath);

    public Color StatusColor => Status switch
    {
        DownloadStatus.Done      => Color.FromArgb("#30D158"),
        DownloadStatus.Failed    => Color.FromArgb("#FF453A"),
        DownloadStatus.Cancelled => Color.FromArgb("#FFD60A"),
        DownloadStatus.Running   => Color.FromArgb("#8B5E5F"),
        _                        => Color.FromArgb("#636366")
    };

    public string StatusIcon => Status switch
    {
        DownloadStatus.Done      => "\uE876", // check
        DownloadStatus.Failed    => "\uE5CD", // close
        DownloadStatus.Cancelled => "\uE5C9", // cancel
        DownloadStatus.Running   => "\uE2C4", // downloading
        _                        => "\uE8B6"  // schedule
    };

    public CancellationTokenSource Cts { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
