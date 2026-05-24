using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Fanzi.Installer;

public partial class InstallerWindow : Window
{
    private static readonly string DefaultInstallPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FANZI");

    public InstallerWindow()
    {
        InitializeComponent();

        InstallPathBox.Text = DefaultInstallPath;
        InstallButton.Click += OnInstallClicked;
        CancelButton.Click += (_, _) => Close();
        BrowseButton.Click += OnBrowseClicked;
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

        OptionsPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            await Task.Run(() => RunInstallation(installPath, desktopShortcut, startMenu, startup));

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
                    string exePath = Path.Combine(installPath, "Fanzi.FanControl.exe");
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

    private void RunInstallation(string installPath, bool desktopShortcut, bool startMenu, bool startup)
    {
        UpdateProgress(5, "Creating installation directory...");
        Directory.CreateDirectory(installPath);

        UpdateProgress(15, "Extracting application files...");
        string? sourceDir = FindSourceFiles();
        if (sourceDir is null)
        {
            throw new InvalidOperationException(
                "Could not locate FANZI application files. Ensure the publish output is bundled with the installer.");
        }

        UpdateProgress(30, "Copying files...");
        CopyDirectory(sourceDir, installPath);

        UpdateProgress(60, "Registering application...");
        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsApp(installPath, desktopShortcut, startMenu, startup);
        }

        UpdateProgress(85, "Creating uninstaller...");
        CreateUninstaller(installPath);

        UpdateProgress(100, "Installation complete!");
    }

    private static string? FindSourceFiles()
    {
        string? assemblyDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(assemblyDir)) return null;

        string appFilesDir = Path.Combine(assemblyDir, "app");
        if (Directory.Exists(appFilesDir))
            return appFilesDir;

        string parentAppDir = Path.Combine(Path.GetDirectoryName(assemblyDir) ?? "", "app");
        if (Directory.Exists(parentAppDir))
            return parentAppDir;

        string publishDir = Path.Combine(assemblyDir, "..", "..", "publish", "win-x64");
        if (Directory.Exists(publishDir))
            return Path.GetFullPath(publishDir);

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsApp(string installPath, bool desktopShortcut, bool startMenu, bool startup)
    {
        string exePath = Path.Combine(installPath, "Fanzi.FanControl.exe");

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
            CreateShortcut(exePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FANZI.lnk"));

        if (startMenu)
        {
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "FANZI");
            Directory.CreateDirectory(startMenuDir);
            CreateShortcut(exePath, Path.Combine(startMenuDir, "FANZI.lnk"));
        }

        if (startup)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            runKey?.SetValue("FANZI", $"\"{exePath}\" --minimized");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string targetPath, string shortcutPath)
    {
        string script = $"""
            $ws = New-Object -ComObject WScript.Shell
            $s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')
            $s.TargetPath = '{targetPath.Replace("'", "''")}'
            $s.WorkingDirectory = '{Path.GetDirectoryName(targetPath)?.Replace("'", "''")}'
            $s.Description = 'FANZI — AI-Powered Fan Control'
            $s.Save()
            """;

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"{script.Replace("\"", "\\\"")}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        })?.WaitForExit(5000);
    }

    private static void CreateUninstaller(string installPath)
    {
        string uninstallBat = Path.Combine(installPath, "uninstall.bat");
        string content = $"""
            @echo off
            echo Uninstalling FANZI...
            reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "FANZI" /f >nul 2>&1
            reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FANZI" /f >nul 2>&1
            del "%USERPROFILE%\Desktop\FANZI.lnk" >nul 2>&1
            rmdir /s /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\FANZI" >nul 2>&1
            echo FANZI has been uninstalled. You can delete this folder manually.
            echo Install location: {installPath}
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
        System.Threading.Thread.Sleep(200);
    }
}
