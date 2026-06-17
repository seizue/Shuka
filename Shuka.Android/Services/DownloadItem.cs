using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Shuka.Android.Services;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Paused,
    Resuming,
    Failed,
    Completed,
    Cancelled
}

/// <summary>
/// Represents a single novel download job.
/// </summary>
public class DownloadItem : INotifyPropertyChanged
{
    private string _statusText  = "Queued";
    private double _progress    = 0;
    private DownloadStatus _status = DownloadStatus.Pending;
    private string? _epubPath;
    private string _logText = "";
    private int _queuePosition;

    public Guid   Id         { get; set; } = Guid.NewGuid();
    public string Url        { get; set; } = "";
    public string CoverUrl   { get; set; } = "";
    public int    Chapters   { get; set; }
    /// <summary>1-based start chapter. 0 = from the beginning.</summary>
    public int    ChapterFrom { get; set; } = 0;
    public bool   Translate   { get; set; } = true;
    public bool   ForceRebuild { get; set; } = false;
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;

    // Resolved after GatherBookInfo
    public string Title  { get; set; } = "Loading...";
    public string Author { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string OriginalAuthor { get; set; } = "";
    /// <summary>Actual chapter count resolved after GatherBookInfo completes.</summary>
    public int    TotalChapters { get; set; } = 0;

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

    [JsonIgnore]
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

    [JsonIgnore]
    public int QueuePosition
    {
        get => _queuePosition;
        set { _queuePosition = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public bool IsRunning   => Status == DownloadStatus.Downloading || Status == DownloadStatus.Pending || Status == DownloadStatus.Resuming;
    [JsonIgnore]
    public bool IsDone      => Status == DownloadStatus.Completed;
    [JsonIgnore]
    public bool IsFailed    => Status == DownloadStatus.Failed;
    [JsonIgnore]
    public bool IsCancelled => Status == DownloadStatus.Cancelled;
    [JsonIgnore]
    public bool IsFinished  => Status is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Failed;
    [JsonIgnore]
    public bool HasEpub     => Status == DownloadStatus.Completed && !string.IsNullOrEmpty(EpubPath);

    [JsonIgnore]
    public Color StatusColor => Status switch
    {
        DownloadStatus.Completed => Color.FromArgb("#30D158"),
        DownloadStatus.Failed    => Color.FromArgb("#FF453A"),
        DownloadStatus.Cancelled => Color.FromArgb("#FFD60A"),
        DownloadStatus.Downloading => Color.FromArgb("#8B5E5F"),
        DownloadStatus.Resuming => Color.FromArgb("#FF9500"),
        DownloadStatus.Paused => Color.FromArgb("#8E8E93"),
        _                        => Color.FromArgb("#636366") // Pending
    };

    [JsonIgnore]
    public string StatusIcon => Status switch
    {
        DownloadStatus.Completed => "\uE876", // check
        DownloadStatus.Failed    => "\uE5CD", // close
        DownloadStatus.Cancelled => "\uE5C9", // cancel
        DownloadStatus.Downloading => "\uE2C4", // downloading
        DownloadStatus.Resuming => "\uE8BA", // restore/resume
        DownloadStatus.Paused => "\uE034", // pause
        _                        => "\uE8B6"  // schedule (Pending)
    };

    [JsonIgnore]
    public CancellationTokenSource Cts { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
