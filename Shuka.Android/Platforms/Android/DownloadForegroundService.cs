using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
#pragma warning disable CS8602 // AndroidX nullability annotations are overly conservative

namespace Shuka.Android.Platforms.Android;

/// <summary>
/// A foreground service that keeps the download task alive when the app is
/// backgrounded or the screen turns off.
/// </summary>
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class DownloadForegroundService : Service
{
    private const string ChannelId        = "shuka_download_channel";
    private const string DoneChannelId    = "shuka_done_channel";
    private const int    NotificationId   = 1001;

    // Track active notification state for live updates
    private static int    _currentTotal   = 0;
    private static int    _currentChapter = 0;
    private static string _currentTitle   = "Downloading novel\u2026";

    public static void Start()
    {
        var ctx = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(DownloadForegroundService));
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            ctx.StartForegroundService(intent);
        else
            ctx.StartService(intent);
#pragma warning restore CA1416
    }

    public static void Stop()
    {
        var ctx = global::Android.App.Application.Context;
        ctx.StopService(new Intent(ctx, typeof(DownloadForegroundService)));
        _currentTotal   = 0;
        _currentChapter = 0;
        _currentTitle   = "Downloading novel\u2026";
    }

    /// <summary>
    /// Updates the ongoing download notification with live chapter progress.
    /// Call this from the download progress callback. Throttled by the OS (max ~1/s).
    /// </summary>
    public static void UpdateProgress(string title, int current, int total)
    {
        _currentTitle   = title;
        _currentChapter = current;
        _currentTotal   = total;

        var ctx = global::Android.App.Application.Context;
        EnsureProgressChannel(ctx);

        var launchIntent = ctx.PackageManager
            ?.GetLaunchIntentForPackage(ctx.PackageName ?? "")
            ?.SetFlags(ActivityFlags.SingleTop)
            ?? new Intent(ctx, typeof(DownloadForegroundService));
        launchIntent.PutExtra("navigate_to", "DownloadsPage");

#pragma warning disable CA1416
        var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pendingIntent = PendingIntent.GetActivity(ctx, 0, launchIntent, pendingFlags);

        string contentText = total > 0
            ? $"Chapter {current} of {total}"
            : "Preparing\u2026";

        var builder = new NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle(title)
            .SetContentText(contentText)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(pendingIntent)
            .SetPriority(NotificationCompat.PriorityLow);

        if (total > 0)
        {
            builder.SetProgress(total, current, false);
        }
        else
        {
            builder.SetProgress(0, 0, true); // indeterminate
        }

        try
        {
            var mgr = NotificationManagerCompat.From(ctx);
            mgr?.Notify(NotificationId, builder.Build()!);
        }
        catch { }
    }

    /// <summary>
    /// Post a "download complete" heads-up notification with an "Open" action.
    /// Safe to call from any thread.
    /// </summary>
    public static void NotifyDone(string title, string epubPath)
    {
        var ctx = global::Android.App.Application.Context;
        EnsureDoneChannel(ctx);

        // Main tap action: open the app
        var launchIntent = ctx.PackageManager
            ?.GetLaunchIntentForPackage(ctx.PackageName ?? "")
            ?.SetFlags(ActivityFlags.SingleTop)
            ?? new Intent(ctx, typeof(DownloadForegroundService));
        launchIntent.PutExtra("navigate_to", "DownloadsPage");

#pragma warning disable CA1416
        var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var launchPendingIntent = PendingIntent.GetActivity(
            ctx, title.GetHashCode(), launchIntent, pendingFlags);

        // "Open" action: open the EPUB file
        Intent? openIntent = null;
        PendingIntent? openPendingIntent = null;

        try
        {
            // Check if it's a content URI (SAF) or file path
            if (epubPath.StartsWith("content://"))
            {
                var uri = global::Android.Net.Uri.Parse(epubPath);
                openIntent = new Intent(Intent.ActionView);
                openIntent.SetDataAndType(uri, "application/epub+zip");
                openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            }
            else if (System.IO.File.Exists(epubPath))
            {
                var file = new Java.IO.File(epubPath);
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    ctx, "com.seizue.shuka.fileprovider", file);
                openIntent = new Intent(Intent.ActionView);
                openIntent.SetDataAndType(uri, "application/epub+zip");
                openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            }

            if (openIntent != null)
            {
                openPendingIntent = PendingIntent.GetActivity(
                    ctx, title.GetHashCode() + 1, openIntent, pendingFlags);
            }
        }
        catch
        {
            // If we can't create the open intent, just skip the action button
        }

        var builder = new NotificationCompat.Builder(ctx, DoneChannelId)
            .SetContentTitle("Download complete")
            .SetContentText(title)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
            .SetAutoCancel(true)
            .SetContentIntent(launchPendingIntent)
            .SetPriority(NotificationCompat.PriorityDefault);

        // Add "Open" action button if we successfully created the intent
        if (openPendingIntent != null)
        {
            builder.AddAction(
                global::Android.Resource.Drawable.IcMenuView,
                "Open",
                openPendingIntent);
        }

        var notification = builder.Build()!;

        var mgr = NotificationManagerCompat.From(ctx);
        // Use a unique ID per title so multiple completions don't collapse into one
        mgr?.Notify(Math.Abs(title.GetHashCode() % 9000) + 2000, notification);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        var notification = BuildNotification();
#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);
#pragma warning restore CA1416

        // NotSticky — don't restart the service if the app is killed.
        // The service is started explicitly by DownloadManager when a download begins.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
#pragma warning disable CA1416, CA1422
        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            StopForeground(StopForegroundFlags.Remove);
        else
            StopForeground(true);
#pragma warning restore CA1416, CA1422
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var ctx = global::Android.App.Application.Context;

        var launchIntent = ctx.PackageManager
            ?.GetLaunchIntentForPackage(ctx.PackageName ?? "")
            ?.SetFlags(ActivityFlags.SingleTop)
            ?? new Intent(ctx, typeof(DownloadForegroundService));
        launchIntent.PutExtra("navigate_to", "DownloadsPage");

#pragma warning disable CA1416
        var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pendingIntent = PendingIntent.GetActivity(ctx, 0, launchIntent, pendingFlags);

        string contentText = _currentTotal > 0
            ? $"Chapter {_currentChapter} of {_currentTotal}"
            : "Preparing\u2026";

        var builder = new NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle(_currentTitle)
            .SetContentText(contentText)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(pendingIntent)
            .SetPriority(NotificationCompat.PriorityLow);

        if (_currentTotal > 0)
            builder.SetProgress(_currentTotal, _currentChapter, false);
        else
            builder.SetProgress(0, 0, true);

        return builder.Build()!;
    }

    private void CreateNotificationChannel() => EnsureProgressChannel(this);

    private static void EnsureProgressChannel(global::Android.Content.Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

#pragma warning disable CA1416
        var mgr = (NotificationManager?)ctx.GetSystemService(NotificationService);
        if (mgr?.GetNotificationChannel(ChannelId) != null) return;

        var channel = new NotificationChannel(
            ChannelId,
            "Downloads",
            NotificationImportance.Low)
        {
            Description = "Shuka novel download progress"
        };
        mgr?.CreateNotificationChannel(channel);
#pragma warning restore CA1416
    }

    private static void EnsureDoneChannel(global::Android.Content.Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

#pragma warning disable CA1416
        var mgr = (NotificationManager?)ctx.GetSystemService(NotificationService);
        if (mgr?.GetNotificationChannel(DoneChannelId) != null) return;

        var channel = new NotificationChannel(
            DoneChannelId,
            "Download complete",
            NotificationImportance.Default)
        {
            Description = "Notifies when a novel EPUB has finished downloading"
        };
        mgr?.CreateNotificationChannel(channel);
#pragma warning restore CA1416
    }
}
