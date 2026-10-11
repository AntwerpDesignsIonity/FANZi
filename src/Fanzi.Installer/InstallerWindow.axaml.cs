using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fanzi.FanControl.Services.OpenRgb;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Fanzi.Installer;

public partial class InstallerWindow : Window
{
    private static readonly string DefaultInstallPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FANZI");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Installer/app version from the csproj &lt;Version&gt; (e.g. "2.2.0").</summary>
    public static string AppVersion { get; } =
        typeof(InstallerWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string VersionBanner { get; } = $"Version {AppVersion} — © 2018-2026 Ionity Global Pty Ltd";

    private const string OpenRgbDownloadPageUrl = "https://openrgb.org/releases.html";

    // ── Microsoft VC++ Redistributable (required by LibreHardwareMonitor kernel driver) ─
    private const string VcRedistUrl =
        "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>Result of the OpenRGB step (null = not attempted / skipped).</summary>
    private OpenRgbInstallResult? _openRgbResult;

    /// <summary>Non-fatal problems shown as an amber warning on the completion page.</summary>
    private readonly List<string> _warnings = new();

    /// <summary>Install log, written to &lt;installPath&gt;\install.log at the end.</summary>
    private readonly List<string> _log = new();
    private readonly object _logLock = new();

    public InstallerWindow()
    {
        InitializeComponent();

        InstallPathBox.Text = DefaultInstallPath;
        InstallButton.Click += OnInstallClicked;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowseClicked;
        OpenRgbDownloadButton.Click += (_, _) => OpenUrl(OpenRgbDownloadPageUrl);

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
            ShowCompletionWarnings(installPath);
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
            Log($"Installation failed: {ex}");
            ProgressTitle.Text = "Installation Failed";
            string[] warnings;
            lock (_logLock) warnings = _warnings.ToArray();
            ProgressStatus.Text = warnings.Length == 0
                ? ex.Message
                : ex.Message + Environment.NewLine + Environment.NewLine
                  + "Also: " + string.Join(Environment.NewLine + Environment.NewLine, warnings);
            ProgressStatus.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
        finally
        {
            WriteInstallLog(installPath);
        }
    }

    /// <summary>
    /// Shows every recorded non-fatal problem (OpenRGB, VC++) as an amber warning on the
    /// completion page, plus a download-page button when OpenRGB failed.
    /// </summary>
    private void ShowCompletionWarnings(string installPath)
    {
        bool openRgbFailed = _openRgbResult is { Success: false };
        if (_warnings.Count == 0) return;

        CompleteTitle.Text = "Installation Complete (with warnings)";
        CompleteSubtitle.Text = "FANZI is installed, but some optional components need attention.";
        WarningText.Text = string.Join(Environment.NewLine + Environment.NewLine, _warnings)
            + Environment.NewLine + Environment.NewLine
            + $"Details: {Path.Combine(installPath, "install.log")}";
        OpenRgbDownloadButton.IsVisible = openRgbFailed;
        WarningPanel.IsVisible = true;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser — the URL is shown in the warning text */ }
    }

    private void Log(string message)
    {
        lock (_logLock)
            _log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
    }

    private void WriteInstallLog(string installPath)
    {
        try
        {
            if (!Directory.Exists(installPath)) return;
            string[] lines;
            lock (_logLock) lines = _log.ToArray();
            File.WriteAllLines(Path.Combine(installPath, "install.log"), lines);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void AddWarning(string warning)
    {
        lock (_logLock) _warnings.Add(warning);
        Log($"WARNING: {warning}");
    }

    private async Task RunInstallationAsync(
        string installPath, bool desktopShortcut, bool startMenu,
        bool startup, bool installOpenRgb, bool installVcRedist)
    {
        Log($"FANZI installer {AppVersion} — target {installPath}; openRgb={installOpenRgb}, vcRedist={installVcRedist}, startup={startup}");

        // ── Step 0: Pre-flight dependency checks — results decide which steps run ──
        UpdateProgress(2, "Checking system dependencies...");
        bool vcRedistInstalled = IsVcRedistInstalled();
        string? existingOpenRgb = IsOpenRgbAlreadyInstalled(installPath);
        Log($"Pre-flight: VC++ runtime installed={vcRedistInstalled}; OpenRGB={existingOpenRgb ?? "not found"}");

        // ── Step 1: VC++ Redistributable ───────────────────────────────────────
        if (installVcRedist && vcRedistInstalled)
        {
            UpdateProgress(10, "Visual C++ runtime already installed — skipping");
        }
        else if (installVcRedist)
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
        if (installOpenRgb && existingOpenRgb is not null)
        {
            UpdateProgress(75, $"OpenRGB already installed ({Path.GetDirectoryName(existingOpenRgb)}) — skipping download");
        }
        else if (installOpenRgb)
        {
            await InstallOpenRgbAsync(installPath);
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

    /// <summary>
    /// Returns the path of an existing OpenRGB.exe, or null. Checks &lt;installPath&gt;\OpenRGB,
    /// %LOCALAPPDATA%\FANZI\OpenRGB (where the app downloads it), Program Files (x86/x64),
    /// %LOCALAPPDATA%\Programs\OpenRGB, %LOCALAPPDATA%\OpenRGB, the Uninstall / App Paths
    /// registry keys and PATH — the same locations FANZI itself searches.
    /// </summary>
    private static string? IsOpenRgbAlreadyInstalled(string installPath)
    {
        try
        {
            return OpenRgbInstallLocator.FindExisting(new[]
            {
                Path.Combine(installPath, "OpenRGB", OpenRgbInstallLocator.ExeName),
            });
        }
        catch
        {
            return null;
        }
    }

    // ── VC++ Redistributable ──────────────────────────────────────────────────

    private async Task InstallVcRedistributableAsync()
    {
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
            string reason = ex.Message.Split('\n')[0];
            UpdateProgress(12, $"VC++ download failed ({reason}) — continuing");
            AddWarning($"Visual C++ runtime could not be downloaded ({reason}). Hardware sensors may not work — install it from {VcRedistUrl}.");
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
            if (proc is null)
            {
                AddWarning($"Visual C++ runtime installer could not be started. Install it from {VcRedistUrl}.");
            }
            else
            {
                await proc.WaitForExitAsync();
                Log($"VC++ installer exit code {proc.ExitCode}");
                // 0 = OK, 3010 = reboot required, 1638 = newer version already installed
                if (proc.ExitCode != 0 && proc.ExitCode != 3010 && proc.ExitCode != 1638)
                {
                    UpdateProgress(13, $"VC++ installer exited with code {proc.ExitCode} — continuing");
                    AddWarning($"Visual C++ runtime installer exited with code {proc.ExitCode}. Hardware sensors may not work — install it from {VcRedistUrl}.");
                }
            }
        }
        catch (Exception ex)
        {
            string reason = ex.Message.Split('\n')[0];
            UpdateProgress(13, $"VC++ install failed ({reason}) — continuing");
            AddWarning($"Visual C++ runtime install failed ({reason}). Install it from {VcRedistUrl}.");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ── OpenRGB (shared resolver + validated package installer) ───────────────

    private async Task InstallOpenRgbAsync(string installPath)
    {
        string openRgbDir = Path.Combine(installPath, "OpenRGB");
        UpdateProgress(55, "Looking up the latest OpenRGB release...");

        try
        {
            _openRgbResult = await OpenRgbPackageInstaller.InstallLatestAsync(
                openRgbDir,
                new InlineProgress(msg => UpdateProgress(62, msg)),
                Log);
        }
        catch (Exception ex)
        {
            _openRgbResult = OpenRgbInstallResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        if (_openRgbResult.Success)
        {
            Log($"OpenRGB installed: {_openRgbResult.ExePath} from {_openRgbResult.SourceUrl}");
            UpdateProgress(75, "OpenRGB installed");
            return;
        }

        // Non-fatal: FANZI still works and will retry from its RGB tab.
        UpdateProgress(75, "OpenRGB download failed — see the warning when installation finishes");
        AddWarning(
            $"OpenRGB could not be installed: {_openRgbResult.Error}" + Environment.NewLine +
            $"RGB lighting needs OpenRGB. Download the Windows portable zip from {OpenRgbDownloadPageUrl} " +
            "or use Retry / Locate OpenRGB.exe… on FANZI's RGB tab.");
    }

    /// <summary>IProgress that invokes the callback synchronously (keeps messages in order).</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
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
        uninstallKey.SetValue("DisplayVersion", AppVersion);
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
        Log(status);
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = percent;
            ProgressStatus.Text = status;
        });
    }
}
