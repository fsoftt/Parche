using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace Parche.App;

/// <summary>
/// Servicio en primer plano que mantiene vivo el proceso (y la red) mientras hay descargas, con una notificación de
/// progreso. No descarga nada por sí mismo: el trabajo lo hace <c>DownloadService</c>; este solo evita que Android
/// cierre la app en segundo plano.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class DownloadForegroundService : Service
{
    private const string ChannelId = "downloads";
    private const int NotificationId = 4711;
    private const string ExtraTitle = "title";
    private const string ExtraText = "text";
    private const string ExtraPercent = "percent";

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = Build(this, intent?.GetStringExtra(ExtraTitle) ?? "", intent?.GetStringExtra(ExtraText) ?? "",
            intent?.GetIntExtra(ExtraPercent, -1) ?? -1);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception)
        {
            // Android puede negar el primer plano (p. ej. permiso o arranque desde segundo plano): se detiene sin cerrar la app.
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (_wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "parche:downloads");
            _wakeLock?.Acquire();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch (Exception) { }
        _wakeLock = null;
        base.OnDestroy();
    }

    /// <summary>Arranca el servicio o actualiza su notificación. <paramref name="percent"/> &lt; 0 = progreso indeterminado.</summary>
    public static void Show(string title, string text, int percent)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(DownloadForegroundService));
        intent.PutExtra(ExtraTitle, title).PutExtra(ExtraText, text).PutExtra(ExtraPercent, percent);
        try { ContextCompat.StartForegroundService(context, intent); }
        catch (Exception) { /* sin servicio las descargas siguen mientras la app esté abierta */ }
    }

    public static void Hide()
    {
        var context = global::Android.App.Application.Context;
        try { context.StopService(new Intent(context, typeof(DownloadForegroundService))); }
        catch (Exception) { }
    }

    private static Notification Build(Context context, string title, string text, int percent)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && context.GetSystemService(NotificationService) is NotificationManager manager
            && manager.GetNotificationChannel(ChannelId) is null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, title, NotificationImportance.Low));

        var open = PendingIntent.GetActivity(context, 0,
            new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetProgress(100, Math.Max(0, percent), percent < 0)!
            .SetContentIntent(open)!
            .Build()!;
    }
}
