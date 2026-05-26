using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.Installer;

public partial class InstallerWindow : Window
{
    private static readonly string DefaultInstallPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FANZI");

    private static readonly HttpClient Http = new();

    public InstallerWindow()
    {
        InitializeComponent();

        InstallPathBox.Text = DefaultInstallPath;
        InstallButton.Click += OnInstallClicked;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowseClicked;

        // Wire titlebar window controls (custom chrome - native min/close removed)
        if (this.FindControl<Border>("TitleBar") is { } titleBar)
        {
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }
        if (this.FindControl<Button>("MinimizeButton") is { } minBtn)
            minBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        if (this.FindControl<Button>("CloseButton") is { } closeBtn)
            closeBtn.Click += (_, _) => Close();
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Install Location",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            InstallPathBox.Text = folders[0].Path.LocalPath;
        }
    }

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        string installPath = InstallPathBox.Text?.Trim() ?? DefaultInstallPath;
        bool desktopShortcut = DesktopShortcutCheck.IsChecked == true;
        bool startMenu = StartMenuCheck.IsChecked == true;
        bool startup = StartupCheck.IsChecked == true;
        bool launchAfter = LaunchAfterCheck.IsChecked == true;
        bool installOpenRgb = InstallOpenRgbCheck.IsChecked == true;

        OptionsPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            await Task.Run(async () => await RunInstallationAsync(installPath, desktopShortcut, startMenu, startup, installOpenRgb));

            ProgressPanel.IsVisible = false;
            CompletePanel.IsVisible = true;
            InstallLocationLabel.Text = installPath;
            InstallButton.Content = "Close";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= OnInstallClicked;
            InstallButton.Click += (_, _) =>
            {
                if (launchAfter)
                {
                    string launcherPath = Path.Combine(installPath, "Run_Ionity.exe");
                    string exePath = File.Exists(launcherPath)
                        ? launcherPath
                        : Path.Combine(installPath, "Fanzi.FanControl.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true,
                            Verb = "runas"
                        });
                    }
                }
                Close();
            };
        }
        catch (Exception ex)
        {
            ProgressTitle.Text = "Installation Failed";
            ProgressStatus.Text = ex.Message;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
    }

    private async Task RunInstallationAsync(string installPath, bool desktopShortcut, bool startMenu, bool startup, bool installOpenRgb)
    {
        UpdateProgress(5, "Creating installation directory...");
        Directory.CreateDirectory(installPath);

        UpdateProgress(10, "Extracting FANZI from installer...");

        // Try embedded resource first (true single-file installer)
        if (!TryExtractEmbeddedFanzi(installPath))
        {
            UpdateProgress(15, "Embedded payload not found, looking for app folder...");
            string? sourceDir = FindSourceFiles();
            if (sourceDir is null)
            {
                throw new InvalidOperationException(
                    "Could not locate FANZI application. Installer EXE has no embedded payload AND no 'app' folder is next to it.");
            }
            UpdateProgress(25, "Copying FANZI files...");
            CopyDirectory(sourceDir, installPath);
        }
        else
        {
            UpdateProgress(40, "FANZI extracted successfully");
        }

        UpdateProgress(45, "Creating Run_Ionity launcher...");
        CreateRunIonityLauncher(installPath);

        if (installOpenRgb)
        {
            UpdateProgress(55, "Downloading OpenRGB...");
            try
            {
                await InstallOpenRgbAsync(installPath);
            }
            catch (Exception ex)
            {
                // Non-fatal: continue install even if OpenRGB download fails
                UpdateProgress(70, $"OpenRGB skipped ({ex.Message.Split('\n')[0]})");
                Thread.Sleep(800);
            }
        }

        UpdateProgress(80, "Registering application...");
        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsApp(installPath, desktopShortcut, startMenu, startup);
        }

        UpdateProgress(92, "Creating uninstaller...");
        CreateUninstaller(installPath);

        UpdateProgress(100, "Installation complete!");
    }

    /// <summary>
    /// Extracts the embedded Fanzi.FanControl.exe resource (embedded at build time
    /// via &lt;EmbeddedResource&gt; in the installer csproj). Returns false if no
    /// payload was embedded — caller should fall back to disk-side app folder.
    /// </summary>
    private static bool TryExtractEmbeddedFanzi(string installPath)
    {
        var asm = typeof(InstallerWindow).Assembly;
        // Embedded with LogicalName "Fanzi.FanControl.exe"
        using var stream = asm.GetManifestResourceStream("Fanzi.FanControl.exe");
        if (stream is null) return false;

        Directory.CreateDirectory(installPath);
        string outPath = Path.Combine(installPath, "Fanzi.FanControl.exe");

        // Kill any running FANZI before overwriting
        try
        {
            foreach (var p in Process.GetProcessesByName("Fanzi.FanControl"))
            {
                try { p.Kill(true); p.WaitForExit(3000); } catch { }
            }
        }
        catch { }

        using var fs = File.Create(outPath);
        stream.CopyTo(fs);
        return true;
    }

    private static string? FindSourceFiles()
    {
        string? assemblyDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(assemblyDir)) return null;

        // Look for 'app' folder alongside installer
        foreach (var candidate in new[]
        {
            Path.Combine(assemblyDir, "app"),
            Path.Combine(Path.GetDirectoryName(assemblyDir) ?? "", "app"),
            Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "publish")),
            Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "publish")),
        })
        {
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Fanzi.FanControl.exe")))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task InstallOpenRgbAsync(string installPath)
    {
        string openRgbDir = Path.Combine(installPath, "OpenRGB");
        Directory.CreateDirectory(openRgbDir);

        string zipPath = Path.Combine(openRgbDir, "OpenRGB.zip");
        const string downloadUrl = "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_6b1df76.zip";

        UpdateProgress(60, "Downloading OpenRGB (~8 MB)...");
        using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(zipPath);
            await stream.CopyToAsync(fileStream);
        }

        UpdateProgress(70, "Extracting OpenRGB...");
        ZipFile.ExtractToDirectory(zipPath, openRgbDir, overwriteFiles: true);
        File.Delete(zipPath);

        // Flatten the OpenRGB_0.9_Windows_64 subfolder if present
        var subdirs = Directory.GetDirectories(openRgbDir);
        foreach (var sd in subdirs)
        {
            if (Path.GetFileName(sd).StartsWith("OpenRGB", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in Directory.GetFiles(sd))
                    File.Move(f, Path.Combine(openRgbDir, Path.GetFileName(f)), overwrite: true);
                foreach (var d in Directory.GetDirectories(sd))
                {
                    var dest = Path.Combine(openRgbDir, Path.GetFileName(d));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(d, dest);
                }
                Directory.Delete(sd, true);
            }
        }

        UpdateProgress(75, "OpenRGB installed");
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsApp(string installPath, bool desktopShortcut, bool startMenu, bool startup)
    {
        string exePath = Path.Combine(installPath, "Fanzi.FanControl.exe");
        string launcherPath = Path.Combine(installPath, "Run_Ionity.exe");
        string targetPath = File.Exists(launcherPath) ? launcherPath : exePath;

        using var uninstallKey = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI");
        uninstallKey.SetValue("DisplayName", "FANZI — AI-Powered Fan Control");
        uninstallKey.SetValue("DisplayVersion", "2.0.0");
        uninstallKey.SetValue("Publisher", "Antwerp Designs — Johan Wilhelm van Antwerp");
        uninstallKey.SetValue("InstallLocation", installPath);
        uninstallKey.SetValue("DisplayIcon", exePath);
        uninstallKey.SetValue("UninstallString", $"\"{Path.Combine(installPath, "uninstall.bat")}\"");
        uninstallKey.SetValue("URLInfoAbout", "https://www.ionity.today");
        uninstallKey.SetValue("NoModify", 1);
        uninstallKey.SetValue("NoRepair", 1);

        if (desktopShortcut)
        {
            // Use the per-user Desktop folder so a non-elevated user still sees the icon
            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            CreateShortcut(exePath, Path.Combine(userDesktop, "FANZI.lnk"), iconPath: exePath);
            // Also put one on the All Users desktop so other accounts on this PC see it
            try { CreateShortcut(exePath, Path.Combine(publicDesktop, "FANZI.lnk"), iconPath: exePath); } catch { }
        }

        if (startMenu)
        {
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "FANZI");
            Directory.CreateDirectory(startMenuDir);
            CreateShortcut(exePath, Path.Combine(startMenuDir, "FANZI.lnk"), iconPath: exePath);
            CreateShortcut(Path.Combine(installPath, "uninstall.bat"),
                Path.Combine(startMenuDir, "Uninstall FANZI.lnk"), iconPath: exePath);
        }

        if (startup)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            runKey?.SetValue("FANZI", $"\"{targetPath}\" --minimized");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string targetPath, string shortcutPath, string? iconPath = null)
    {
        // Use WScript.Shell COM directly — far more reliable than shelling out to powershell
        // and works even if PowerShell is locked down by policy.
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            // Ensure parent dir exists
            var parent = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
            shortcut.Description = "FANZI — AI-Powered Fan Control & RGB Lighting";
            shortcut.IconLocation = (iconPath ?? targetPath) + ",0";
            shortcut.Save();
        }
        catch
        {
            // Last-resort fallback: write a .url file (Explorer handles it the same way)
            try
            {
                string urlPath = Path.ChangeExtension(shortcutPath, ".url");
                File.WriteAllText(urlPath,
                    $"[InternetShortcut]\r\nURL=file:///{targetPath.Replace('\\', '/')}\r\nIconFile={targetPath}\r\nIconIndex=0\r\n");
            }
            catch { }
        }
    }

    private static void CreateRunIonityLauncher(string installPath)
    {
        // Run_Ionity.bat - a simple branded launcher that elevates and launches FANZI
        string batPath = Path.Combine(installPath, "Run_Ionity.bat");
        string content = $"""
            @echo off
            title Run_Ionity — Launching FANZI
            echo ===========================================
            echo  FANZI by Antwerp Designs · AI@IONITY.TODAY
            echo ===========================================
            echo Starting FANZI with hardware access...
            cd /d "{installPath}"
            start "" "Fanzi.FanControl.exe" %*
            exit
            """;
        File.WriteAllText(batPath, content);

        // Also create a launcher .vbs that runs silently (no console flash)
        string vbsPath = Path.Combine(installPath, "Run_Ionity.vbs");
        string fanziExe = Path.Combine(installPath, "Fanzi.FanControl.exe").Replace("\\", "\\\\");
        string vbsContent =
            "Set WshShell = CreateObject(\"WScript.Shell\")" + Environment.NewLine +
            "WshShell.CurrentDirectory = \"" + installPath.Replace("\\", "\\\\") + "\"" + Environment.NewLine +
            "WshShell.Run \"\"\"" + fanziExe + "\"\"\", 1, False" + Environment.NewLine;
        File.WriteAllText(vbsPath, vbsContent);
    }

    private static void CreateUninstaller(string installPath)
    {
        string uninstallBat = Path.Combine(installPath, "uninstall.bat");
        string content = $"""
            @echo off
            echo Uninstalling FANZI...
            taskkill /F /IM "Fanzi.FanControl.exe" >nul 2>&1
            taskkill /F /IM "OpenRGB.exe" >nul 2>&1
            reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "FANZI" /f >nul 2>&1
            reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI" /f >nul 2>&1
            del "%USERPROFILE%\Desktop\FANZI.lnk" >nul 2>&1
            rmdir /s /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\FANZI" >nul 2>&1
            timeout /t 1 >nul
            echo FANZI has been uninstalled.
            echo Install folder: {installPath}
            echo You can delete this folder manually.
            pause
            """;
        File.WriteAllText(uninstallBat, content);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            string destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private void UpdateProgress(int percent, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = percent;
            ProgressStatus.Text = status;
        });
        System.Threading.Thread.Sleep(120);
    }
}
