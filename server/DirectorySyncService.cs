namespace Slopterm.Server;

/// <summary>Which way a one-time sync (or the initial reconcile of a watch) copies files.</summary>
public enum SyncDirection
{
    // Local is the source of truth: copy files that are missing or newer locally up to remote.
    LocalToRemote,
    // Remote is the source of truth: copy files that are missing or newer remotely down to local.
    RemoteToLocal,
}

/// <summary>Result of a one-time reconcile - how many files were copied, and whether nothing was.</summary>
public sealed record SyncResult(int FilesTransferred);

/// <summary>Live state of one running watch, reported to the UI like ForwardingService.GetStatus().</summary>
public sealed record SyncWatchStatus(
    string WatchId,
    string SessionId,
    string LocalDir,
    string RemoteDir,
    SyncDirection Direction,
    string State, // watching | syncing | error
    int FilesTransferred,
    string? Error);

/// <summary>
/// One-time directory sync plus ongoing directory watch between a local folder and a remote
/// folder over an existing <see cref="SftpSession"/>. Reconciliation is a simple, safe
/// "copy missing/newer" pass in one direction - v1 does NOT propagate deletions and does NOT
/// resolve conflicts (a file that changed on both sides just gets whichever side is source-of-
/// truth copied over). Symlinks are followed as ordinary files/dirs by the underlying SFTP/IO
/// walk, not specially handled.
///
/// A watch keeps a local folder and remote folder in sync until stopped: the local side is
/// pushed immediately via a FileSystemWatcher (SFTP has no change notifications), and a polling
/// loop re-runs the same reconciliation on an interval so remote-side changes (and any local
/// change the watcher missed) are still caught. Modeled on ForwardingService: a long-lived
/// background worker per watch, best-effort, with failures surfaced in GetStatus rather than
/// thrown.
/// </summary>
public sealed class DirectorySyncService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, Watch> _watches = new(); // key: watchId
    private bool _disposed;

    /// <summary>
    /// Reconciles once in the given direction over an existing session: copies every file that's
    /// missing on the destination or newer on the source (by mtime; size differing also counts).
    /// </summary>
    public static async Task<SyncResult> SyncOnceAsync(
        SftpSession session, string localDir, string remoteDir, SyncDirection direction, CancellationToken ct)
    {
        return direction == SyncDirection.LocalToRemote
            ? await SyncLocalToRemoteAsync(session, localDir, remoteDir, ct)
            : await SyncRemoteToLocalAsync(session, localDir, remoteDir, ct);
    }

    private static async Task<SyncResult> SyncLocalToRemoteAsync(
        SftpSession session, string localDir, string remoteDir, CancellationToken ct)
    {
        if (!Directory.Exists(localDir))
        {
            throw new DirectoryNotFoundException($"Local directory not found: {localDir}");
        }

        var remoteByRelative = session.WalkFiles(remoteDir)
            .ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

        var transferred = 0;
        foreach (var localPath in Directory.EnumerateFiles(localDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = ToPosixRelative(localDir, localPath);
            var info = new FileInfo(localPath);
            if (remoteByRelative.TryGetValue(relative, out var remote) && !IsSourceNewer(info.Length, info.LastWriteTimeUtc, remote))
            {
                continue;
            }

            await session.UploadToPathAsync(localPath, SftpSession.JoinPosix(remoteDir, relative), ct);
            transferred++;
        }

        return new SyncResult(transferred);
    }

    private static async Task<SyncResult> SyncRemoteToLocalAsync(
        SftpSession session, string localDir, string remoteDir, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);

        var transferred = 0;
        foreach (var remote in session.WalkFiles(remoteDir))
        {
            ct.ThrowIfCancellationRequested();
            var localPath = Path.Combine(localDir, remote.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
            {
                var info = new FileInfo(localPath);
                if (!IsSourceNewer(remote.Size, remote.ModifiedUtc, new RemoteFile(remote.RelativePath, info.Length, info.LastWriteTimeUtc)))
                {
                    continue;
                }
            }

            await session.DownloadToPathAsync(SftpSession.JoinPosix(remoteDir, remote.RelativePath), localPath, remote.ModifiedUtc, ct);
            transferred++;
        }

        return new SyncResult(transferred);
    }

    // Source wins if the destination is missing (handled by the caller), its size differs, or
    // its mtime is newer. A 2s tolerance absorbs filesystem/SFTP mtime granularity differences
    // so an unchanged file isn't re-copied on every poll.
    private static bool IsSourceNewer(long sourceSize, DateTime sourceModifiedUtc, RemoteFile destination)
    {
        return sourceSize != destination.Size
            || sourceModifiedUtc > destination.ModifiedUtc.AddSeconds(2);
    }

    private static string ToPosixRelative(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Starts (or returns the existing) watch mirroring one local dir and remote dir.</summary>
    public string StartWatch(SftpSession session, string localDir, string remoteDir, SyncDirection direction)
    {
        if (!Directory.Exists(localDir))
        {
            throw new DirectoryNotFoundException($"Local directory not found: {localDir}");
        }

        var watch = new Watch(session, localDir, remoteDir, direction);
        lock (_lock)
        {
            if (_disposed)
            {
                watch.Dispose();
                throw new InvalidOperationException("Service is shutting down.");
            }

            _watches[watch.Id] = watch;
        }

        watch.Start();
        return watch.Id;
    }

    public void StopWatch(string watchId)
    {
        Watch? watch;
        lock (_lock)
        {
            if (!_watches.Remove(watchId, out watch))
            {
                return;
            }
        }

        watch.Dispose();
    }

    public IReadOnlyList<SyncWatchStatus> GetStatus()
    {
        lock (_lock)
        {
            return _watches.Values.Select(w => w.GetStatus()).ToList();
        }
    }

    public void Dispose()
    {
        List<Watch> watches;
        lock (_lock)
        {
            _disposed = true;
            watches = _watches.Values.ToList();
            _watches.Clear();
        }

        foreach (var watch in watches)
        {
            watch.Dispose();
        }
    }

    /// <summary>One running watch: its FileSystemWatcher (local push) plus a polling reconcile loop.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly SftpSession _session;
        private readonly string _localDir;
        private readonly string _remoteDir;
        private readonly SyncDirection _direction;
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private readonly object _stateLock = new();
        private FileSystemWatcher? _fsWatcher;
        private Task? _loop;
        private string _state = "watching"; // watching | syncing | error
        private int _filesTransferred;
        private string? _error;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Watch(SftpSession session, string localDir, string remoteDir, SyncDirection direction)
        {
            _session = session;
            _localDir = localDir;
            _remoteDir = remoteDir;
            _direction = direction;
        }

        public void Start()
        {
            // The local side pushes immediately on any change (SFTP can't notify us of remote
            // ones), so a local watch reacts without waiting for the next poll. A remote->local
            // watch relies purely on polling since there's nothing local to react to.
            if (_direction == SyncDirection.LocalToRemote)
            {
                _fsWatcher = new FileSystemWatcher(_localDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _fsWatcher.Changed += OnLocalChanged;
                _fsWatcher.Created += OnLocalChanged;
                _fsWatcher.Renamed += OnLocalChanged;
                _fsWatcher.EnableRaisingEvents = true;
            }

            _loop = Task.Run(() => RunLoop(_cts.Token));
        }

        private void OnLocalChanged(object sender, FileSystemEventArgs e) => _wake.Set();

        public SyncWatchStatus GetStatus()
        {
            lock (_stateLock)
            {
                return new SyncWatchStatus(Id, _session.Id, _localDir, _remoteDir, _direction, _state, _filesTransferred, _error);
            }
        }

        private async Task RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    lock (_stateLock)
                    {
                        _state = "syncing";
                    }

                    var result = await SyncOnceAsync(_session, _localDir, _remoteDir, _direction, token);
                    lock (_stateLock)
                    {
                        _state = "watching";
                        _filesTransferred += result.FilesTransferred;
                        _error = null;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Best-effort - a transient failure (e.g. the SFTP session dropped) surfaces
                    // in GetStatus and the loop retries on the next poll rather than dying.
                    lock (_stateLock)
                    {
                        _state = "error";
                        _error = ex.Message;
                    }
                }

                // Woken early by a local change, or on the poll interval - either way reconcile again.
                try
                {
                    _wake.Wait(PollInterval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _wake.Reset();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _wake.Set();
            if (_fsWatcher is not null)
            {
                _fsWatcher.EnableRaisingEvents = false;
                _fsWatcher.Dispose();
            }

            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort teardown - the token already told the loop to stop.
            }

            _cts.Dispose();
            _wake.Dispose();
        }
    }
}
