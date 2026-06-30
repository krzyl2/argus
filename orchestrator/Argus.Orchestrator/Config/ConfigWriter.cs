namespace Argus.Orchestrator.Config;

/// <summary>
/// Atomic YAML config writer: temp-file + File.Move(overwrite:true) serialized
/// by SemaphoreSlim(1,1). POSIX rename() is atomic — readers always see a
/// complete file, never a partial write during a concurrent FileSystemWatcher event.
///
/// Does NOT serialize YAML — writes the provided string verbatim.
/// YAML serialization is a Phase 2+ caller concern.
/// DI registration is deferred to Plan 02 (Program.cs) to avoid a file conflict.
/// </summary>
public sealed class ConfigWriter
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(string targetPath, string yaml,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(targetPath)!;
            tmp = Path.Combine(dir, $".entities.tmp.{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, targetPath, overwrite: true); // atomic POSIX rename
            tmp = null; // Move succeeded — do not delete
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
            _lock.Release();
        }
    }
}
