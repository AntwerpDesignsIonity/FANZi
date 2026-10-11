using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.Tools;

/// <summary>A Windows command-line tool run from the Tools tab (defrag, sfc, DISM, netsh, powercfg, …).</summary>
/// <param name="Title">Shown in the console header and status ("Optimise C:").</param>
/// <param name="FileName">Executable (resolved from PATH / System32).</param>
/// <param name="Arguments">Command line arguments.</param>
/// <param name="NeedsRestart">True when Windows must restart before the change takes effect.</param>
public sealed record ToolCommand(string Title, string FileName, string Arguments, bool NeedsRestart = false)
{
    public override string ToString() => $"{FileName} {Arguments}".Trim();
}

/// <summary>Live progress of a running tool: the latest status line and its percentage when the tool reports one.</summary>
public readonly record struct ToolProgress(string? Line, double? Percent, bool IsProgressLine);

/// <summary>Outcome of a tool run.</summary>
public sealed record ToolRunResult(int ExitCode, bool Succeeded, bool Cancelled, string Output, string? ErrorSummary, TimeSpan Elapsed);

/// <summary>
/// v4.1.0: runs Windows maintenance tools hidden, streams their console output line by line (progress lines that
/// redraw in place with '\r' or backspaces are reported as progress, not logged hundreds of times), detects the
/// output encoding (sfc.exe writes UTF-16 when redirected; the others use the OEM code page) and supports
/// cancellation (kills the process tree). No simulation: everything shown comes from the real tool.
/// </summary>
public sealed class ToolProcessRunner
{
    private static readonly Regex TotalPattern = new(@"Total:\s*(\d{1,3})\s*%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PercentPattern = new(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Error codes / phrases the tools print while still returning exit code 0 (defrag does this on access denied).
    private static readonly Regex ErrorPattern = new(
        @"\(0x8[0-9A-F]{7}\)|insufficient privileges|access is denied|must be an administrator|elevated permissions are required|requires elevation|run as administrator|^Error:?\s*\d+|cannot (start|be found)|is not recognized",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static ToolProcessRunner()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* already registered */ }
    }

    /// <summary>
    /// Percentage from a progress line ("Analysis: 23% complete", "Verification 45% complete.",
    /// "[====  25.0%  ]", "Retrim:  100% complete."); null for anything else — a report line such as
    /// "Total fragmented space = 3%" is not progress.
    /// </summary>
    public static double? ParseProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        // chkdsk: "Progress: 1234 of 50000 done; Stage:  24%; Total:  10%; ETA:   0:00:12" — use the overall total.
        var total = TotalPattern.Match(line);
        if (total.Success && double.TryParse(total.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t is >= 0 and <= 100)
            return t;
        bool looksLikeProgress = line.Contains("complete", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("[=", StringComparison.Ordinal)
                                 || line.TrimStart().StartsWith('[');
        if (!looksLikeProgress) return null;
        var match = PercentPattern.Match(line);
        if (!match.Success) return null;
        string number = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
        return value is >= 0 and <= 100 ? value : null;
    }

    /// <summary>First line that looks like an error (null when none).</summary>
    public static string? FindError(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && ErrorPattern.IsMatch(line)) return line;
        }
        return null;
    }

    /// <summary>True when <paramref name="bytes"/> look like UTF-16LE text (sfc.exe): most odd bytes are zero.</summary>
    public static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;
        int pairs = bytes.Length / 2, zeros = 0;
        for (int i = 1; i < bytes.Length; i += 2)
            if (bytes[i] == 0) zeros++;
        return zeros >= pairs * 0.6;
    }

    /// <summary>OEM code page console tools write in (437 / 850 …); UTF-8 when unavailable.</summary>
    internal static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }

    /// <summary>
    /// Splits console text into lines the way a console shows them: '\n' ends a line, a '\r' not followed by '\n'
    /// means the tool redraws the current line (progress), backspaces erase. Returns completed lines; the
    /// unfinished tail stays in <paramref name="pending"/>. Lines that were overwritten in place are flagged.
    /// </summary>
    public static List<(string Line, bool Overwritten)> SplitConsole(string chunk, StringBuilder pending)
    {
        var lines = new List<(string, bool)>();
        foreach (char c in chunk)
        {
            switch (c)
            {
                case '\n':
                    lines.Add((pending.ToString(), false));
                    pending.Clear();
                    break;
                case '\r':
                    // Hold the CR: if '\n' follows it is a normal line end, otherwise the line is redrawn.
                    pending.Append('\r');
                    break;
                case '\b':
                    if (pending.Length > 0) pending.Length--;
                    break;
                case '\0':
                    break;
                default:
                    if (pending.Length > 0 && pending[^1] == '\r')
                    {
                        pending.Length--;
                        if (pending.Length > 0) lines.Add((pending.ToString(), true));
                        pending.Clear();
                    }
                    pending.Append(c);
                    break;
            }
        }
        // A trailing "\r\n" split across chunks: strip the CR on the line it ends.
        for (int i = 0; i < lines.Count; i++)
            lines[i] = (lines[i].Item1.TrimEnd('\r'), lines[i].Item2);
        return lines;
    }

    /// <summary>Runs <paramref name="command"/>, reporting each line / progress; never throws for tool failures.</summary>
    public async Task<ToolRunResult> RunAsync(ToolCommand command, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var log = new StringBuilder();
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tool '{command}' could not start", ex);
            return new ToolRunResult(-1, false, false, "", $"{command.FileName} could not start: {ex.Message}", stopwatch.Elapsed);
        }
        if (process is null)
            return new ToolRunResult(-1, false, false, "", $"{command.FileName} could not start.", stopwatch.Elapsed);

        AppLog.Info($"Tool started: {command}");
        using (process)
        {
            using var registration = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });

            var gate = new object();
            void Report(string line, bool overwritten)
            {
                var text = line.TrimEnd();
                if (text.Length == 0) return;
                double? percent = ParseProgress(text);
                // In-place redraws are progress only; a finished line (even "100% complete.") is logged once.
                if (!overwritten)
                    lock (gate) log.AppendLine(text);
                progress?.Report(new ToolProgress(text, percent, overwritten || percent is not null));
            }

            var stdout = PumpAsync(process.StandardOutput.BaseStream, Report);
            var stderr = PumpAsync(process.StandardError.BaseStream, Report);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Tool '{command}' output failed", ex);
            }

            bool cancelled = ct.IsCancellationRequested;
            int exitCode = SafeExitCode(process);
            string output;
            lock (gate) output = log.ToString();
            string? error = cancelled ? "Cancelled." : FindError(output);
            bool ok = !cancelled && exitCode == 0 && error is null;
            AppLog.Info($"Tool finished: {command} -> exit {exitCode}{(cancelled ? " (cancelled)" : "")} in {stopwatch.Elapsed.TotalSeconds:F1} s");
            return new ToolRunResult(exitCode, ok, cancelled, output, ok ? null : error ?? $"Exit code {exitCode}.", stopwatch.Elapsed);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    /// <summary>Reads raw bytes, detects UTF-16 vs OEM on the first chunk, and reports console lines.</summary>
    private static async Task PumpAsync(Stream stream, Action<string, bool> report)
    {
        var buffer = new byte[4096];
        Decoder? decoder = null;
        var pending = new StringBuilder();
        var chars = new char[8192];
        // The encoding is chosen once enough bytes are seen (a first read can be just a few bytes).
        var head = new MemoryStream();

        void Decode(byte[] bytes, int count, bool flush)
        {
            if (decoder is null)
            {
                head.Write(bytes, 0, count);
                if (head.Length < 32 && !flush) return;
                var all = head.ToArray();
                decoder = (LooksLikeUtf16(all) ? Encoding.Unicode : OemEncoding()).GetDecoder();
                bytes = all;
                count = all.Length;
            }
            var outChars = count * 2 + 16 > chars.Length ? new char[count * 2 + 16] : chars;
            int n = decoder.GetChars(bytes, 0, count, outChars, 0, flush);
            foreach (var (line, overwritten) in SplitConsole(new string(outChars, 0, n), pending))
                report(line, overwritten);
        }

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                Decode(buffer, read, flush: false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process killed (cancel) — the stream closes under us.
        }
        try { Decode(Array.Empty<byte>(), 0, flush: true); }
        catch (Exception ex) { AppLog.Warn("Decoding tool output failed", ex); }
        if (pending.Length > 0) report(pending.ToString().TrimEnd('\r'), false);
    }
}
