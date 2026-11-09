using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtMp4.Services;

public record DownloadProgress(double Percentage, string Speed, string Status, bool IsMerging)
{
    public bool IsInfoOnly { get; init; }
    public bool IsTranscoding { get; init; }
}

public class DownloadService
{
    // percent | downloaded_bytes | total_bytes | status
    private static readonly Regex ProgressRegex = new(@"^(\d+\.?\d*)%\|([^|]*)\|([^|]*)\|(.*)$");

    // X.com plays H.264 video + AAC audio only, capped at 1920x1200 and 60fps.
    private const int XMaxWidth = 1920;
    private const int XMaxHeight = 1200;
    private const double XMaxFps = 60;

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static bool _ytDlpUpdateChecked;
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    private string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    private string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtMp4", "logs");

    public string? LastLogFilePath { get; private set; }

    public string GetYtDlpPath()
    {
        var local = Path.Combine(ToolsDir, "yt-dlp.exe");
        if (File.Exists(local)) return local;
        var onPath = FindOnPath("yt-dlp.exe");
        return onPath ?? local;
    }

    public string? GetFfmpegDir()
    {
        if (File.Exists(Path.Combine(ToolsDir, "ffmpeg.exe")))
            return ToolsDir;
        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath != null)
            return Path.GetDirectoryName(onPath);
        return null;
    }

    public string? GetDenoPath()
    {
        var local = Path.Combine(ToolsDir, "deno.exe");
        if (File.Exists(local)) return local;
        return FindOnPath("deno.exe");
    }

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    public async Task<string?> DownloadAsync(
        string url,
        string outputDir,
        bool xCompatible,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        var logLock = new object();
        log.AppendLine($"YtMp4 download log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"URL: {url}");
        log.AppendLine();

        try
        {
            return await DownloadInternalAsync(url, outputDir, xCompatible, progress, cancellationToken, hasRetriedWithUpdate: false, log, logLock);
        }
        finally
        {
            lock (logLock)
            {
                LastLogFilePath = WriteLogFile(log.ToString());
            }
        }
    }

    private async Task<string?> DownloadInternalAsync(
        string url,
        string outputDir,
        bool xCompatible,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken,
        bool hasRetriedWithUpdate,
        StringBuilder log,
        object logLock)
    {
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
            throw new DirectoryNotFoundException($"Output folder not found: {outputDir}");

        await EnsureYtDlpUpdatedAsync(progress, log, logLock, cancellationToken, force: false);

        string ytDlpPath = GetYtDlpPath();
        if (!File.Exists(ytDlpPath))
            throw new FileNotFoundException($"yt-dlp.exe not found. Place it in: {ToolsDir}");

        string? ffmpegDir = GetFfmpegDir();
        if (ffmpegDir == null)
            throw new FileNotFoundException($"ffmpeg.exe not found. Place it in: {ToolsDir} or install it on system PATH.");

        string? denoPath = GetDenoPath();

        string tempDir = Path.Combine(outputDir, ".ytmp4-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Use a RELATIVE output template — if we pass an absolute path, yt-dlp
        // ignores --paths entirely and partial files dump into the output folder
        // instead of our temp subfolder.
        string outputTemplate = "%(title)s.%(ext)s";

        // "mp4" is not the same thing as H.264 on YouTube — av01 (AV1) is served in mp4
        // containers too, and yt-dlp's default codec preference is av01 > vp9 > h264. So an
        // ext-only selector hands back an AV1-in-mp4 file, which X.com rejects with
        // "Incompatible video codecs" (YouTube re-encodes on upload, so it never complains).
        // YouTube's avc1 ladder stops at 1080p, which is also X's practical ceiling.
        string format = xCompatible
            ? "bv*[vcodec^=avc1][height<=1080][fps<=60]+ba[acodec^=mp4a]/" +
              "bv*[vcodec^=avc1]+ba[acodec^=mp4a]/" +
              "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b"
            : "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba[ext=m4a]/bv*+ba/b";

        // Move the moov atom to the front and report the codecs we actually got, so we can
        // transcode afterwards for the few videos with no H.264 ladder at all.
        string xCompatArgs = xCompatible
            ? "--postprocessor-args \"Merger:-movflags +faststart\" " +
              "--print \"after_move:META:%(vcodec)s|%(acodec)s|%(width)s|%(height)s|%(fps)s|%(duration)s\" "
            : "";

        // YouTube requires running a JS runtime to decrypt signature URLs; without one, some
        // formats resolve to stale/invalid URLs that fail mid-download with HTTP 403.
        string jsRuntimeArg = !string.IsNullOrEmpty(denoPath) ? $"--js-runtimes \"deno:{denoPath}\" " : "";
        string args = $"-f \"{format}\" --merge-output-format mp4 --ffmpeg-location \"{ffmpegDir}\" " +
                      jsRuntimeArg +
                      $"--no-update --concurrent-fragments 16 --http-chunk-size 10M " +
                      $"--paths \"temp:{tempDir}\" --paths \"home:{outputDir}\" " +
                      // --print implies --quiet, suppressing progress output. --progress forces it back on.
                      $"--newline --progress --progress-template \"download:%(progress._percent_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.status)s\" " +
                      $"--print \"after_move:FILEPATH:%(filepath)s\" " +
                      xCompatArgs +
                      $"--restrict-filenames -o \"{outputTemplate}\" \"{url}\"";

        LogLine(log, logLock, $"Command: {ytDlpPath} {args}");
        LogLine(log, logLock, "");

        var psi = new ProcessStartInfo(ytDlpPath, args)
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
        MediaMeta? meta = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            LogLine(log, logLock, "[out] " + e.Data);
            if (e.Data.StartsWith("FILEPATH:"))
            {
                finalFilePath = e.Data.Substring("FILEPATH:".Length).Trim();
                return;
            }
            if (e.Data.StartsWith("META:"))
            {
                meta = MediaMeta.Parse(e.Data.Substring("META:".Length));
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
            LogLine(log, logLock, "[err] " + e.Data);
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

            LogLine(log, logLock, "");
            LogLine(log, logLock, $"Exit code: {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderrTail) tail = string.Join(" | ", stderrTail);

                bool isOutdatedError =
                    tail.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
                    tail.Contains("403: Forbidden", StringComparison.OrdinalIgnoreCase) ||
                    tail.Contains("older than 90 days", StringComparison.OrdinalIgnoreCase) ||
                    tail.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase);

                if (isOutdatedError && !hasRetriedWithUpdate)
                {
                    LogLine(log, logLock, "[recovery] YouTube returned 403 Forbidden / bot detection. Forcing yt-dlp update and retrying...");
                    progress.Report(new DownloadProgress(0, "", "YouTube blocked download (403). Updating yt-dlp and retrying...", false) { IsInfoOnly = true });

                    await EnsureYtDlpUpdatedAsync(progress, log, logLock, cancellationToken, force: true);
                    return await DownloadInternalAsync(url, outputDir, xCompatible, progress, cancellationToken, hasRetriedWithUpdate: true, log, logLock);
                }

                throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}. {tail}");
            }

            if (xCompatible && finalFilePath is not null && File.Exists(finalFilePath))
                finalFilePath = await MakeXCompatibleAsync(finalFilePath, ffmpegDir, meta, progress, log, logLock, cancellationToken);

            return finalFilePath;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Makes the finished file something X.com will accept. Streams that already qualify are
    /// stream-copied, so this is a cheap remux in the normal case; it only really re-encodes
    /// when YouTube had no H.264 ladder for the video (some Shorts and newer uploads are
    /// AV1/VP9 only) or the source is above X's frame size / rate caps.
    /// </summary>
    private async Task<string> MakeXCompatibleAsync(
        string path,
        string ffmpegDir,
        MediaMeta? meta,
        IProgress<DownloadProgress> progress,
        StringBuilder log,
        object logLock,
        CancellationToken cancellationToken)
    {
        if (meta is null)
        {
            LogLine(log, logLock, "[x-compat] yt-dlp reported no stream metadata; leaving the file as-is");
            return path;
        }

        if (!meta.HasKnownCodecs)
        {
            LogLine(log, logLock, $"[x-compat] codecs unknown ({meta}); leaving the file as-is");
            return path;
        }

        bool videoOk = meta.IsXVideoCompatible;
        bool audioOk = meta.IsXAudioCompatible;
        if (videoOk && audioOk)
        {
            LogLine(log, logLock, $"[x-compat] {meta} is already X-compatible; no conversion needed");
            return path;
        }

        string tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + ".xtmp-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");

        var ffArgs = new StringBuilder();
        ffArgs.Append($"-hide_banner -nostdin -y -i \"{path}\" ");
        if (videoOk)
        {
            ffArgs.Append("-c:v copy ");
        }
        else
        {
            // No explicit -level: x264 picks a conformant one for the (capped) frame size.
            ffArgs.Append("-c:v libx264 -profile:v high -pix_fmt yuv420p -preset veryfast -crf 20 ");
            string fpsCap = meta.Fps > XMaxFps ? $",fps={XMaxFps}" : "";
            ffArgs.Append($"-vf \"scale='min({XMaxWidth},iw)':'min({XMaxHeight},ih)'" +
                          $":force_original_aspect_ratio=decrease:force_divisible_by=2{fpsCap}\" ");
        }
        ffArgs.Append(audioOk ? "-c:a copy " : "-c:a aac -b:a 192k -ac 2 ");
        ffArgs.Append($"-movflags +faststart -progress pipe:1 -nostats \"{tempPath}\"");

        string ffmpegPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
        LogLine(log, logLock, $"[x-compat] {meta} needs conversion (video ok: {videoOk}, audio ok: {audioOk})");
        LogLine(log, logLock, $"[x-compat] command: {ffmpegPath} {ffArgs}");

        const string statusText = "Converting for X.com...";
        double durationSec = meta.Duration;
        progress.Report(durationSec > 0
            ? new DownloadProgress(0, "", statusText, false) { IsTranscoding = true }
            : new DownloadProgress(0, "", statusText, false) { IsInfoOnly = true });

        var psi = new ProcessStartInfo(ffmpegPath, ffArgs.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var ffmpeg = new Process { StartInfo = psi };
        var stderrTail = new Queue<string>();

        ffmpeg.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || durationSec <= 0) return;
            // -progress writes key=value lines; out_time_us is the position in the output.
            if (!e.Data.StartsWith("out_time_us=")) return;
            if (!long.TryParse(e.Data.Substring("out_time_us=".Length), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out long microseconds) || microseconds <= 0)
                return;
            double pct = Math.Clamp(microseconds / 1_000_000.0 / durationSec * 100.0, 0, 100);
            progress.Report(new DownloadProgress(pct, "", statusText, false) { IsTranscoding = true });
        };

        ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            LogLine(log, logLock, "[ffmpeg] " + e.Data);
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 20) stderrTail.Dequeue();
            }
        };

        ffmpeg.Start();
        ffmpeg.BeginOutputReadLine();
        ffmpeg.BeginErrorReadLine();

        try
        {
            await ffmpeg.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!ffmpeg.HasExited)
                ffmpeg.Kill(entireProcessTree: true);
            TryDeleteFile(tempPath);
            throw;
        }

        if (ffmpeg.ExitCode != 0)
        {
            TryDeleteFile(tempPath);
            string tail;
            lock (stderrTail) tail = string.Join(" | ", stderrTail);
            throw new InvalidOperationException(
                $"X.com conversion failed (ffmpeg exited with code {ffmpeg.ExitCode}). {tail}");
        }

        // The download itself succeeded — if the swap fails, keep the original file.
        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            LogLine(log, logLock, $"[x-compat] could not replace the original file: {ex.Message}");
            TryDeleteFile(tempPath);
            return path;
        }

        LogLine(log, logLock, "[x-compat] conversion complete");
        return path;
    }

    public async Task EnsureYtDlpUpdatedAsync(
        IProgress<DownloadProgress>? progress,
        StringBuilder? log,
        object? logLock,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!force && _ytDlpUpdateChecked) return;

        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && _ytDlpUpdateChecked) return;

            string targetPath = Path.Combine(ToolsDir, "yt-dlp.exe");
            bool fileExists = File.Exists(targetPath);

            progress?.Report(new DownloadProgress(0, "", "Checking for yt-dlp updates...", false) { IsInfoOnly = true });
            LogLine(log, logLock, "[update] Checking for yt-dlp updates...");

            string? localVersion = fileExists ? await GetLocalYtDlpVersionAsync(targetPath, cancellationToken) : null;
            LogLine(log, logLock, $"[update] Current local version: {localVersion ?? "not found"}");

            var (remoteVersion, assetApiUrl, assetBrowserUrl) = await FetchLatestReleaseInfoAsync(cancellationToken);

            if (remoteVersion != null)
            {
                LogLine(log, logLock, $"[update] Latest GitHub version: {remoteVersion}");

                if (localVersion != null && string.Equals(localVersion.Trim(), remoteVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    LogLine(log, logLock, "[update] yt-dlp is already up to date.");
                    _ytDlpUpdateChecked = true;
                    return;
                }

                progress?.Report(new DownloadProgress(0, "", $"Updating yt-dlp to {remoteVersion}...", false) { IsInfoOnly = true });
                LogLine(log, logLock, $"[update] Downloading yt-dlp {remoteVersion}...");

                bool downloaded = false;
                if (!string.IsNullOrEmpty(assetApiUrl))
                {
                    downloaded = await DownloadYtDlpDirectAsync(assetApiUrl, targetPath, progress, log, logLock, cancellationToken, isApiAsset: true);
                }
                if (!downloaded && !string.IsNullOrEmpty(assetBrowserUrl))
                {
                    downloaded = await DownloadYtDlpDirectAsync(assetBrowserUrl, targetPath, progress, log, logLock, cancellationToken, isApiAsset: false);
                }

                if (downloaded)
                {
                    LogLine(log, logLock, $"[update] Successfully updated yt-dlp to {remoteVersion}.");
                    _ytDlpUpdateChecked = true;
                    return;
                }
            }

            if (fileExists)
            {
                LogLine(log, logLock, "[update] Direct download unavailable; attempting 'yt-dlp -U' fallback...");
                bool cliUpdated = await RunYtDlpSelfUpdateAsync(targetPath, log, logLock, cancellationToken);
                if (cliUpdated)
                {
                    LogLine(log, logLock, "[update] Successfully updated yt-dlp via CLI.");
                    _ytDlpUpdateChecked = true;
                    return;
                }
            }

            if (File.Exists(targetPath) || FindOnPath("yt-dlp.exe") != null)
            {
                LogLine(log, logLock, "[update] Continuing with current yt-dlp binary.");
                _ytDlpUpdateChecked = true;
            }
            else
            {
                throw new FileNotFoundException($"yt-dlp.exe is missing and could not be downloaded automatically to: {targetPath}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLine(log, logLock, $"[update] Warning: Update failed: {ex.Message}");
            if (!File.Exists(Path.Combine(ToolsDir, "yt-dlp.exe")) && FindOnPath("yt-dlp.exe") == null)
                throw;
            _ytDlpUpdateChecked = true;
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static async Task<(string? TagName, string? AssetApiUrl, string? AssetBrowserUrl)> FetchLatestReleaseInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
            req.Headers.UserAgent.ParseAdd("YtMp4/1.0 (+https://github.com/sacor10/ytmp4)");
            req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

            using var resp = await HttpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null);

            using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string? tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString()?.Trim().TrimStart('v') : null;
            string? assetApiUrl = null;
            string? assetBrowserUrl = null;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameElem) && nameElem.GetString() == "yt-dlp.exe")
                    {
                        if (asset.TryGetProperty("url", out var urlElem))
                            assetApiUrl = urlElem.GetString();
                        if (asset.TryGetProperty("browser_download_url", out var browserElem))
                            assetBrowserUrl = browserElem.GetString();
                        break;
                    }
                }
            }

            return (tagName, assetApiUrl, assetBrowserUrl);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<bool> DownloadYtDlpDirectAsync(
        string downloadUrl,
        string targetPath,
        IProgress<DownloadProgress>? progress,
        StringBuilder? log,
        object? logLock,
        CancellationToken cancellationToken,
        bool isApiAsset)
    {
        string toolsDirectory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(toolsDirectory);
        string tempFile = targetPath + ".download-" + Guid.NewGuid().ToString("N");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.UserAgent.ParseAdd("YtMp4/1.0 (+https://github.com/sacor10/ytmp4)");
            if (isApiAsset)
            {
                req.Headers.Accept.ParseAdd("application/octet-stream");
            }

            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                LogLine(log, logLock, $"[update] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {downloadUrl}");
                return false;
            }

            long totalBytes = resp.Content.Headers.ContentLength ?? -1;
            using var contentStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                    {
                        double pct = Math.Round((double)totalRead / totalBytes * 100, 1);
                        progress?.Report(new DownloadProgress(pct, "", $"Downloading yt-dlp ({pct}%)...", false) { IsInfoOnly = true });
                    }
                }
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length < 10 * 1024 * 1024)
            {
                LogLine(log, logLock, $"[update] Downloaded file is too small ({fi.Length} bytes), discarding.");
                TryDeleteFile(tempFile);
                return false;
            }

            // Replace target file
            string backupFile = targetPath + ".old";
            TryDeleteFile(backupFile);
            if (File.Exists(targetPath))
            {
                File.Move(targetPath, backupFile);
            }
            File.Move(tempFile, targetPath, overwrite: true);
            TryDeleteFile(backupFile);
            return true;
        }
        catch (Exception ex)
        {
            LogLine(log, logLock, $"[update] Failed downloading from {downloadUrl}: {ex.Message}");
            TryDeleteFile(tempFile);
            return false;
        }
    }

    private static async Task<string?> GetLocalYtDlpVersionAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(ytDlpPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            string output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> RunYtDlpSelfUpdateAsync(
        string ytDlpPath,
        StringBuilder? log,
        object? logLock,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(ytDlpPath, "-U")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) LogLine(log, logLock, "[update-cli] " + e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) LogLine(log, logLock, "[update-cli-err] " + e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                LogLine(log, logLock, "[update-cli] 'yt-dlp -U' timed out after 60s.");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogLine(log, logLock, $"[update-cli] 'yt-dlp -U' failed: {ex.Message}");
            return false;
        }
    }

    private static void LogLine(StringBuilder? log, object? logLock, string text)
    {
        if (log == null || logLock == null) return;
        lock (logLock)
        {
            log.AppendLine(text);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
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

    /// <summary>Stream details of the selected format, as reported by yt-dlp's --print.</summary>
    private record MediaMeta(string VideoCodec, string AudioCodec, int Width, int Height, double Fps, double Duration)
    {
        public static MediaMeta? Parse(string line)
        {
            var parts = line.Split('|');
            if (parts.Length < 6) return null;
            return new MediaMeta(
                parts[0].Trim(), parts[1].Trim(),
                (int)ParseNumber(parts[2]), (int)ParseNumber(parts[3]),
                ParseNumber(parts[4]), ParseNumber(parts[5]));
        }

        // yt-dlp prints "NA" for anything it does not know; treat that as "unconstrained".
        private static double ParseNumber(string value) =>
            double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0;

        // yt-dlp prints "NA" when it has no value; don't re-encode on a guess.
        public bool HasKnownCodecs =>
            IsKnown(VideoCodec) && IsKnown(AudioCodec);

        private static bool IsKnown(string codec) =>
            !string.IsNullOrWhiteSpace(codec) &&
            !codec.Equals("NA", StringComparison.OrdinalIgnoreCase) &&
            !codec.Equals("none", StringComparison.OrdinalIgnoreCase);

        public bool IsXVideoCompatible =>
            (VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
             VideoCodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase)) &&
            (Width <= 0 || Width <= XMaxWidth) &&
            (Height <= 0 || Height <= XMaxHeight) &&
            (Fps <= 0 || Fps <= XMaxFps);

        public bool IsXAudioCompatible =>
            AudioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ||
            AudioCodec.StartsWith("aac", StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            $"{VideoCodec}/{AudioCodec} {Width}x{Height}@{Fps.ToString(CultureInfo.InvariantCulture)}fps";
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
