using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System.Collections;
using System.Text;

namespace DotNetBoost.MultiLingual.TestSupport;

/// <summary>
/// Files held in memory, with a change token that fires on every <see cref="Set"/> or
/// <see cref="Remove"/> — so reload-on-change can be tested without a file system watcher and
/// the timing it brings.
/// </summary>
public sealed class InMemoryFileProvider : IFileProvider, IDisposable
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource _changed = new();

    /// <summary>Creates the provider with <paramref name="files"/>, keyed by path ("Resources/en.json").</summary>
    public InMemoryFileProvider(IDictionary<string, string>? files = null)
    {
        foreach (var (path, content) in files ?? new Dictionary<string, string>())
            _files[Normalize(path)] = content;
    }

    /// <summary>Adds or replaces a file and signals the change.</summary>
    public void Set(string path, string content)
    {
        lock (_gate)
            _files[Normalize(path)] = content;
        Signal();
    }

    /// <summary>Removes a file and signals the change.</summary>
    public void Remove(string path)
    {
        lock (_gate)
            _files.Remove(Normalize(path));
        Signal();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
            _changed.Dispose();
    }

    /// <inheritdoc/>
    public IFileInfo GetFileInfo(string subpath)
    {
        lock (_gate)
        {
            var path = Normalize(subpath);
            return _files.TryGetValue(path, out var content)
                ? new MemoryFile(path, content)
                : new NotFoundFileInfo(path);
        }
    }

    /// <inheritdoc/>
    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        lock (_gate)
        {
            var folder = Normalize(subpath);
            var prefix = folder.Length == 0 ? string.Empty : folder + "/";
            var files  = _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
                .Select(f => (IFileInfo)new MemoryFile(f.Key, f.Value))
                .ToList();

            return files.Count == 0 ? NotFoundDirectoryContents.Singleton : new Contents(files);
        }
    }

    /// <inheritdoc/>
    public IChangeToken Watch(string filter)
    {
        lock (_gate)
            return new CancellationChangeToken(_changed.Token);
    }

    private void Signal()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private sealed class MemoryFile(string path, string content) : IFileInfo
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public bool Exists => true;
        public long Length => _bytes.Length;
        public string? PhysicalPath => null;
        public string Name => path[(path.LastIndexOf('/') + 1)..];
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(_bytes, writable: false);
    }

    private sealed class Contents(List<IFileInfo> files) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => files.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
