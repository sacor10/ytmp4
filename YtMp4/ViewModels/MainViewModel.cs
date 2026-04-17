using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using YtMp4.Services;

namespace YtMp4.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DownloadService _service = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private string _url = string.Empty;

    [ObservableProperty] private string _outputFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayVideoCommand))]
    [NotifyPropertyChangedFor(nameof(HasCompletedDownload))]
    private string? _lastDownloadedFile;

    public bool HasCompletedDownload => !string.IsNullOrEmpty(LastDownloadedFile);

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ProgressValue = 0;
        SpeedText = string.Empty;
        StatusText = "Starting...";
        IsIndeterminate = true;
        LastDownloadedFile = null;

        var progress = new Progress<DownloadProgress>(update =>
        {
            if (update.IsMerging)
            {
                IsIndeterminate = true;
                StatusText = "Merging streams...";
                return;
            }

            if (update.IsInfoOnly)
            {
                IsIndeterminate = true;
                StatusText = update.Status;
                return;
            }

            IsIndeterminate = false;
            ProgressValue = update.Percentage;
            SpeedText = update.Speed;
            StatusText = update.Percentage >= 100 ? "Finishing..." : "Downloading...";
        });

        try
        {
            var path = await _service.DownloadAsync(Url, OutputFolder, progress, _cts.Token);
            IsIndeterminate = false;
            ProgressValue = 100;
            LastDownloadedFile = path;
            StatusText = "Done!";
        }
        catch (OperationCanceledException)
        {
            IsIndeterminate = false;
            ProgressValue = 0;
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            IsIndeterminate = false;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            SpeedText = string.Empty;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanDownload() => !string.IsNullOrWhiteSpace(Url);

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select download folder",
            InitialDirectory = OutputFolder
        };
        if (dialog.ShowDialog() == true)
            OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!Directory.Exists(OutputFolder)) return;
        // If we have a specific file, select it in Explorer; otherwise just open the folder.
        if (!string.IsNullOrEmpty(LastDownloadedFile) && File.Exists(LastDownloadedFile))
            Process.Start("explorer.exe", $"/select,\"{LastDownloadedFile}\"");
        else
            Process.Start("explorer.exe", $"\"{OutputFolder}\"");
    }

    [RelayCommand(CanExecute = nameof(CanPlayVideo))]
    private void PlayVideo()
    {
        if (string.IsNullOrEmpty(LastDownloadedFile) || !File.Exists(LastDownloadedFile)) return;
        Process.Start(new ProcessStartInfo(LastDownloadedFile) { UseShellExecute = true });
    }

    private bool CanPlayVideo() => !string.IsNullOrEmpty(LastDownloadedFile) && File.Exists(LastDownloadedFile);
}
