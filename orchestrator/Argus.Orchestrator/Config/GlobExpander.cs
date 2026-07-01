using Argus.Orchestrator.Ha;
using System.IO.Enumeration;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Pure static glob expander: resolves include/exclude glob patterns against a sensor snapshot
/// using the authoritative CONTEXT combine model.
///
/// Uses <see cref="FileSystemName.MatchesSimpleExpression"/> (BCL, net8.0) for glob matching —
/// supports <c>*</c> and <c>?</c> wildcards, case-insensitive. Zero new NuGet dependencies.
///
/// Combine model (authoritative — matches CONTEXT.md):
///   resolved = (entities matching include-globs − entities matching exclude-globs)
///              ∪ manually-checked − manually-unchecked
///   - No include patterns → ALL snapshot entities are the base candidate set.
///   - Excludes remove from the pattern-selected set.
///   - manually-checked are ADDED after excludes (manual check overrides an exclusion).
///   - manually-unchecked are REMOVED last (manual uncheck overrides an inclusion). Manual wins.
/// </summary>
public static class GlobExpander
{
    /// <summary>
    /// Resolves the entity selection from glob patterns and manual overrides.
    /// </summary>
    /// <param name="snapshot">Current live sensor snapshot (source of valid entity_ids).</param>
    /// <param name="includePatterns">Glob patterns for entities to include. Empty = include all.</param>
    /// <param name="excludePatterns">Glob patterns for entities to exclude from the include set.</param>
    /// <param name="manuallyChecked">Entity ids explicitly checked by the user; added after excludes.</param>
    /// <param name="manuallyUnchecked">Entity ids explicitly unchecked by the user; removed last.</param>
    /// <returns>
    /// A <see cref="HashSet{T}"/> (OrdinalIgnoreCase) of resolved entity_ids.
    /// </returns>
    public static HashSet<string> Resolve(
        IReadOnlyList<HaSensorEntry> snapshot,
        IEnumerable<string> includePatterns,
        IEnumerable<string> excludePatterns,
        IEnumerable<string> manuallyChecked,
        IEnumerable<string> manuallyUnchecked)
    {
        // Step 1: all entity ids from snapshot into a case-insensitive set
        var allIds = snapshot
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 2: filter out empty/whitespace patterns
        var includes = includePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var excludes = excludePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // Step 3: build pattern-selected set
        //   No include patterns → all entities are the base candidate set.
        //   Otherwise → only entities matching at least one include pattern.
        HashSet<string> patternSelected;
        if (includes.Count == 0)
        {
            patternSelected = new HashSet<string>(allIds, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            patternSelected = allIds
                .Where(id => includes.Any(p =>
                    FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Step 4: remove entities matching any exclude pattern (applied to allIds for correctness)
        foreach (var id in allIds.Where(id =>
            excludes.Any(p => FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true))))
        {
            patternSelected.Remove(id);
        }

        // Step 5: add manually-checked entities (overrides exclusion)
        // Only IDs present in the live snapshot are accepted — rejects arbitrary form-submitted strings
        // that do not correspond to a real HA entity (WR-03).
        foreach (var id in manuallyChecked)
        {
            if (!string.IsNullOrWhiteSpace(id) && allIds.Contains(id))
                patternSelected.Add(id);
        }

        // Step 6: remove manually-unchecked entities LAST (final override — manual uncheck wins)
        // Constrained to snapshot members for symmetry; removing an absent id is a no-op anyway.
        foreach (var id in manuallyUnchecked)
        {
            if (!string.IsNullOrWhiteSpace(id) && allIds.Contains(id))
                patternSelected.Remove(id);
        }

        return patternSelected;
    }
}
