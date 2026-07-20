#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result;
#endif

using MelonLoader.Installer.Core;

namespace MelonLoader.Installer.App.Utils;

public class APKInstaller
{
    public static APKInstaller Current { get; private set; }

    private UnityApplicationFinder.Data _data;

    private IPatchLogger _logger;
    
    private string? _apkDirectory;

    private Func<Task>? _next;
    private bool _successful = false;

#if ANDROID
    private int _installLoopCount;
    private int _uninstallLoopCount;

    private TaskCompletionSource<bool>? _packageChangeCompletionSource;
    private PackageChangeReceiver? _packageChangeReceiver;
    private Android.App.Activity? _receiverActivity;

    public void SetPackageChangeCompletion(bool success) => _packageChangeCompletionSource?.TrySetResult(success);

    // Register/unregister are kept symmetric and defensive: a leaked BroadcastReceiver pins the
    // Activity ("Activity has leaked IntentReceiver") and leaves the installer unusable until it is
    // force-stopped. We unregister from the exact Activity we registered on, and never throw.
    private void RegisterPackageReceiver(string action)
    {
        UnregisterPackageReceiver();

        _packageChangeCompletionSource = new TaskCompletionSource<bool>();
        _packageChangeReceiver = new PackageChangeReceiver(_packageChangeCompletionSource, _data.PackageName);

        IntentFilter filter = new(action);
        filter.AddDataScheme("package");

        _receiverActivity = Platform.CurrentActivity;
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
            _receiverActivity!.RegisterReceiver(_packageChangeReceiver, filter, ReceiverFlags.NotExported);
        else
            _receiverActivity!.RegisterReceiver(_packageChangeReceiver, filter);
    }

    private void UnregisterPackageReceiver()
    {
        if (_packageChangeReceiver == null)
            return;

        try
        {
            (_receiverActivity ?? Platform.CurrentActivity)?.UnregisterReceiver(_packageChangeReceiver);
        }
        catch (Exception e)
        {
            // Already unregistered / activity gone. Nothing to do but avoid propagating.
            System.Diagnostics.Debug.WriteLine($"Failed to unregister package receiver: {e}");
        }

        _packageChangeReceiver = null;
        _receiverActivity = null;
    }
#endif

    public APKInstaller(UnityApplicationFinder.Data data, IPatchLogger logger)
    {
        Current = this;

        _data = data;
        _logger = logger;

#if ANDROID
        _installLoopCount = 0;
        _uninstallLoopCount = 0;
#endif
    }

    public async Task<bool> Install(string apkDirectory)
    {
        _apkDirectory = apkDirectory;


#if ANDROID
        _next = CheckPackageInstalled;
        await CheckPackageUninstalled();
#else
        _next = InternalInstall;
        await UninstallPackage();
#endif

        return _successful;
    }

    private async Task InternalInstall()
    {
        _logger.Log("Installing, please wait...");

        string[] apks = Directory.GetFiles(_apkDirectory!, "*.apk");

#if ANDROID
        PackageInstaller packageInstaller = Platform.CurrentActivity!.PackageManager!.PackageInstaller;
        try
        {
            PackageInstaller.SessionParams param = new(PackageInstallMode.FullInstall);
            param.SetInstallReason(PackageInstallReason.User);

            int sessionId = packageInstaller.CreateSession(param);
            PackageInstaller.Session session = packageInstaller.OpenSession(sessionId);

            for (int i = 0; i < apks.Length; i++)
            {
                string apk = apks[i];
                using FileStream apkStream = new(apk, FileMode.Open);
                using Stream outStream = session.OpenWrite($"{i + 1}.apk", 0, apkStream.Length);
                apkStream.CopyTo(outStream);
                await outStream.FlushAsync();
                session.Fsync(outStream);
            }

            RegisterPackageReceiver(Intent.ActionPackageAdded);

            Intent callbackIntent = new(Platform.CurrentActivity!, typeof(Platforms.Android.PackageInstallerService));
            PendingIntent pending = PendingIntent.GetService(Platform.CurrentActivity!, 0, callbackIntent, (PendingIntentFlags)33554432)!; // 33554432 is PendingIntentFlags.Mutable but maui doesn't provide it
            session.Commit(pending.IntentSender);

            bool success = await _packageChangeCompletionSource!.Task;
        }
        catch (Exception e)
        {
            _logger.Log($"Failed to install APK(s)\n{e}");
            _successful = false;
            return;
        }
        finally
        {
            UnregisterPackageReceiver();
        }
#else
        if (apks.Length > 1)
            await ADBManager.InstallMultipleAPKs(apks.First(a => a.EndsWith("base.apk")), apks.Where(a => !a.EndsWith("base.apk")).ToArray());
        else
            await ADBManager.InstallAPK(apks[0]);
#endif

        _successful = true;
    }

    private async Task UninstallPackage()
    {
#if ANDROID
        _logger.Log("Uninstalling, please wait...");

        try
        {
            PackageInstaller packageInstaller = Platform.CurrentActivity!.PackageManager!.PackageInstaller;

            Intent callbackIntent = new(Platform.CurrentActivity!, typeof(Platforms.Android.PackageInstallerService));
            PendingIntent pending = PendingIntent.GetService(Platform.CurrentActivity!, 0, callbackIntent, (PendingIntentFlags)33554432)!;

            RegisterPackageReceiver(Intent.ActionPackageRemoved);

            packageInstaller.Uninstall(_data.PackageName, pending.IntentSender);

            bool success = await _packageChangeCompletionSource!.Task;
        }
        catch (Exception e)
        {
            _logger.Log($"Failed to uninstall package\n{e}");
        }
        finally
        {
            UnregisterPackageReceiver();
        }
#else
        await ADBManager.UninstallPackage(_data.PackageName);
        await _next!();
#endif
    }

#if ANDROID
    private async Task CheckPackageUninstalled()
    {
        while (IsPackageInstalled())
        {
            if (_uninstallLoopCount >= 3)
            {
                _successful = false;
                return;
            }

            if (_uninstallLoopCount > 0)
                _logger.Log("Package was not uninstalled, re-attempting");

            _uninstallLoopCount++;
            await UninstallPackage();
        }

        _logger.Log("Uninstall successful");
        await _next!();
    }

    public async Task CheckPackageInstalled()
    {
        while (!IsPackageInstalled())
        {
            if (_installLoopCount >= 3)
            {
                _successful = false;
                return;
            }

            if (_installLoopCount > 0)
                _logger.Log("Package was not installed, re-attempting");

            _installLoopCount++;
            await InternalInstall();
        }

        _logger.Log("Install successful");
    }

    private bool IsPackageInstalled()
    {
        try
        {
            Platform.CurrentActivity!.PackageManager!.GetPackageInfo(_data.PackageName, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public class PackageChangeReceiver(TaskCompletionSource<bool> completionSource, string packageName) : BroadcastReceiver
    {
        private readonly TaskCompletionSource<bool> _completionSource = completionSource;
        private readonly string _packageName = packageName;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent == null)
                return;

            if (intent.Action == Intent.ActionPackageAdded || intent.Action == Intent.ActionPackageRemoved)
            {
                string? modifiedPackage = intent.Data?.EncodedSchemeSpecificPart;
                if (modifiedPackage == _packageName)
                {
                    _completionSource.TrySetResult(true);
                }
            }
        }
    }
#endif
}