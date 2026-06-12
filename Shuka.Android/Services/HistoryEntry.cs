namespace Shuka.Android.Services;

/// <summary>
/// A persisted record of a completed download.
/// Stored in {AppDataDirectory}/history.json.
/// </summary>
public class HistoryEntry
{
    public Guid     Id             { get; init; } = Guid.NewGuid();
    public string   Title          { get; set; }  = "";
    public string   Author         { get; set; }  = "";
    public string   Url            { get; init; } = "";
    public string?  EpubPath       { get; set; }
    public string?  CoverLocalPath { get; set; }  // cached local image path
    public string?  CoverUrl       { get; init; } // original remote URL
    public int      ChapterCount   { get; set; }
    public DateTime CompletedAt    { get; init; } = DateTime.Now;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFileAvailable { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCoverAvailable { get; set; }
}
