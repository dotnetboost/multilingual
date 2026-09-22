using DotNetBoost.MultiLingual.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace DotNetBoost.MultiLingual.Localization;

/// <summary>
/// The message files, one per language, each loaded on first use and flattened into a
/// dictionary of dotted keys: <c>{"Product": {"Created": "…"}}</c> becomes <c>Product.Created</c>.
/// A change to the folder drops everything loaded, so the next lookup reads the files again.
/// </summary>
internal sealed partial class JsonResourceStore : IDisposable
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling     = JsonCommentHandling.Skip
    };

    private readonly IFileProvider _files;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly IDisposable? _ownedProvider;
    private readonly IDisposable? _watch;
    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;
    private bool _warnedAboutEmptyFolder;

    public JsonResourceStore(IFileProvider files, JsonLocalizationOptions options, ILogger<JsonResourceStore> logger, bool ownsProvider = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);

        _files         = files;
        _path          = (options.ResourcesPath ?? string.Empty).Replace('\\', '/').Trim('/');
        _logger        = logger;
        _ownedProvider = ownsProvider ? files as IDisposable : null;

        if (options.ReloadOnChange)
            _watch = ChangeToken.OnChange(() => _files.Watch(Combine("*.json")), Reset);
    }

    /// <summary>The flattened messages for <paramref name="code"/>, or null when it has no file.</summary>
    /// <exception cref="InvalidOperationException">The file is malformed; the message names it.</exception>
    public IReadOnlyDictionary<string, string>? Get(string code)
    {
        var snapshot = Current();
        return snapshot.Files.TryGetValue(code, out var file)
            ? snapshot.Loaded.GetOrAdd(file.Code, _ => new Lazy<FrozenDictionary<string, string>>(() => Load(file))).Value
            : null;
    }

    /// <summary>Where <paramref name="code"/>'s file is, or would be — for <c>SearchedLocation</c>.</summary>
    public string Describe(string code) =>
        Current().Files.TryGetValue(code, out var file) ? file.DisplayPath : Combine(code + ".json");

    /// <summary>Loads every file now, reporting all the malformed ones at once.</summary>
    /// <exception cref="InvalidOperationException">At least one file is malformed.</exception>
    public void LoadAll()
    {
        var errors = new List<string>();
        foreach (var code in Current().Files.Keys)
        {
            try
            {
                _ = Get(code);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count == 0)
            return;

        var sb = new StringBuilder("Invalid localization files:");
        foreach (var error in errors)
            sb.AppendLine().Append("  - ").Append(error);

        throw new InvalidOperationException(sb.ToString());
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _ownedProvider?.Dispose();
    }

    private void Reset() => _snapshot = null;

    private Snapshot Current()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
            return snapshot;

        lock (_gate)
        {
            return _snapshot ??= Scan();
        }
    }

    /// <summary>
    /// Lists the folder once and keys each file by its canonical language code, so <c>pt-br.json</c>
    /// serves <c>pt-BR</c> even on a case-sensitive file system.
    /// </summary>
    private Snapshot Scan()
    {
        var files    = new Dictionary<string, ResourceFile>(StringComparer.OrdinalIgnoreCase);
        var contents = _files.GetDirectoryContents(_path);

        if (contents.Exists)
        {
            foreach (var entry in contents)
            {
                if (entry.IsDirectory || !entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var displayPath = Combine(entry.Name);
                if (!LanguageCodes.TryNormalize(entry.Name[..^".json".Length], out var code))
                {
                    LogNotALanguage(displayPath);
                    continue;
                }

                if (files.TryGetValue(code, out var existing))
                {
                    throw new InvalidOperationException(
                        $"{existing.DisplayPath} and {displayPath} are both messages for '{code}'. Keep one of them.");
                }

                files[code] = new ResourceFile(code, entry, displayPath);
            }
        }

        if (files.Count == 0 && !_warnedAboutEmptyFolder)
        {
            _warnedAboutEmptyFolder = true;
            LogNoFiles(string.IsNullOrEmpty(_path) ? "/" : _path);
        }

        return new Snapshot(files);
    }

    private static FrozenDictionary<string, string> Load(ResourceFile file)
    {
        try
        {
            using var stream   = file.Info.CreateReadStream();
            using var document = JsonDocument.Parse(stream, ParseOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{file.DisplayPath}: the top level must be a JSON object.");

            var messages = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(document.RootElement, prefix: null, messages, file.DisplayPath);
            return messages.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file.DisplayPath}: not valid JSON — {ex.Message}", ex);
        }
    }

    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> messages, string displayPath)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, messages, displayPath);
                    break;

                case JsonValueKind.String:
                    // Both "A.B" and {"A": {"B": …}} flatten to A.B; silently keeping one would
                    // make which one wins depend on the order they are written in.
                    if (!messages.TryAdd(key, property.Value.GetString()!))
                        throw new InvalidOperationException($"{displayPath}: '{key}' is defined more than once.");
                    break;

                default:
                    throw new InvalidOperationException(
                        $"{displayPath}: '{key}' must be a string or an object of strings, not {property.Value.ValueKind}.");
            }
        }
    }

    private string Combine(string name) => string.IsNullOrEmpty(_path) ? name : $"{_path}/{name}";

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No localization files were found in '{Path}'. Every message will be returned as its key. " +
                  "Check ResourcesPath, and that the files are copied to the output directory.")]
    private partial void LogNoFiles(string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ignoring {File}: its name is not a language code. Name files after the language, e.g. 'en.json' or 'pt-BR.json'.")]
    private partial void LogNotALanguage(string file);

    private sealed record ResourceFile(string Code, IFileInfo Info, string DisplayPath);

    private sealed class Snapshot(Dictionary<string, ResourceFile> files)
    {
        public Dictionary<string, ResourceFile> Files { get; } = files;

        public ConcurrentDictionary<string, Lazy<FrozenDictionary<string, string>>> Loaded { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
