using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YtMp4.Services;

public record DownloadProgress(double Percentage, string Speed, string Status, bool IsMerging)
{
    public bool IsInfoOnly { get; init; }
}

public class DownloadService
{
    // percent | downloaded_bytes | total_bytes | status
    private static readonly Regex ProgressRegex = new(@"^(\d+\.?\d*)%\|([^|]*)\|([^|]*)\|(.*)$");

    private static bool _ytDlpUpdateChecked;

    private string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    private string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");
    private string FfmpegDir => ToolsDir;
    private string DenoPath => Path.Combine(ToolsDir, "deno.exe");

    private string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtMp4", "logs");

    public string? LastLogFilePath { get; private set; }

    public async Task<string?> DownloadAsync(string url, string outputDir, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(YtDlpPath))
            throw new FileNotFoundException($"yt-dlp.exe not found. Place it in: {ToolsDir}");
        if (!File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")))
            throw new FileNotFoundException($"ffmpeg.exe not found. Place it in: {ToolsDir}");
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
            throw new DirectoryNotFoundException($"Output folder not found: {outputDir}");

        await EnsureYtDlpUpdatedAsync(progress, cancellationToken);

        string tempDir = Path.Combine(outputDir, ".ytmp4-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Use a RELATIVE output template — if we pass an absolute path, yt-dlp
        // ignores --paths entirely and partial files dump into the output folder
        // instead of our temp subfolder.
        string outputTemplate = "%(title)s.%(ext)s";

        string format = "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba[ext=m4a]/bv*+ba/b";
        // YouTube requires running a JS runtime to decrypt signature URLs; without one, some
        // formats resolve to stale/invalid URLs that fail mid-download with HTTP 403.
        string jsRuntimeArg = File.Exists(DenoPath) ? $"--js-runtimes \"deno:{DenoPath}\" " : "";
        string args = $"-f \"{format}\" --merge-output-format mp4 --ffmpeg-location \"{FfmpegDir}\" " +
                      jsRuntimeArg +
                      $"--concurrent-fragments 16 --http-chunk-size 10M " +
                      $"--paths \"temp:{tempDir}\" --paths \"home:{outputDir}\" " +
                      // --print implies --quiet, suppressing progress output. --progress forces it back on.
                      $"--newline --progress --progress-template \"download:%(progress._percent_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.status)s\" " +
                      $"--print \"after_move:FILEPATH:%(filepath)s\" " +
                      $"--restrict-filenames -o \"{outputTemplate}\" \"{url}\"";

        var psi = new ProcessStartInfo(YtDlpPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stderrTail = new Queue<string>();
        var tracker = new ProgressTracker();
        string? finalFilePath = null;

        var log = new StringBuilder();
        var logLock = new object();
        log.AppendLine($"YtMp4 download log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"URL: {url}");
        log.AppendLine($"Command: {YtDlpPath} {args}");
        log.AppendLine();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (logLock) log.AppendLine("[out] " + e.Data);
            if (e.Data.StartsWith("FILEPATH:"))
            {
                finalFilePath = e.Data.Substring("FILEPATH:".Length).Trim();
                return;
            }
            var parsed = tracker.Update(e.Data);
            if (parsed is not null)
            {
                progress.Report(parsed);
                return;
            }
            // Non-progress lines like "[youtube] Extracting URL" / "[youtube] Downloading webpage"
            // — surface them so the user sees activity during info extraction.
            var trimmed = e.Data.Trim();
            if (trimmed.StartsWith("["))
                progress.Report(new DownloadProgress(0, "", trimmed, false) { IsInfoOnly = true });
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (logLock) log.AppendLine("[err] " + e.Data);
            if (e.Data.Contains("[Merger]"))
                progress.Report(new DownloadProgress(100, "", "Merging streams...", true));
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 20) stderrTail.Dequeue();
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderrTail) tail = string.Join(" | ", stderrTail);
                throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}. {tail}");
            }

            return finalFilePath;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
            lock (logLock)
            {
                log.AppendLine();
                log.AppendLine($"Exit code: {(process.HasExited ? process.ExitCode : "n/a")}");
                LastLogFilePath = WriteLogFile(log.ToString());
            }
        }
    }

    private async Task EnsureYtDlpUpdatedAsync(IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (_ytDlpUpdateChecked) return;
        _ytDlpUpdateChecked = true;

        progress.Report(new DownloadProgress(0, "", "Checking for updates...", false) { IsInfoOnly = true });

        try
        {
            var psi = new ProcessStartInfo(YtDlpPath, "-U")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Update check timed out (not a user cancel) — kill it and proceed with the existing binary.
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Best-effort: if the update check fails (e.g. offline), continue with whatever yt-dlp.exe is present.
        }
    }

    private string? WriteLogFile(string content)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            string path = Path.Combine(LogsDir, $"ytmp4-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, content);
            PruneOldLogs();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var oldLogs = new DirectoryInfo(LogsDir).GetFiles("ytmp4-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(20);
            foreach (var file in oldLogs)
                file.Delete();
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup; ignore if files are still locked
        }
    }

    private class ProgressTracker
    {
        private const double Alpha = 0.15;

        private long _lastBytes;
        private DateTime _lastTime;
        private double _emaBytesPerSec;
        private bool _seeded;

        public DownloadProgress? Update(string line)
        {
            line = line.TrimStart();
            var match = ProgressRegex.Match(line);
            if (!match.Success) return null;

            double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pct);
            long.TryParse(match.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out long downloaded);
            long.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out long total);

            var now = DateTime.UtcNow;
            string speedText = "";

            if (_seeded)
            {
                double dt = (now - _lastTime).TotalSeconds;
                long db = downloaded - _lastBytes;
                if (dt > 0.05 && db >= 0)
                {
                    double instantRate = db / dt;
                    _emaBytesPerSec = _emaBytesPerSec == 0
                        ? instantRate
                        : (Alpha * instantRate) + ((1 - Alpha) * _emaBytesPerSec);
                    _lastBytes = downloaded;
                    _lastTime = now;
                }
                else if (db < 0)
                {
                    _lastBytes = downloaded;
                    _lastTime = now;
                }
            }
            else
            {
                _lastBytes = downloaded;
                _lastTime = now;
                _seeded = true;
            }

            if (_emaBytesPerSec > 0)
                speedText = FormatSpeed(_emaBytesPerSec);

            return new DownloadProgress(pct, speedText, match.Groups[4].Value.Trim(), false);
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            while (bytesPerSec >= 1024 && i < units.Length - 1)
            {
                bytesPerSec /= 1024;
                i++;
            }
            return $"{bytesPerSec:0.0} {units[i]}";
        }

    }
}
