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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    // ── OpenRGB download mirrors (same fallback chain as OpenRgbServerManager) ─
    private static readonly string[] OpenRgbDownloadUrls =
    [
        // GitLab CI (OpenRGB official build server)
        "https://gitlab.com/CalcProgrammer1/OpenRGB/-/jobs/artifacts/master/download?job=Windows+64",
        // Release 0.9 static build
        "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_6b1df76.zip",
    ];

    // ── Microsoft VC++ Redistributable (required by LibreHardwareMonitor kernel driver) ─
    private const string VcRedistUrl =
        "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public InstallerWindow()
    {
        InitializeComponent();

        InstallPathBox.Text = DefaultInstallPath;
        InstallButton.Click += OnInstallClicked;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowseClicked;

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
        bool installVcRedist = InstallVcRedistCheck.IsChecked == true;

        OptionsPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            await Task.Run(async () => await RunInstallationAsync(
                installPath, desktopShortcut, startMenu, startup,
                installOpenRgb, installVcRedist));

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

    private async Task RunInstallationAsync(
        string installPath, bool desktopShortcut, bool startMenu,
        bool startup, bool installOpenRgb, bool installVcRedist)
    {
        // ── Step 0: Pre-flight dependency checks ──────────────────────────────
        UpdateProgress(2, "Checking system dependencies...");
        await CheckDependenciesAsync();

        // ── Step 1: VC++ Redistributable ───────────────────────────────────────
        if (installVcRedist)
        {
            await InstallVcRedistributableAsync();
        }

        // ── Step 2: Extract FANZI ──────────────────────────────────────────────
        UpdateProgress(15, "Creating installation directory...");
        Directory.CreateDirectory(installPath);

        UpdateProgress(20, "Extracting FANZI from installer...");

        if (!TryExtractEmbeddedFanzi(installPath))
        {
            UpdateProgress(25, "Embedded payload not found, looking for app folder...");
            string? sourceDir = FindSourceFiles();
            if (sourceDir is null)
            {
                throw new InvalidOperationException(
                    "Could not locate FANZI application files. " +
                    "Make sure the installer was built with the embedded payload or an 'app' folder is next to it.");
            }
            UpdateProgress(30, "Copying FANZI files...");
            CopyDirectory(sourceDir, installPath);
        }

        UpdateProgress(45, "FANZI extracted successfully");

        // ── Step 3: Create branded launcher ────────────────────────────────────
        UpdateProgress(48, "Creating Run_Ionity launcher...");
        CreateRunIonityLauncher(installPath);

        // ── Step 4: OpenRGB ────────────────────────────────────────────────────
        if (installOpenRgb)
        {
            await InstallOpenRgbWithFallbackAsync(installPath);
        }

        // ── Step 5: Register with Windows ──────────────────────────────────────
        UpdateProgress(80, "Registering application...");
        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsApp(installPath, desktopShortcut, startMenu, startup);
        }

        // ── Step 6: Uninstaller ────────────────────────────────────────────────
        UpdateProgress(92, "Creating uninstaller...");
        CreateUninstaller(installPath);

        UpdateProgress(100, "Installation complete!");
    }

    // ── Pre-flight dependency checks ──────────────────────────────────────────

    private async Task CheckDependenciesAsync()
    {
        // Check if VC++ Redistributable is already installed
        bool vcRedistInstalled = IsVcRedistInstalled();

        Dispatcher.UIThread.Post(() =>
        {
            if (vcRedistInstalled)
            {
                InstallVcRedistCheck.IsChecked = false;
            }
        });

        // Check if OpenRGB is already installed somewhere
        bool openRgbFound = IsOpenRgbAlreadyInstalled();

        Dispatcher.UIThread.Post(() =>
        {
            if (openRgbFound)
            {
                InstallOpenRgbCheck.IsChecked = false;
            }
        });

        await Task.Delay(200); // Brief pause for UI update
    }

    private static bool IsVcRedistInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (key is null) return false;
            var installed = key.GetValue("Installed");
            return installed is int v && v == 1;
        }
        catch { return false; }
    }

    private static bool IsOpenRgbAlreadyInstalled()
    {
        // Check common locations
        string[] searchPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRGB"),
        ];

        foreach (var p in searchPaths)
        {
            if (File.Exists(Path.Combine(p, "OpenRGB.exe")))
                return true;
        }

        // Also check PATH
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (path is not null)
            {
                foreach (var dir in path.Split(';'))
                {
                    if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "OpenRGB.exe")))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    // ── VC++ Redistributable ──────────────────────────────────────────────────

    private async Task InstallVcRedistributableAsync()
    {
        if (IsVcRedistInstalled())
        {
            UpdateProgress(10, "Visual C++ runtime already installed — skipping");
            return;
        }

        UpdateProgress(8, "Downloading Visual C++ runtime...");

        string tempPath = Path.Combine(Path.GetTempPath(), "vc_redist.x64.exe");
        try
        {
            using var response = await Http.GetAsync(VcRedistUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fs = File.Create(tempPath);
            await stream.CopyToAsync(fs);
        }
        catch (Exception ex)
        {
            UpdateProgress(12, $"VC++ download failed ({ex.Message.Split('\n')[0]}) — continuing");
            await Task.Delay(500);
            return;
        }

        UpdateProgress(12, "Installing Visual C++ runtime (this may take a moment)...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0 && proc.ExitCode != 3010) // 3010 = reboot required
                {
                    UpdateProgress(13, $"VC++ installer exited with code {proc.ExitCode} — continuing");
                    await Task.Delay(500);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateProgress(13, $"VC++ install failed ({ex.Message.Split('\n')[0]}) — continuing");
            await Task.Delay(500);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ── OpenRGB (with mirror fallback) ────────────────────────────────────────

    private async Task InstallOpenRgbWithFallbackAsync(string installPath)
    {
        string openRgbDir = Path.Combine(installPath, "OpenRGB");
        Directory.CreateDirectory(openRgbDir);

        string zipPath = Path.Combine(openRgbDir, "OpenRGB.zip");

        // Check if already downloaded and extracted
        if (File.Exists(Path.Combine(openRgbDir, "OpenRGB.exe")))
        {
            UpdateProgress(55, "OpenRGB already installed in FANZI folder — skipping download");
            await Task.Delay(300);
            return;
        }

        // Try each mirror until one succeeds
        Exception? lastError = null;
        foreach (var url in OpenRgbDownloadUrls)
        {
            try
            {
                UpdateProgress(55, $"Downloading OpenRGB from {new Uri(url).Host}...");

                if (File.Exists(zipPath)) File.Delete(zipPath);

                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(zipPath);
                await stream.CopyToAsync(fileStream);

                // Success — extract
                UpdateProgress(68, "Extracting OpenRGB...");
                ZipFile.ExtractToDirectory(zipPath, openRgbDir, overwriteFiles: true);
                File.Delete(zipPath);

                // Flatten subdirectory if present
                FlattenOpenRgbSubdir(openRgbDir);

                UpdateProgress(75, "OpenRGB installed");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                UpdateProgress(60, $"Mirror failed, trying next...");
                await Task.Delay(500);
            }
        }

        // All mirrors failed — non-fatal, warn the user
        UpdateProgress(75, $"OpenRGB download failed — RGB control requires manual install");
        await Task.Delay(1000);
    }

    private static void FlattenOpenRgbSubdir(string openRgbDir)
    {
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
    }

    // ── Embedded FANZI extraction ─────────────────────────────────────────────

    private static bool TryExtractEmbeddedFanzi(string installPath)
    {
        var asm = typeof(InstallerWindow).Assembly;
        using var stream = asm.GetManifestResourceStream("Fanzi.FanControl.exe");
        if (stream is null) return false;

        Directory.CreateDirectory(installPath);
        string outPath = Path.Combine(installPath, "Fanzi.FanControl.exe");

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

    // ── Windows registration ──────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsApp(string installPath, bool desktopShortcut, bool startMenu, bool startup)
    {
        string exePath = Path.Combine(installPath, "Fanzi.FanControl.exe");
        string launcherPath = Path.Combine(installPath, "Run_Ionity.exe");
        string targetPath = File.Exists(launcherPath) ? launcherPath : exePath;

        using var uninstallKey = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI");
        uninstallKey.SetValue("DisplayName", "FANZI — AI-Powered Fan Control");
        uninstallKey.SetValue("DisplayVersion", "2.1.0");
        uninstallKey.SetValue("Publisher", "Ionity Global Pty Ltd");
        uninstallKey.SetValue("InstallLocation", installPath);
        uninstallKey.SetValue("DisplayIcon", exePath);
        uninstallKey.SetValue("UninstallString", $"\"{Path.Combine(installPath, "uninstall.bat")}\"");
        uninstallKey.SetValue("URLInfoAbout", "https://www.ionity.today");
        uninstallKey.SetValue("NoModify", 1);
        uninstallKey.SetValue("NoRepair", 1);

        if (desktopShortcut)
        {
            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            CreateShortcut(exePath, Path.Combine(userDesktop, "FANZI.lnk"), iconPath: exePath);
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
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

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
            try
            {
                string urlPath = Path.ChangeExtension(shortcutPath, ".url");
                File.WriteAllText(urlPath,
                    $"[InternetShortcut]\r\nURL=file:///{targetPath.Replace('\\', '/')}\r\nIconFile={targetPath}\r\nIconIndex=0\r\n");
            }
            catch { }
        }
    }

    // ── Launcher creation ─────────────────────────────────────────────────────

    private static void CreateRunIonityLauncher(string installPath)
    {
        string batPath = Path.Combine(installPath, "Run_Ionity.bat");
        string content = $"""
            @echo off
            title Run_Ionity — Launching FANZI
            echo ===========================================
            echo  FANZI by Ionity Global Pty Ltd
            echo  AI@IONITY.TODAY
            echo ===========================================
            echo Starting FANZI with hardware access...
            cd /d "{installPath}"
            start "" "Fanzi.FanControl.exe" %*
            exit
            """;
        File.WriteAllText(batPath, content);

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
            echo ========================================
            echo  FANZI Uninstaller
            echo  Ionity Global Pty Ltd
            echo ========================================
            echo.
            echo Uninstalling FANZI...
            taskkill /F /IM "Fanzi.FanControl.exe" >nul 2>&1
            taskkill /F /IM "OpenRGB.exe" >nul 2>&1
            reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "FANZI" /f >nul 2>&1
            reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI" /f >nul 2>&1
            del "%USERPROFILE%\Desktop\FANZI.lnk" >nul 2>&1
            del "%PUBLIC%\Desktop\FANZI.lnk" >nul 2>&1
            rmdir /s /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\FANZI" >nul 2>&1
            timeout /t 1 >nul
            echo.
            echo FANZI has been uninstalled.
            echo Install folder: {installPath}
            echo You can delete this folder manually.
            echo.
            pause
            """;
        File.WriteAllText(uninstallBat, content);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        Thread.Sleep(120);
    }
}
