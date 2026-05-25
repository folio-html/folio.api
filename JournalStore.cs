using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Folio.Api;

/// <summary>
/// File-backed store for journal markdown. Each journal is one <c>{name}.md</c>
/// under the data directory. The on-disk file is the canonical artifact — this
/// type only adds ETag-based optimistic concurrency, atomic writes, and a guard
/// against names that could escape the data directory.
/// </summary>
public sealed partial class JournalStore
{
    private readonly string _root;
    private readonly bool _backups;
    // One lock per journal name so a read-compare-write (the If-Match check)
    // can't interleave with another writer for the same file.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public JournalStore(IConfiguration config, IHostEnvironment env)
    {
        var dir = config["Folio:DataDir"] ?? "data";
        _root = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir));
        _backups = config.GetValue("Folio:Backups", true);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Absolute path to the directory journals are stored in.</summary>
    public string Root => _root;

    /// <summary>Probes that the data directory can actually be written to.</summary>
    public bool IsWritable()
    {
        try
        {
            var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NameRegex();

    /// <summary>True for names that are safe to map to a file in the data dir.</summary>
    public static bool IsValidName(string name) =>
        name is not ("." or "..") && NameRegex().IsMatch(name);

    public IReadOnlyList<JournalInfo> List()
    {
        var result = new List<JournalInfo>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.md", SearchOption.TopDirectoryOnly))
        {
            var bytes = File.ReadAllBytes(path);
            result.Add(new JournalInfo(
                Name: Path.GetFileNameWithoutExtension(path),
                Size: bytes.LongLength,
                Modified: File.GetLastWriteTimeUtc(path),
                ETag: ComputeETag(bytes)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    public async Task<(byte[] Bytes, string ETag)?> ReadAsync(string name, CancellationToken ct)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return (bytes, ComputeETag(bytes));
    }

    public async Task<string?> ETagAsync(string name, CancellationToken ct)
    {
        var found = await ReadAsync(name, ct);
        return found?.ETag;
    }

    /// <summary>
    /// Writes the journal, honouring <paramref name="ifMatch"/> as an optimistic
    /// lock. Pass <c>null</c> to skip the check, <c>"*"</c> to require the file
    /// not to exist yet (create-only).
    /// </summary>
    public async Task<WriteResult> WriteAsync(string name, byte[] bytes, string? ifMatch, CancellationToken ct)
    {
        var path = PathFor(name);
        var gate = _locks.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existed = File.Exists(path);
            var current = existed ? ComputeETag(await File.ReadAllBytesAsync(path, ct)) : null;

            if (ifMatch is "*")
            {
                if (existed) return WriteResult.Conflict;          // create-only, but it's there
            }
            else if (ifMatch is not null && ifMatch != current)
            {
                return WriteResult.Conflict;                       // stale version
            }

            if (existed && _backups) Backup(path);
            await WriteAtomicAsync(path, bytes, ct);

            return new WriteResult(existed ? WriteOutcome.Updated : WriteOutcome.Created, ComputeETag(bytes));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        var path = PathFor(name);
        var gate = _locks.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return false;
            if (_backups) Backup(path);
            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── internals ────────────────────────────────────────────────────────────

    private string PathFor(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Invalid journal name.", nameof(name));
        var full = Path.GetFullPath(Path.Combine(_root, name + ".md"));
        // Defence in depth: the regex already forbids separators, but never trust it alone.
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Resolved path escapes the data directory.", nameof(name));
        return full;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);   // atomic rename on the same volume
    }

    private void Backup(string path)
    {
        var dir = Path.Combine(_root, ".backups");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var name = Path.GetFileNameWithoutExtension(path);
        File.Copy(path, Path.Combine(dir, $"{name}-{stamp}.md"), overwrite: false);
    }

    private static string ComputeETag(byte[] bytes) =>
        '"' + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + '"';
}

public enum WriteOutcome { Created, Updated, Conflict }

public readonly record struct WriteResult(WriteOutcome Outcome, string? ETag)
{
    public static readonly WriteResult Conflict = new(WriteOutcome.Conflict, null);
}

public readonly record struct JournalInfo(string Name, long Size, DateTime Modified, string ETag);

/// <summary>Reports the API as healthy only while the data directory is writable.</summary>
public sealed class DataDirectoryHealthCheck(JournalStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(store.IsWritable()
            ? HealthCheckResult.Healthy($"Data directory is writable ({store.Root}).")
            : HealthCheckResult.Unhealthy($"Data directory is not writable ({store.Root})."));
}
