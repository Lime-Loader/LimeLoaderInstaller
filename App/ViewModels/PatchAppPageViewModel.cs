using MelonLoader.Installer.App.Utils;
using MelonLoader.Installer.Core;
using System.IO.Compression;
using System.Windows.Input;

namespace MelonLoader.Installer.App.ViewModels;

public class PatchAppPageViewModel : BindableObject
{
    public static UnityApplicationFinder.Data CurrentAppData
    {
        get => _currentAppData ?? _dummyAppData;
        set
        {
            if (PatchRunner.IsPatching)
            {
                PopupHelper.Toast("Currently patching, cannot change apps.").Wait();
                return;
            }

            _currentAppData = value;
        }
    }

    private static readonly UnityApplicationFinder.Data _dummyAppData = new("No app selected.", "Please choose one from the apps tab.", UnityApplicationFinder.Status.Unpatched, UnityApplicationFinder.Source.None, [], null);
    private static UnityApplicationFinder.Data? _currentAppData;

    public ICommand PatchTappedCommand { get; }
    public ICommand CustomPatchCommand { get; }
    public ICommand CustomMelonDataCommand { get; }
    public ICommand PatchLocalTappedCommand { get; }
    public ICommand RestoreTappedCommand { get; }

    public PatchAppPageViewModel()
    {
        PatchTappedCommand = new Command(PatchWithoutLocalDeps);
        CustomPatchCommand = new Command(SelectCustomPatch);
        CustomMelonDataCommand = new Command(SelectCustomMelonData);
        PatchLocalTappedCommand = new Command(SelectLocalDepsWithPatch);
        RestoreTappedCommand = new Command(RestoreUnpatchedAPK);
    }

    private async void PatchWithoutLocalDeps()
    {
        await DoPatch();
    }

    private async void SelectCustomPatch()
    {
        FilePickerFileType dllType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, [ "*/*" ] },
            { DevicePlatform.WinUI, [ ".dll" ] }
        });

        var result = await FilePicker.Default.PickAsync(new PickOptions() { FileTypes = dllType });
        
        if (result == null)
        {
            await PopupHelper.Toast("No file selected.");
            return;
        }

        if (!result.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            await PopupHelper.Toast("Please select a .dll file.");
            return;
        }

        string patchPath = result.FullPath;

        if (!File.Exists(patchPath))
        {
            await PopupHelper.Toast("Selected file does not exist.");
            return;
        }

        if (PatchRunner.IsPatching)
        {
            await PopupHelper.Toast("Already patching or restoring, you cannot work on multiple apps at once.");
            return;
        }

        bool loadResult = Plugin.LoadPlugin(patchPath, out bool added);

        if (!loadResult)
        {
            await PopupHelper.Toast("Selected file is not a valid LimeLoader plugin.");
            return;
        }

        await PopupHelper.Toast((added ? "Added " : "Removed ") + "selected plugin.");
    }

    private async void SelectCustomMelonData()
    {
        if (PatchRunner.IsPatching)
        {
            await PopupHelper.Toast("Already patching or restoring, you cannot work on multiple apps at once.");
            return;
        }

        if (_currentAppData == null)
        {
            await PopupHelper.Toast("No app selected.", CommunityToolkit.Maui.Core.ToastDuration.Short);
            return;
        }

        FilePickerFileType zipType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, [ "application/zip", "application/octet-stream", "*/*" ] },
            { DevicePlatform.WinUI, [ ".zip" ] }
        });

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions() { FileTypes = zipType, PickerTitle = "Select a melon_data.zip" });
            if (result == null)
                return;

            if (!result.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await PopupHelper.Toast("Please select a .zip file.");
                return;
            }

            // Sanity check that it actually looks like a melon_data package before committing to a patch.
            try
            {
                using FileStream zipStream = new(result.FullPath, FileMode.Open);
                using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

                bool looksValid = archive.Entries.Any(a => a.FullName.Replace('\\', '/').Contains("MelonLoader/"));
                if (!looksValid)
                {
                    await PopupHelper.Toast("Selected zip does not contain a MelonLoader folder and cannot be used.");
                    return;
                }
            }
            catch (Exception ex)
            {
                await PopupHelper.Toast("Selected zip is invalid.");
                System.Diagnostics.Debug.WriteLine(ex);
                return;
            }

            await DoPatch(customMelonDataPath: result.FullPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void SelectLocalDepsWithPatch()
    {
        FilePickerFileType zipType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, [ "application/zip" ] },
            { DevicePlatform.WinUI, [ ".zip" ] }
        });

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions() { FileTypes = zipType });
            if (result != null)
            {
                try
                {
                    using FileStream apkStream = new(result.FullPath, FileMode.Open);
                    using ZipArchive archive = new(apkStream, ZipArchiveMode.Read);

                    bool hasArm64Dir = archive.Entries.Any(a => a.FullName.Contains("arm64-v8a"));

                    if (!hasArm64Dir)
                    {
                        await PopupHelper.Toast("Selected zip does not contain ARM64 libraries and cannot be used.");
                        return;
                    }

                    var unityLib = archive.GetEntry("Libs/arm64-v8a/libunity.so") ?? archive.GetEntry("arm64-v8a/libunity.so");

                    if (unityLib == null)
                    {
                        await PopupHelper.Toast("Selected zip does not contain libunity.so and cannot be used.");
                        return;
                    }

                    await DoPatch(result.FullPath);
                }
                catch (Exception ex)
                {
                    await PopupHelper.Toast("Selected zip is invalid.");

                    System.Diagnostics.Debug.WriteLine(ex);

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static async Task DoPatch(string? localDepsPath = null, string? customMelonDataPath = null)
    {
        if (PatchRunner.IsPatching)
        {
            await PopupHelper.Toast("Already patching or restoring, you cannot work on multiple apps at once.");
            return;
        }

        if (_currentAppData == null)
        {
            await PopupHelper.Toast("No app selected.", CommunityToolkit.Maui.Core.ToastDuration.Short);
            return;
        }

        await PatchRunner.Begin(CurrentAppData, localDepsPath, customMelonDataPath);
    }

    private async void RestoreUnpatchedAPK()
    {
        await PatchRunner.BeginRestore(CurrentAppData);
    }
}