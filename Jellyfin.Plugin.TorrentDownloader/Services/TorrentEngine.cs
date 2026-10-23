using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.TorrentDownloader.Configuration;
using Jellyfin.Plugin.TorrentDownloader.Models;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.TorrentDownloader.Services
{
    /// <summary>
    /// Implements torrent engine operations using MonoTorrent.
    /// </summary>
    public partial class TorrentEngine : ITorrentEngine, IDisposable
    {
        private readonly ILogger<TorrentEngine> _logger;
        private readonly Dictionary<Guid, TorrentManager> _torrentManagers;
        private ClientEngine? _engine;
        private EngineSettings? _engineSettings;
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="TorrentEngine"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public TorrentEngine(ILogger<TorrentEngine> logger)
        {
            _logger = logger;
            _torrentManagers = new Dictionary<Guid, TorrentManager>();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _engine?.Dispose();
                }

                _disposed = true;
            }
        }

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            try
            {
                var config = TorrentDownloaderPlugin.Instance?.Configuration;
                if (config == null)
                {
                    throw new InvalidOperationException("Plugin configuration not available");
                }

                var settingBuilder = new EngineSettingsBuilder
                {
                    MaximumDownloadRate = config.MaxDownloadSpeed > 0 ? (int)config.MaxDownloadSpeed : 0,
                    MaximumUploadRate = config.MaxUploadSpeed > 0 ? (int)config.MaxUploadSpeed : 0,
                    AllowPortForwarding = true,
                    AllowLocalPeerDiscovery = true,
                };
                settingBuilder.ListenEndPoints.Add("ipv4", new IPEndPoint(IPAddress.Any, config.ListenPort));

                _engineSettings = settingBuilder.ToSettings();
                _engine = new ClientEngine(_engineSettings);

		Log_InitializeInformation(config.ListenPort, config.EnableDHT, config.EnablePEX, config.EnableEncryption);
                
                // Note: In MonoTorrent 3.0.2, DHT and PEX are enabled via TorrentSettings per torrent
                // Encryption settings are handled automatically by the engine
                
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
		Log_InitializeError(ex);
                throw;
            }
        }
	[LoggerMessage(Level = LogLevel.Information, Message = "Torrent engine initialized successfully - Port: {Port}, DHT: {DHT}, PEX: {PEX}, Encryption: {Encryption}")]
	private partial void Log_InitializeInformation(int Port, bool DHT, bool PEX, bool Encryption);
	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to initialize torrent engine")]
	private partial void Log_InitializeError(Exception Ex);
	
        [LoggerMessage(Level = LogLevel.Information, Message = "Starting download for {Name}")]
	private partial void Log_StartDownloadInformation(string Name);

        /// <inheritdoc />
        public async Task StartDownloadAsync(DownloadEntry downloadEntry)
        {
            ArgumentNullException.ThrowIfNull(downloadEntry);
            
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                if (_engine == null)
                {
                    throw new InvalidOperationException("Torrent engine not initialized");
                }

		Log_StartDownloadInformation(downloadEntry.DisplayName);

                var config = TorrentDownloaderPlugin.Instance?.Configuration;
                if (config == null)
                {
                    throw new InvalidOperationException("Plugin configuration not available");
                }

                // Create torrent settings with configuration
                // Note: PEX and DHT are managed by MonoTorrent automatically in 3.0.2
                var torrentSettings = new TorrentSettingsBuilder
                {
                    AllowInitialSeeding = false,
                    MaximumConnections = 60
                }.ToSettings();

                // Parse torrent source and create manager
                TorrentManager manager;
                if (downloadEntry.TorrentSource.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var magnetLink = MagnetLink.Parse(downloadEntry.TorrentSource);
                    manager = await _engine.AddStreamingAsync(magnetLink, downloadEntry.StagingPath, torrentSettings).ConfigureAwait(false);
                }
                else
                {
                    var torrent = await Torrent.LoadAsync(downloadEntry.TorrentSource).ConfigureAwait(false);
                    manager = await _engine.AddAsync(torrent, downloadEntry.StagingPath, torrentSettings).ConfigureAwait(false);
                }
                
                lock (_lock)
                {
                    _torrentManagers[downloadEntry.DownloadId] = manager;
                }

                // Start download
                await manager.StartAsync().ConfigureAwait(false);

		Log_StartDownloadInformation(
                    downloadEntry.DisplayName,
                    manager.InfoHashes.V1?.ToHex() ?? "unknown");
            }
            catch (Exception ex)
            {
		Log_StartDownloadError(ex, downloadEntry.DisplayName);
                throw;
            }
        }
	[LoggerMessage(Level = LogLevel.Information, Message = "Download started: {Name}, InfoHash: {Hash}")]
	partial void Log_StartDownloadInformation(string Name, string Hash);
	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to start download for {Name}")]
	partial void Log_StartDownloadError(Exception ex, string Name);

        /// <inheritdoc />
        public async Task PauseDownloadAsync(Guid downloadId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var manager = GetTorrentManager(downloadId);
                if (manager != null)
                {
                    await manager.PauseAsync();
		    Log_PauseDownloadInformation(downloadId);
                }
            }
            catch (Exception ex)
            {
                Log_PauseDownloadError(ex, downloadId);
                throw;
            }
        }
    	[LoggerMessage(Level = LogLevel.Information, Message = "Download paused: {DownloadId}")]
	partial void Log_PauseDownloadInformation(System.Guid DownloadId);
    	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to pause download {DownloadId}")]
	partial void Log_PauseDownloadError(Exception ex, System.Guid DownloadId);

        /// <inheritdoc />
        public async Task ResumeDownloadAsync(Guid downloadId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var manager = GetTorrentManager(downloadId);
                if (manager != null)
                {
                    await manager.StartAsync();
                    Log_ResumeDownloadInformation(downloadId);
                }
            }
            catch (Exception ex)
            {
	    	Log_ResumeDownloadError(ex, downloadId);
                throw;
            }
        }
    	[LoggerMessage(Level = LogLevel.Information, Message = "Download resumed: {DownloadId}")]
	partial void Log_ResumeDownloadInformation(System.Guid DownloadId);
    	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to resume download {DownloadId}")]
	partial void Log_ResumeDownloadError(Exception ex, System.Guid DownloadId);

        /// <inheritdoc />
        public async Task StopDownloadAsync(Guid downloadId, bool deleteFiles)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var manager = GetTorrentManager(downloadId);
                if (manager != null)
                {
                    await manager.StopAsync();
                    
                    if (_engine != null)
                    {
                        await _engine.RemoveAsync(manager);
                    }

                    lock (_lock)
                    {
                        _torrentManagers.Remove(downloadId);
                    }

                    if (deleteFiles && Directory.Exists(manager.SavePath))
                    {
                        Directory.Delete(manager.SavePath, true);
                    }

                    Log_StopDownloadInformation(downloadId, deleteFiles);
                }
            }
            catch (Exception ex)
            {
		Log_StopDownloadError(ex, downloadId);
                throw;
            }
        }
    	[LoggerMessage(Level = LogLevel.Information, Message = "Download stopped: {DownloadId}, DeleteFiles: {DeleteFiles}")]
	partial void Log_StopDownloadInformation(System.Guid DownloadId, bool DeleteFiles);
    	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to stop download {DownloadId}")]
	partial void Log_StopDownloadError(Exception ex, System.Guid DownloadId);

        /// <inheritdoc />
        public Task<DownloadEntry?> GetDownloadProgressAsync(Guid downloadId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var manager = GetTorrentManager(downloadId);
                if (manager == null)
                {
                    return Task.FromResult<DownloadEntry?>(null);
                }

                var entry = new DownloadEntry
                {
                    DownloadId = downloadId,
                    TotalSize = manager.Torrent?.Size ?? 0,
                    DownloadedSize = manager.Monitor.DataBytesReceived,
                    ProgressPercent = (decimal)manager.Progress,
                    DownloadSpeed = manager.Monitor.DownloadRate,
                    UploadSpeed = manager.Monitor.UploadRate,
                    PeerCount = manager.OpenConnections,
                    DisplayName = manager.Torrent?.Name ?? "Unknown",
                    InfoHash = manager.InfoHashes.V1?.ToHex() ?? "unknown"
                };

                // Calculate ETA
                if (entry.DownloadSpeed > 0)
                {
                    var remaining = entry.TotalSize - entry.DownloadedSize;
                    entry.EstimatedTimeRemaining = remaining / entry.DownloadSpeed;
                }

                return Task.FromResult<DownloadEntry?>(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get progress for download {DownloadId}", downloadId);
		Log_GetDownloadProgressError(ex, downloadId);
                return Task.FromResult<DownloadEntry?>(null);
            }
        }
	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to get progress for download {DownloadId}")]
	partial void Log_GetDownloadProgressError(Exception ex, System.Guid DownloadId);

        /// <inheritdoc />
        public Task<bool> ValidateTorrentSourceAsync(string torrentSource)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(torrentSource))
                {
                    return Task.FromResult(false);
                }

                // Validate magnet link
                if (torrentSource.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        MagnetLink.Parse(torrentSource);
                        return Task.FromResult(true);
                    }
                    catch
                    {
                        return Task.FromResult(false);
                    }
                }

                // Validate torrent file
                if (File.Exists(torrentSource) && torrentSource.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate torrent source");
		Log_ValidateTorrentSourceError(ex);
                return Task.FromResult(false);
            }
        }
	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to validate torrent source")]
	partial void Log_ValidateTorrentSourceError(Exception ex);

        /// <inheritdoc />
        public async Task ShutdownAsync()
        {
            try
            {
                if (_engine != null)
                {
                    foreach (var manager in _torrentManagers.Values.ToList())
                    {
                        await manager.StopAsync();
                    }

                    await _engine.StopAllAsync();
                    _torrentManagers.Clear();
                }

                Log_ShutdownInformation();
            }
            catch (Exception ex)
            {
                Log_ShutdownError(ex);
            }
        }
	[LoggerMessage(Level = LogLevel.Information, Message = "Torrent engine shut down successfully")]
	partial void Log_ShutdownInformation();
	[LoggerMessage(Level = LogLevel.Error, Message = "Error during torrent engine shutdown")]
	partial void Log_ShutdownError(Exception Ex);

        private TorrentManager? GetTorrentManager(Guid downloadId)
        {
            lock (_lock)
            {
                return _torrentManagers.TryGetValue(downloadId, out var manager) ? manager : null;
            }
        }
    }
}
