using DotNetBoost.MultiLingual.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DotNetBoost.MultiLingual.Core;

/// <summary>Validation and canonicalisation of language codes.</summary>
public static partial class LanguageCodes
{
    /// <summary>Longest code accepted, e.g. <c>zh-Hant</c> fits comfortably.</summary>
    public const int MaxLength = 16;

    // Language parts that are written right to left. Only consulted when the runtime has no
    // culture data (invariant globalization mode, common in slim container images).
    private static readonly HashSet<string> RightToLeftLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "arc", "ckb", "dv", "fa", "he", "ku", "ps", "sd", "syr", "ug", "ur", "yi"
    };

    private static readonly Lazy<bool> CultureDataAvailable = new(ProbeCultureData);

    /// <summary>
    /// Validates and canonicalises a code: lowercase language part, uppercase region
    /// (<c>pt-BR</c>). The shape is checked with a regex and then confirmed against the runtime's
    /// culture data, so <c>zz</c> is rejected even though it looks well-formed. When the runtime
    /// runs in invariant globalization mode there is no culture data to confirm against, and the
    /// shape check alone decides.
    /// </summary>
    /// <exception cref="InvalidLanguageException">The code is malformed or unknown.</exception>
    public static string Normalize(string? rawCode)
    {
        var code = rawCode?.Trim() ?? string.Empty;

        if (!LanguageCodePattern().IsMatch(code))
        {
            throw new InvalidLanguageException(
                $"'{rawCode}' is not a valid language code. Use an ISO 639-1 code, optionally with a " +
                "region: 'en', 'ar', 'pt-BR'.");
        }

        var parts = code.Split('-');
        var canonical = parts.Length == 2
            ? $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}"
            : parts[0].ToLowerInvariant();

        if (CultureDataAvailable.Value && !IsKnownCulture(canonical))
            throw new InvalidLanguageException($"'{rawCode}' is not a language code this runtime recognises.");

        return canonical;
    }

    /// <summary>Non-throwing <see cref="Normalize"/>.</summary>
    public static bool TryNormalize(string? rawCode, out string canonical)
    {
        try
        {
            canonical = Normalize(rawCode);
            return true;
        }
        catch (InvalidLanguageException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// The direction the runtime's culture data records for an already-normalised code, or a
    /// built-in list of right-to-left languages when there is no culture data.
    /// </summary>
    public static TextDirection InferDirection(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (CultureDataAvailable.Value)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code).TextInfo.IsRightToLeft ? TextDirection.Rtl : TextDirection.Ltr;
            }
            catch (CultureNotFoundException)
            {
                // Fall through to the built-in list.
            }
        }

        var separator = code.IndexOf('-', StringComparison.Ordinal);
        var language  = separator > 0 ? code[..separator] : code;
        return RightToLeftLanguages.Contains(language) ? TextDirection.Rtl : TextDirection.Ltr;
    }

    private static bool IsKnownCulture(string code)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// With InvariantGlobalization on, .NET refuses every culture but the invariant one, which
    /// would make every code look unknown. Detect that once and degrade to the shape check.
    /// </summary>
    private static bool ProbeCultureData()
    {
        try
        {
            return !string.IsNullOrEmpty(CultureInfo.GetCultureInfo("en-US").Name);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[a-zA-Z]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();
}
