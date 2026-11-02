using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Threading.Channels;
using PhotoScope.Models;

namespace PhotoScope.Services;

public sealed class ImportOptions
{
    public required IReadOnlyList<string> Sources { get; init; }
    public bool Recursive { get; init; } = true;
    public ImportMode Mode { get; init; } = ImportMode.ReferenceInPlace;
    public string LibraryFolder { get; init; } = "";
    public bool SkipDuplicates { get; init; } = true;
}

public sealed record ImportProgress(bool Scanning, int Found, int Processed, int Imported, int Duplicates, int AlreadyInLibrary, int Failed);

public sealed class ImportResult
{
    public int Found { get; set; }
    public int Imported { get; set; }
    public int Duplicates { get; set; }
    public int AlreadyInLibrary { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Imports files in the background: enumerate, hash, read metadata, optionally copy into the
/// library folder (never overwriting anything), build a thumbnail, then insert into the
/// database in batches. Originals are only ever read.
/// </summary>
public sealed class ImportService
{
    readonly LibraryDb _db;
    readonly ThumbnailCache _thumbs;

    public ImportService(LibraryDb db, ThumbnailCache thumbs)
    {
        _db = db;
        _thumbs = thumbs;
    }

    /// <param name="onBatch">Called on a background thread with each batch of newly inserted photos.</param>
    public Task<ImportResult> RunAsync(ImportOptions options, IProgress<ImportProgress> progress, Action<IReadOnlyList<Photo>> onBatch, CancellationToken ct) =>
        Task.Run(() => RunCoreAsync(options, progress, onBatch, ct), CancellationToken.None);

    async Task<ImportResult> RunCoreAsync(ImportOptions o, IProgress<ImportProgress> progress, Action<IReadOnlyList<Photo>> onBatch, CancellationToken ct)
    {
        var result = new ImportResult();
        int found = 0, processed = 0, imported = 0, duplicates = 0, already = 0, failed = 0;
        var scanning = true;
        void Report() => progress.Report(new ImportProgress(Volatile.Read(ref scanning), Volatile.Read(ref found), Volatile.Read(ref processed),
            Volatile.Read(ref imported), Volatile.Read(ref duplicates), Volatile.Read(ref already), Volatile.Read(ref failed)));

        using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = Task.Run(async () =>
        {
            try
            {
                while (!reporterCts.IsCancellationRequested)
                {
                    Report();
                    await Task.Delay(150, reporterCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        var errors = new ConcurrentQueue<string>();
        try
        {
            // 1. Find files.
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in o.Sources)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var file in Enumerate(source, o.Recursive, ct, errors))
                {
                    if (seen.Add(file))
                    {
                        files.Add(file);
                        Interlocked.Increment(ref found);
                    }
                }
            }
            Volatile.Write(ref scanning, false);
            result.Found = files.Count;
            Log.Info($"Import: {files.Count} candidate files, mode {o.Mode}, skip duplicates {o.SkipDuplicates}");

            // 2. Process in parallel; a single writer inserts batches into the database.
            var knownPaths = _db.GetAllPaths();
            var knownHashes = _db.GetAllHashes();
            var batchHashes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var importedAt = DateTime.UtcNow;
            if (o.Mode == ImportMode.CopyToLibrary && AppSettings.ValidateLibraryFolder(o.LibraryFolder) is { } folderError)
                throw new UserFacingException("The library folder can't be used: " + folderError);
            var copyRoot = o.Mode == ImportMode.CopyToLibrary ? Path.GetFullPath(o.LibraryFolder) : null;
            if (copyRoot != null) Directory.CreateDirectory(copyRoot);

            var channel = Channel.CreateBounded<Photo>(new BoundedChannelOptions(512) { SingleReader = true });
            var writer = Task.Run(() => WriteBatchesAsync(channel.Reader, onBatch, () => Interlocked.Increment(ref imported)));

            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                CancellationToken = ct,
            };
            try
            {
                await Parallel.ForEachAsync(files, parallel, async (path, token) =>
                {
                    try
                    {
                        var outcome = ProcessFile(path, o, copyRoot, knownPaths, knownHashes, batchHashes, importedAt, token, out var photo);
                        switch (outcome)
                        {
                            case Outcome.Imported:
                                await channel.Writer.WriteAsync(photo!, CancellationToken.None);
                                break;
                            case Outcome.Duplicate:
                                Interlocked.Increment(ref duplicates);
                                break;
                            case Outcome.AlreadyInLibrary:
                                Interlocked.Increment(ref already);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        var msg = $"{path}: {(ex is UserFacingException ? ex.Message : Friendly.Describe(ex))}";
                        if (errors.Count < 200) errors.Enqueue(msg);
                        Log.Warn("Import failed: " + msg, ex);
                    }
                    finally
                    {
                        Interlocked.Increment(ref processed);
                    }
                });
            }
            finally
            {
                channel.Writer.TryComplete();
                await writer;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
            Log.Info("Import cancelled by user");
        }
        finally
        {
            Volatile.Write(ref scanning, false);
            reporterCts.Cancel();
            await reporter;
            Report();
        }

        result.Imported = imported;
        result.Duplicates = duplicates;
        result.AlreadyInLibrary = already;
        result.Failed = failed;
        result.Errors.AddRange(errors);
        Log.Info($"Import finished: {imported} imported, {duplicates} duplicates, {already} already in library, {failed} failed, cancelled={result.Cancelled}");
        return result;
    }

    enum Outcome { Imported, Duplicate, AlreadyInLibrary }

    Outcome ProcessFile(string path, ImportOptions o, string? copyRoot, HashSet<string> knownPaths, HashSet<string> knownHashes,
        ConcurrentDictionary<string, byte> batchHashes, DateTime importedAt, CancellationToken ct, out Photo? photo)
    {
        photo = null;
        if (knownPaths.Contains(path)) return Outcome.AlreadyInLibrary;

        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("File vanished during import", path);
        if (fi.Length == 0) throw new UserFacingException("The file is empty.");

        var hash = HashFile(path, ct);
        var isNewHash = batchHashes.TryAdd(hash, 0);
        if (o.SkipDuplicates && (knownHashes.Contains(hash) || !isNewHash))
            return Outcome.Duplicate;

        ImageInfo? info = null;
        string? decodeError = null;
        try
        {
            info = ImageDecoder.ReadInfo(path);
        }
        catch (Exception ex)
        {
            // Only remember errors that will recur; temporary ones are retried when the thumbnail loads.
            if (Friendly.IsPermanentImageError(ex)) decodeError = Friendly.DescribeImageError(ex, path);
            Log.Warn($"Import: could not read image info for {path}: {decodeError}", ex);
        }

        var finalPath = path;
        if (copyRoot != null && !AppPaths.IsUnder(path, copyRoot))
        {
            var date = info?.DateTaken ?? fi.LastWriteTime;
            var folder = Path.Combine(copyRoot, date.Year.ToString("0000"), date.Month.ToString("00"));
            finalPath = FileOps.CopyNoOverwrite(path, folder);
            fi = new FileInfo(finalPath);
        }

        photo = new Photo
        {
            Path = finalPath,
            SizeBytes = fi.Length,
            ModifiedUtc = fi.LastWriteTimeUtc,
            Hash = hash,
            PixelWidth = info?.PixelWidth ?? 0,
            PixelHeight = info?.PixelHeight ?? 0,
            Orientation = info?.Orientation ?? 1,
            DateTaken = info?.DateTaken,
            CameraModel = info?.CameraModel,
            DateImportedUtc = importedAt,
            DecodeError = decodeError,
        };

        if (decodeError == null && !_thumbs.Exists(photo))
        {
            try
            {
                _thumbs.Generate(photo);
            }
            catch (Exception ex)
            {
                if (Friendly.IsPermanentImageError(ex)) photo.DecodeError = Friendly.DescribeImageError(ex, finalPath);
                Log.Warn($"Import: thumbnail failed for {finalPath}: {photo.DecodeError}", ex);
            }
        }
        return Outcome.Imported;
    }

    async Task WriteBatchesAsync(ChannelReader<Photo> reader, Action<IReadOnlyList<Photo>> onBatch, Action countImported)
    {
        var batch = new List<Photo>();
        while (await reader.WaitToReadAsync())
        {
            // Gather what's ready, wait briefly for more, then flush: keeps the gallery live without per-item churn.
            var deadline = DateTime.UtcNow.AddMilliseconds(250);
            while (batch.Count < 200)
            {
                while (batch.Count < 200 && reader.TryRead(out var p)) batch.Add(p);
                var remaining = deadline - DateTime.UtcNow;
                if (batch.Count >= 200 || remaining <= TimeSpan.Zero) break;
                using var waitCts = new CancellationTokenSource(remaining);
                try
                {
                    if (!await reader.WaitToReadAsync(waitCts.Token)) break;
                }
                catch (OperationCanceledException) { break; }
            }
            Flush();
        }
        Flush();

        void Flush()
        {
            if (batch.Count == 0) return;
            try
            {
                _db.InsertPhotos(batch);
                var inserted = batch.Where(p => p.Id > 0).ToList();
                foreach (var _ in inserted) countImported();
                if (inserted.Count > 0) onBatch(inserted);
            }
            catch (Exception ex)
            {
                Log.Error("Import: database insert failed", ex);
            }
            batch = new List<Photo>();
        }
    }

    static string HashFile(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static string HashFileForCompare(string path) => HashFile(path, CancellationToken.None);

    static IEnumerable<string> Enumerate(string source, bool recursive, CancellationToken ct, ConcurrentQueue<string> errors)
    {
        string full;
        try { full = Path.GetFullPath(source); }
        catch (Exception ex)
        {
            errors.Enqueue($"{source}: {Friendly.Describe(ex)}");
            yield break;
        }

        if (File.Exists(full))
        {
            if (ImageDecoder.IsSupported(full) && !AppPaths.IsUnder(full, AppPaths.Root)) yield return full;
            yield break;
        }
        if (!Directory.Exists(full))
        {
            errors.Enqueue($"{source}: not found");
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        // Walk directories ourselves so junctions/symlinks are never followed (no loops) and the
        // app's own data folder is never imported.
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            if (AppPaths.IsUnder(dir, AppPaths.Root)) continue;

            List<string> filesHere;
            List<string> subdirs = new();
            try
            {
                filesHere = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && ImageDecoder.IsSupported(e.FileName.ToString()),
                }.ToList();
                if (recursive)
                {
                    subdirs = new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), options)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) =>
                            e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
                    }.ToList();
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue($"{dir}: {Friendly.Describe(ex)}");
                continue;
            }

            filesHere.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in filesHere) yield return f;
            subdirs.Sort(StringComparer.OrdinalIgnoreCase);
            for (var i = subdirs.Count - 1; i >= 0; i--) pending.Push(subdirs[i]);
        }
    }
}
