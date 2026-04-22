using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace Shuka.Android.Platforms.Android;

/// <summary>
/// A foreground service that keeps the download task alive when the app is
/// backgrounded or the screen turns off.
/// </summary>
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class DownloadForegroundService : Service
{
    private const string ChannelId    = "shuka_download_channel";
    private const int    NotificationId = 1001;

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

        var launchIntent = ctx.PackageManager!
            .GetLaunchIntentForPackage(ctx.PackageName!)!
            .SetFlags(ActivityFlags.SingleTop);

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
}
