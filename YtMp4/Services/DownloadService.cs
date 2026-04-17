using System.Diagnostics;
using System.Globalization;
using System.IO;
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

    private string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    private string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");
    private string FfmpegDir => ToolsDir;

    public async Task<string?> DownloadAsync(string url, string outputDir, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(YtDlpPath))
            throw new FileNotFoundException($"yt-dlp.exe not found. Place it in: {ToolsDir}");
        if (!File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")))
            throw new FileNotFoundException($"ffmpeg.exe not found. Place it in: {ToolsDir}");
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
            throw new DirectoryNotFoundException($"Output folder not found: {outputDir}");

        string tempDir = Path.Combine(outputDir, ".ytmp4-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Use a RELATIVE output template — if we pass an absolute path, yt-dlp
        // ignores --paths entirely and partial files dump into the output folder
        // instead of our temp subfolder.
        string outputTemplate = "%(title)s.%(ext)s";

        string format = "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba[ext=m4a]/bv*+ba/b";
        // Boost audio during the merge step — re-encode audio only (video stays copied),
        // so we avoid a second full-file ffmpeg pass after download.
        string mergerArgs = "-filter:a volume=4.0 -c:a aac -b:a 192k";
        string args = $"-f \"{format}\" --merge-output-format mp4 --ffmpeg-location \"{FfmpegDir}\" " +
                      $"--concurrent-fragments 16 --http-chunk-size 10M " +
                      $"--paths \"temp:{tempDir}\" --paths \"home:{outputDir}\" " +
                      $"--postprocessor-args \"Merger:{mergerArgs}\" " +
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

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
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
