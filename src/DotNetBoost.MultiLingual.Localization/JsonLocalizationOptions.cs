using Microsoft.Extensions.FileProviders;

namespace DotNetBoost.MultiLingual.Localization;

/// <summary>
/// Where the JSON message files live and how they are loaded. Configured through
/// <c>AddJsonLocalization(o =&gt; …)</c>.
/// </summary>
public sealed class JsonLocalizationOptions
{
    /// <summary>
    /// The folder holding one <c>{language}.json</c> file per language, relative to
    /// <see cref="FileProvider"/>. Default: <c>Resources</c>. An empty string means the provider's root.
    /// </summary>
    public string ResourcesPath { get; set; } = "Resources";

    /// <summary>
    /// Where the files are read from. Default: the application's content root
    /// (<c>IHostEnvironment.ContentRootFileProvider</c>), or the application's base directory when
    /// there is no host. Set it to an <c>EmbeddedFileProvider</c> to ship the files inside an assembly.
    /// </summary>
    public IFileProvider? FileProvider { get; set; }

    /// <summary>
    /// Whether an edited, added or removed file is picked up without a restart. Default:
    /// <c>true</c>. Only takes effect for providers that report changes, such as the physical one.
    /// </summary>
    public bool ReloadOnChange { get; set; } = true;

    /// <summary>
    /// Whether every file is parsed when the application starts, so a malformed one fails the
    /// start rather than the first request in that language. Default: <c>true</c>.
    /// </summary>
    public bool ValidateFilesAtStartup { get; set; } = true;
}
