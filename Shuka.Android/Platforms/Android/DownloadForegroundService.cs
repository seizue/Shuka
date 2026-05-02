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
    }

    /// <summary>
    /// Post a "download complete" heads-up notification.
    /// Safe to call from any thread.
    /// </summary>
    public static void NotifyDone(string title)
    {
        var ctx = global::Android.App.Application.Context;
        EnsureDoneChannel(ctx);

        var launchIntent = ctx.PackageManager
            ?.GetLaunchIntentForPackage(ctx.PackageName ?? "")
            ?.SetFlags(ActivityFlags.SingleTop)
            ?? new Intent(ctx, typeof(DownloadForegroundService));

#pragma warning disable CA1416
        var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pendingIntent = PendingIntent.GetActivity(ctx, title.GetHashCode(), launchIntent, pendingFlags);

        var notification = new NotificationCompat.Builder(ctx, DoneChannelId)
            .SetContentTitle("Download complete")
            .SetContentText(title)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
            .SetAutoCancel(true)
            .SetContentIntent(pendingIntent)
            .SetPriority(NotificationCompat.PriorityDefault)
            .Build()!;

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

        return StartCommandResult.Sticky;
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

#pragma warning disable CA1416
        var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
#pragma warning restore CA1416

        var pendingIntent = PendingIntent.GetActivity(ctx, 0, launchIntent, pendingFlags);

        return new NotificationCompat.Builder(ctx, ChannelId)
            .SetContentTitle("Shuka")
            .SetContentText("Downloading novel…")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetContentIntent(pendingIntent)
            .Build()!;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

#pragma warning disable CA1416
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
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
