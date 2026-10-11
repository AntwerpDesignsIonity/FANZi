using System.IO.Compression;
using System.Net;
using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Tests;

public sealed class OpenRgbPackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fanzi-openrgb-tests-" + Guid.NewGuid().ToString("N"));

    public OpenRgbPackageInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string BuildZip(string name, params (string Path, string Content)[] entries)
    {
        string zipPath = Path.Combine(_root, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = zip.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public void Validate_RejectsHtmlRenamedToZip()
    {
        string path = Path.Combine(_root, "OpenRGB_Windows_64.zip");
        // > 1 MB so only the magic check can reject it.
        File.WriteAllText(path, "<!DOCTYPE html><html><body>" + new string('x', 1_200_000) + "</body></html>");

        string? error = OpenRgbPackageInstaller.ValidatePackage(path);

        Assert.NotNull(error);
        Assert.Contains("not a ZIP", error);
    }

    [Fact]
    public void Validate_RejectsTinyFile()
    {
        string path = BuildZip("tiny.zip", ("OpenRGB.exe", "stub"));
        Assert.Contains("too small", OpenRgbPackageInstaller.ValidatePackage(path));
    }

    [Fact]
    public void Validate_AcceptsRealZipHeader()
    {
        string path = BuildZip("ok.zip", ("OpenRGB.exe", "stub"));
        Assert.Null(OpenRgbPackageInstaller.ValidatePackage(path, minBytes: 1));
    }

    [Fact]
    public void Extract_FlattensNestedFolderContainingExe()
    {
        string zip = BuildZip("nested.zip",
            ("OpenRGB Windows 64-bit/OpenRGB.exe", "exe"),
            ("OpenRGB Windows 64-bit/Qt5Core.dll", "dll"),
            ("OpenRGB Windows 64-bit/plugins/platforms/qwindows.dll", "plugin"),
            ("README.txt", "outside the exe folder"));
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "OpenRGB.exe"), "old version");

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(target, "OpenRGB.exe"), result.ExePath);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "OpenRGB.exe"))); // overwritten
        Assert.True(File.Exists(Path.Combine(target, "Qt5Core.dll")));
        Assert.True(File.Exists(Path.Combine(target, "plugins", "platforms", "qwindows.dll")));
        Assert.False(File.Exists(Path.Combine(target, "README.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "OpenRGB Windows 64-bit")));
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public void Extract_ReplacesWholeFolder_StaleFilesFromOldVersionRemoved()
    {
        string zip = BuildZip("new.zip", ("OpenRGB.exe", "new exe"), ("new.dll", "new"));
        string target = Path.Combine(_root, "swap");
        Directory.CreateDirectory(Path.Combine(target, "plugins"));
        File.WriteAllText(Path.Combine(target, "OpenRGB.exe"), "old exe");
        File.WriteAllText(Path.Combine(target, "old-only.dll"), "old");
        File.WriteAllText(Path.Combine(target, "plugins", "old-plugin.dll"), "old");

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        Assert.True(File.Exists(Path.Combine(target, "new.dll")));
        Assert.False(File.Exists(Path.Combine(target, "old-only.dll")));
        Assert.False(Directory.Exists(Path.Combine(target, "plugins")));
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public void Extract_MoveInFails_RestoresPreviousInstallUntouched()
    {
        string zip = BuildZip("new.zip", ("OpenRGB.exe", "new exe"), ("new.dll", "new"));
        string target = Path.Combine(_root, "restore");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "OpenRGB.exe"), "old exe");
        File.WriteAllText(Path.Combine(target, "old.dll"), "old");
        string fullTarget = Path.GetFullPath(target);
        var log = new List<string>();

        // Renaming the old install away works; moving the new folder in fails (disk full / AV lock).
        void Move(string from, string to)
        {
            if (to == fullTarget && from.Contains(".staging-"))
                throw new IOException("simulated failure while moving the new folder in");
            Directory.Move(from, to);
        }

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, target, log.Add, Move);

        Assert.False(result.Success);
        Assert.Contains("simulated failure", result.Error);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        Assert.True(File.Exists(Path.Combine(target, "old.dll")));
        Assert.False(File.Exists(Path.Combine(target, "new.dll")));
        Assert.Contains(log, l => l.Contains("restored previous install"));
        AssertNoStagingOrBackupLeft(target);
    }

    private static string MakeDir(string path, params (string File, string Content)[] files)
    {
        Directory.CreateDirectory(path);
        foreach (var (file, content) in files)
            File.WriteAllText(Path.Combine(path, file), content);
        return path;
    }

    [Fact]
    public void CleanupStale_HealthyTarget_RemovesBackupStagingAndBakFoldersOnly()
    {
        string target = MakeDir(Path.Combine(_root, "OpenRGB"), ("OpenRGB.exe", "current"));
        MakeDir(target + ".backup-1a2b3c4d", ("OpenRGB.exe", "old"));
        MakeDir(target + ".staging-5e6f7a8b", ("partial.dll", "x"));
        MakeDir(target + ".bak", ("OpenRGB.exe", "older"));
        MakeDir(target + ".bak2", ("OpenRGB.exe", "oldest"));
        // Unrelated siblings must survive.
        string other = MakeDir(Path.Combine(_root, "OpenRGB-Plugins"), ("p.dll", "keep"));
        string otherBackup = MakeDir(Path.Combine(_root, "Other.backup-1"), ("x", "keep"));
        var log = new List<string>();

        int removed = OpenRgbPackageInstaller.CleanupStaleInstallFolders(target, log.Add);

        Assert.Equal(4, removed);
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(otherBackup));
        Assert.DoesNotContain(Directory.GetDirectories(_root).Select(Path.GetFileName),
            n => n!.StartsWith("OpenRGB.", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, log.Count(l => l.Contains("removed stale folder")));
    }

    [Fact]
    public void CleanupStale_TargetMissingAfterFailedRestore_RestoresNewestBackupThenCleans()
    {
        string target = Path.Combine(_root, "OpenRGB");
        string older = MakeDir(target + ".backup-00000001", ("OpenRGB.exe", "older"));
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        string newer = MakeDir(target + ".backup-00000002", ("OpenRGB.exe", "newer"), ("Qt5Core.dll", "dll"));
        Directory.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));
        MakeDir(target + ".staging-00000003", ("OpenRGB.exe", "never finished"));
        var log = new List<string>();

        int removed = OpenRgbPackageInstaller.CleanupStaleInstallFolders(target, log.Add);

        Assert.Equal(2, removed); // older backup + staging; the newer backup became the install
        Assert.Equal("newer", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        Assert.True(File.Exists(Path.Combine(target, "Qt5Core.dll")));
        Assert.Contains(log, l => l.Contains("restored previous install"));
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public void CleanupStale_BrokenTargetWithoutExe_ReplacedByBackup()
    {
        string target = MakeDir(Path.Combine(_root, "OpenRGB"), ("half-copied.dll", "x"));
        MakeDir(target + ".backup-abcdef12", ("OpenRGB.exe", "good"));

        OpenRgbPackageInstaller.CleanupStaleInstallFolders(target);

        Assert.Equal("good", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        Assert.False(File.Exists(Path.Combine(target, "half-copied.dll")));
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public void CleanupStale_RestoreFails_KeepsEveryBackup()
    {
        string target = Path.Combine(_root, "OpenRGB");
        string backup = MakeDir(target + ".backup-abcdef12", ("OpenRGB.exe", "only copy"));
        MakeDir(target + ".staging-12345678", ("x.dll", "x"));
        var log = new List<string>();

        int removed = OpenRgbPackageInstaller.CleanupStaleInstallFolders(target, log.Add,
            moveDirectory: (_, _) => throw new IOException("simulated: folder in use"));

        Assert.Equal(0, removed);
        Assert.Equal("only copy", File.ReadAllText(Path.Combine(backup, "OpenRGB.exe")));
        Assert.Contains(log, l => l.Contains("could not restore"));
    }

    [Fact]
    public void CleanupStale_NothingToDo_ReturnsZero()
    {
        string target = MakeDir(Path.Combine(_root, "OpenRGB"), ("OpenRGB.exe", "current"));
        Assert.Equal(0, OpenRgbPackageInstaller.CleanupStaleInstallFolders(target));
        Assert.Equal(0, OpenRgbPackageInstaller.CleanupStaleInstallFolders(Path.Combine(_root, "missing-parent", "OpenRGB")));
        Assert.True(File.Exists(Path.Combine(target, "OpenRGB.exe")));
    }

    [Fact]
    public void Extract_Success_RemovesBackupLeftByEarlierFailedRestore()
    {
        string zip = BuildZip("new.zip", ("OpenRGB.exe", "new exe"));
        string target = MakeDir(Path.Combine(_root, "OpenRGB"), ("OpenRGB.exe", "old exe"));
        MakeDir(target + ".backup-deadbeef", ("OpenRGB.exe", "stale"));
        MakeDir(target + ".bak", ("OpenRGB.exe", "stale"));

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        AssertNoStagingOrBackupLeft(target);
        Assert.False(Directory.Exists(target + ".bak"));
    }

    [Fact]
    public void Extract_TargetInUse_FailsWithoutTouchingIt()
    {
        string zip = BuildZip("new.zip", ("OpenRGB.exe", "new exe"));
        string target = Path.Combine(_root, "locked");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "OpenRGB.exe"), "old exe");
        string fullTarget = Path.GetFullPath(target);

        void Move(string from, string to)
        {
            if (from == fullTarget) throw new IOException("The process cannot access the file because it is being used by another process.");
            Directory.Move(from, to);
        }

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, target, moveDirectory: Move);

        Assert.False(result.Success);
        Assert.Contains("is OpenRGB running", result.Error);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "OpenRGB.exe")));
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public async Task Install_HappyPath_ReportsSuccessExeAndSource()
    {
        byte[] zip = BuildPackage();
        var requested = new List<Uri>();
        using var http = new HttpClient(new StubHandler(req =>
        {
            requested.Add(req.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        }));
        string target = Path.Combine(_root, "happy");

        var result = await OpenRgbPackageInstaller.InstallAsync(
            new[] { new Uri("https://a.example/OpenRGB_1.0_Windows_64.zip") }, target, http: http);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(Path.GetFullPath(target), "OpenRGB.exe"), result.ExePath);
        Assert.True(File.Exists(result.ExePath));
        Assert.Equal(new Uri("https://a.example/OpenRGB_1.0_Windows_64.zip"), result.SourceUrl);
        Assert.Single(requested);
        AssertNoStagingOrBackupLeft(target);
    }

    [Fact]
    public async Task Install_PlainHttpUrl_IsDownloadedOverHttpsOnly()
    {
        byte[] zip = BuildPackage();
        var requested = new List<Uri>();
        using var http = new HttpClient(new StubHandler(req =>
        {
            requested.Add(req.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        }));

        var result = await OpenRgbPackageInstaller.InstallAsync(
            new[] { new Uri("http://a.example:8080/custom-port.zip"), new Uri("http://a.example/OpenRGB.zip") },
            Path.Combine(_root, "https-only"), http: http);

        Assert.True(result.Success, result.Error);
        Assert.All(requested, u => Assert.Equal(Uri.UriSchemeHttps, u.Scheme));
        Assert.Equal("https://a.example/OpenRGB.zip", Assert.Single(requested).AbsoluteUri); // custom-port http refused
        Assert.Equal(Uri.UriSchemeHttps, result.SourceUrl!.Scheme);
    }

    [Fact]
    public async Task Install_NonHttpsOnly_FailsWithoutAnyRequest()
    {
        int requests = 0;
        using var http = new HttpClient(new StubHandler(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.OK); }));

        var result = await OpenRgbPackageInstaller.InstallAsync(
            new[] { new Uri("ftp://a.example/OpenRGB.zip") }, Path.Combine(_root, "ftp"), http: http);

        Assert.False(result.Success);
        Assert.Contains("only https", result.Error);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Install_StalledDownload_ReportsStall()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) }));

        var result = await OpenRgbPackageInstaller.InstallCoreAsync(
            new[] { new Uri("https://a.example/OpenRGB.zip") }, Path.Combine(_root, "stall"),
            null, null, CancellationToken.None, http,
            overallTimeout: TimeSpan.FromSeconds(30), stallTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(result.Success);
        Assert.Contains("stalled", result.Error);
    }

    [Fact]
    public async Task Install_OverallTimeout_CoversAllMirrors()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) }));

        var result = await OpenRgbPackageInstaller.InstallCoreAsync(
            new[] { new Uri("https://a.example/1.zip"), new Uri("https://b.example/2.zip") },
            Path.Combine(_root, "budget"), null, null, CancellationToken.None, http,
            overallTimeout: TimeSpan.FromMilliseconds(300), stallTimeout: TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Contains("a.example/1.zip: timed out (overall install time limit", result.Error);
        Assert.Contains("b.example/2.zip: skipped — overall time limit", result.Error);
    }

    /// <summary>A real &gt;1 MB package (stored, incompressible) with OpenRGB.exe in a nested folder.</summary>
    private static byte[] BuildPackage()
    {
        var payload = new byte[1_300_000];
        new Random(42).NextBytes(payload);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exe = zip.CreateEntry("OpenRGB Windows 64-bit/OpenRGB.exe", CompressionLevel.NoCompression);
            using (var s = exe.Open()) s.Write(payload);
            var dll = zip.CreateEntry("OpenRGB Windows 64-bit/Qt5Core.dll", CompressionLevel.NoCompression);
            using (var s = dll.Open()) s.Write(payload.AsSpan(0, 1000));
        }
        return ms.ToArray();
    }

    private static void AssertNoStagingOrBackupLeft(string target)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string parent = Path.GetDirectoryName(full)!;
        string name = Path.GetFileName(full);
        Assert.Empty(Directory.GetDirectories(parent, name + ".staging-*"));
        Assert.Empty(Directory.GetDirectories(parent, name + ".backup-*"));
    }

    /// <summary>Response body that never produces data until cancelled.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public void Extract_MissingExe_ReturnsClearError()
    {
        string zip = BuildZip("noexe.zip", ("docs/readme.txt", "nothing here"));
        var log = new List<string>();

        var result = OpenRgbPackageInstaller.ExtractAndInstall(zip, Path.Combine(_root, "target2"), log.Add);

        Assert.False(result.Success);
        Assert.Null(result.ExePath);
        Assert.Contains("OpenRGB.exe not found", result.Error);
        Assert.Contains(log, l => l.Contains("OpenRGB.exe not found"));
    }

    [Fact]
    public async Task Install_AllMirrorsFail_ReportsEachReason()
    {
        var html = new string('x', 2_000_000);
        using var http = new HttpClient(new StubHandler(req => req.RequestUri!.Host == "a.example"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + html) }));

        var result = await OpenRgbPackageInstaller.InstallAsync(
            new[] { new Uri("https://a.example/OpenRGB.zip"), new Uri("https://b.example/OpenRGB.zip") },
            Path.Combine(_root, "target3"), http: http);

        Assert.False(result.Success);
        Assert.StartsWith("All 2 sources failed", result.Error);
        Assert.Contains("a.example/OpenRGB.zip: HTTP 404", result.Error);
        Assert.Contains("b.example/OpenRGB.zip: not a ZIP", result.Error);
    }
}
