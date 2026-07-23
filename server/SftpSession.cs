using Renci.SshNet;

namespace Slopterm.Server;

public sealed record FsEntry(string Name, bool IsDirectory, long Size, DateTime ModifiedUtc);

public sealed record FsListing(string Path, string? Parent, IReadOnlyList<FsEntry> Entries);

// A single remote file in a sync walk: its path (relative to the sync root, or absolute for
// TryGetFile), size and last-modified time - the two fields DirectorySyncService reconciles on.
public sealed record RemoteFile(string RelativePath, long Size, DateTime ModifiedUtc);

public sealed class SftpSession : IDisposable
{
    private readonly SftpClient _client;

    public string Id { get; }
    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public string HomeDirectory { get; }

    private SftpSession(string id, SftpClient client, string host, int port, string username)
    {
        Id = id;
        _client = client;
        Host = host;
        Port = port;
        Username = username;
        HomeDirectory = client.WorkingDirectory;
    }

    public static SftpSession Connect(ConnectRequest request)
    {
        var connectionInfo = SshConnectionInfoFactory.Create(request);
        var client = new SftpClient(connectionInfo);
        client.Connect();
        return new SftpSession(Guid.NewGuid().ToString("N"), client, request.Host, request.Port, request.Username);
    }

    // Remote paths are always POSIX-style (the server's own OS, not this SFTP client's),
    // regardless of what OS slopterm itself is running on.
    public FsListing ListDirectory(string? path)
    {
        var target = string.IsNullOrEmpty(path) ? HomeDirectory : path;
        var entries = _client.ListDirectory(target)
            .Where(e => e.Name != "." && e.Name != "..")
            .Select(e => new FsEntry(e.Name, e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FsListing(target, ComputePosixParent(target), entries);
    }

    /// <summary>Uploads a local file into a remote directory, keeping its original file name.</summary>
    public async Task UploadFileAsync(string localPath, string remoteDir, CancellationToken ct)
    {
        var remotePath = JoinPosixPath(remoteDir, Path.GetFileName(localPath));
        await using var stream = File.OpenRead(localPath);
        await _client.UploadFileAsync(stream, remotePath, ct);
    }

    /// <summary>
    /// Writes raw bytes to a remote directory under the given file name, returning the full
    /// remote path they landed at. Backs the SSH tab's paste/drag-to-upload flow, where the
    /// bytes come straight from the browser (a pasted image, an OS-dropped file) rather than
    /// from a local file on disk like <see cref="UploadFileAsync"/>.
    /// </summary>
    public async Task<string> WriteBytesAsync(string remoteDir, string fileName, byte[] data, CancellationToken ct)
    {
        var remotePath = JoinPosixPath(remoteDir, fileName);
        using var stream = new MemoryStream(data);
        await _client.UploadFileAsync(stream, remotePath, ct);
        return remotePath;
    }

    /// <summary>Uploads raw file bytes (an OS-dragged file, which only exists in the browser
    /// as bytes - no path on disk) into a remote directory under the given file name.</summary>
    public async Task UploadBytesAsync(Stream content, string fileName, string remoteDir, CancellationToken ct)
    {
        var remotePath = JoinPosixPath(remoteDir, Path.GetFileName(fileName));
        await _client.UploadFileAsync(content, remotePath, ct);
    }

    /// <summary>Downloads a remote file into a local directory, keeping its original file name.</summary>
    public async Task DownloadFileAsync(string remotePath, string localDir, CancellationToken ct)
    {
        var fileName = remotePath.TrimEnd('/').Split('/').Last();
        var localPath = Path.Combine(localDir, fileName);
        await using var stream = File.Create(localPath);
        await _client.DownloadFileAsync(remotePath, stream, ct);
    }

    /// <summary>
    /// Recursively walks a remote directory tree, yielding one entry per file with its path
    /// relative to <paramref name="remoteRoot"/> (POSIX '/'-joined). Backs DirectorySyncService's
    /// reconciliation - directories are descended into but not themselves yielded. Returns an
    /// empty list if the root doesn't exist yet, so a first-ever sync into a fresh tree is fine.
    /// </summary>
    public IReadOnlyList<RemoteFile> WalkFiles(string remoteRoot)
    {
        var files = new List<RemoteFile>();
        if (!_client.Exists(remoteRoot))
        {
            return files;
        }

        WalkFilesInto(remoteRoot, "", files);
        return files;
    }

    private void WalkFilesInto(string remoteDir, string relativePrefix, List<RemoteFile> files)
    {
        foreach (var entry in _client.ListDirectory(remoteDir).Where(e => e.Name != "." && e.Name != ".."))
        {
            var relative = relativePrefix.Length == 0 ? entry.Name : relativePrefix + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                WalkFilesInto(entry.FullName, relative, files);
            }
            else
            {
                files.Add(new RemoteFile(relative, entry.Length, entry.LastWriteTimeUtc));
            }
        }
    }

    /// <summary>Attributes of a single remote file (size/mtime), or null if it doesn't exist.</summary>
    public RemoteFile? TryGetFile(string remotePath)
    {
        if (!_client.Exists(remotePath))
        {
            return null;
        }

        var attrs = _client.GetAttributes(remotePath);
        return attrs.IsDirectory ? null : new RemoteFile(remotePath, attrs.Size, attrs.LastWriteTimeUtc);
    }

    /// <summary>
    /// Uploads a local file to an exact remote path (not just into a directory), creating any
    /// missing parent directories first and stamping the remote mtime to match the source so a
    /// later size/mtime reconciliation sees them as equal. Used by DirectorySyncService.
    /// </summary>
    public async Task UploadToPathAsync(string localPath, string remotePath, CancellationToken ct)
    {
        EnsureRemoteDirectory(ComputePosixParent(remotePath));
        await using (var stream = File.OpenRead(localPath))
        {
            await _client.UploadFileAsync(stream, remotePath, ct);
        }

        var attrs = _client.GetAttributes(remotePath);
        attrs.LastWriteTime = File.GetLastWriteTimeUtc(localPath);
        _client.SetAttributes(remotePath, attrs);
    }

    /// <summary>
    /// Downloads an exact remote path to an exact local path, creating any missing parent
    /// directories and stamping the local mtime to match the remote source. Used by
    /// DirectorySyncService.
    /// </summary>
    public async Task DownloadToPathAsync(string remotePath, string localPath, DateTime remoteModifiedUtc, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await using (var stream = File.Create(localPath))
        {
            await _client.DownloadFileAsync(remotePath, stream, ct);
        }

        File.SetLastWriteTimeUtc(localPath, remoteModifiedUtc);
    }

    // Creates every directory along a remote path that doesn't exist yet (mkdir -p), so an
    // upload into a not-yet-mirrored subtree can't fail on a missing parent.
    private void EnsureRemoteDirectory(string? remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir) || _client.Exists(remoteDir))
        {
            return;
        }

        EnsureRemoteDirectory(ComputePosixParent(remoteDir));
        _client.CreateDirectory(remoteDir);
    }

    public static string JoinPosix(string dir, string relative) => JoinPosixPath(dir, relative);

    /// <summary>Renames (or moves within the same parent) a remote file or directory to a new leaf name.</summary>
    public void Rename(string path, string newName)
    {
        var parent = ComputePosixParent(path) ?? "/";
        _client.RenameFile(path, JoinPosixPath(parent, newName));
    }

    /// <summary>Deletes a remote file or directory (directories are removed recursively).</summary>
    public void Delete(string path)
    {
        if (_client.GetAttributes(path).IsDirectory)
        {
            DeleteDirectoryRecursive(path);
        }
        else
        {
            _client.DeleteFile(path);
        }
    }

    /// <summary>Creates a new directory under the given remote parent directory.</summary>
    public void MakeDirectory(string parentDir, string name) => _client.CreateDirectory(JoinPosixPath(parentDir, name));

    // SSH.NET's DeleteDirectory only removes an empty directory, so drain the children first.
    private void DeleteDirectoryRecursive(string path)
    {
        foreach (var entry in _client.ListDirectory(path).Where(e => e.Name != "." && e.Name != ".."))
        {
            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(entry.FullName);
            }
            else
            {
                _client.DeleteFile(entry.FullName);
            }
        }

        _client.DeleteDirectory(path);
    }

    private static string JoinPosixPath(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private static string? ComputePosixParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null; // already root
        }

        var idx = trimmed.LastIndexOf('/');
        if (idx < 0)
        {
            return null;
        }

        return idx == 0 ? "/" : trimmed[..idx];
    }

    public void Dispose()
    {
        if (_client.IsConnected)
        {
            _client.Disconnect();
        }

        _client.Dispose();
    }
}
