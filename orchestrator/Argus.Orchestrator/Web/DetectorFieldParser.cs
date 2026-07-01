using Argus.Orchestrator.Config;
using System.Text.RegularExpressions;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Parses indexed detector form fields from a POST body into a structured dictionary.
///
/// Form encoding for detector fields:
///   detectors[{ei}][{di}][name] = {detector_type}
///   detectors[{ei}][{di}][params][{key}] = {value}
///
/// {ei} = 0-based entity index (correlates to the entity's position in the sorted
///        submitted entity list — alphabetical by EntityId, same order as BuildFullPage renders).
/// {di} = 0-based detector index within the entity.
///
/// T-03-10: Param values are stored as-is (strings); YamlDotNet escapes special chars
/// during serialization. HtmlEncode is applied only at render time (T-03-11).
/// </summary>
internal static partial class DetectorFieldParser
{
    // Pattern: detectors[{ei}][{di}][name] OR detectors[{ei}][{di}][params][{key}]
    [GeneratedRegex(@"^detectors\[(\d+)\]\[(\d+)\]\[(.+?)\](?:\[(.+?)\])?$")]
    private static partial Regex DetectorFieldRegex();

    /// <summary>
    /// Parses all detector-related form keys from a flat key→value dictionary.
    /// Returns a dictionary keyed by entity index → ordered list of DetectorConfigs.
    /// Detectors within each entity are ordered by their di index.
    /// </summary>
    /// <param name="formKeys">All form keys with their first submitted value.</param>
    /// <returns>
    /// Dictionary where key = entity index (ei), value = ordered list of DetectorConfig.
    /// If no detector fields are present, returns an empty dictionary.
    /// </returns>
    public static Dictionary<int, List<DetectorConfig>> Parse(
        IEnumerable<KeyValuePair<string, string>> formKeys)
    {
        // Temporary structure: ei → di → (name, params)
        var raw = new Dictionary<int, Dictionary<int, (string Name, Dictionary<string, string> Params)>>();

        var regex = DetectorFieldRegex();
        foreach (var (key, value) in formKeys)
        {
            var match = regex.Match(key);
            if (!match.Success) continue;

            // Use TryParse to skip malformed fields with overflowing digit groups rather than
            // throwing OverflowException (e.g. detectors[2147483648][0][name]=hst — WR-03).
            if (!int.TryParse(match.Groups[1].Value, out var ei)) continue;
            if (!int.TryParse(match.Groups[2].Value, out var di)) continue;
            var field = match.Groups[3].Value;       // "name" or "params"
            var paramKey = match.Groups[4].Value;    // param key if field == "params"

            if (!raw.TryGetValue(ei, out var entityDetectors))
            {
                entityDetectors = new Dictionary<int, (string, Dictionary<string, string>)>();
                raw[ei] = entityDetectors;
            }

            if (!entityDetectors.TryGetValue(di, out var detEntry))
            {
                detEntry = (string.Empty, new Dictionary<string, string>());
                entityDetectors[di] = detEntry;
            }

            if (string.Equals(field, "name", StringComparison.OrdinalIgnoreCase))
            {
                entityDetectors[di] = (value ?? string.Empty, detEntry.Params);
            }
            else if (string.Equals(field, "params", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(paramKey))
            {
                detEntry.Params[paramKey] = value ?? string.Empty;
                entityDetectors[di] = (detEntry.Name, detEntry.Params);
            }
        }

        // Convert raw to ordered DetectorConfig lists
        var result = new Dictionary<int, List<DetectorConfig>>();
        foreach (var (ei, entityDetectors) in raw)
        {
            var detectors = entityDetectors
                .OrderBy(kvp => kvp.Key)  // order by di
                .Select(kvp => new DetectorConfig
                {
                    Name = kvp.Value.Name,
                    Params = kvp.Value.Params,
                })
                .ToList();
            result[ei] = detectors;
        }

        return result;
    }

    /// <summary>
    /// Overload accepting an IFormCollection-compatible read from Microsoft.AspNetCore.Http.
    /// Flattens keys to their first submitted value.
    /// </summary>
    public static Dictionary<int, List<DetectorConfig>> Parse(
        IEnumerable<string> formKeyNames,
        Func<string, string?> getValue)
    {
        var pairs = formKeyNames
            .Select(k => new KeyValuePair<string, string>(k, getValue(k) ?? string.Empty));
        return Parse(pairs);
    }
}
