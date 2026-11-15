using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace LibPs3Kit;

/// <summary>How a conversion runs.</summary>
public sealed record Ps3Options
{
    /// <summary>Read the output back and check it against what was written (and that its files decrypted right).</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Replace an existing output; otherwise an existing output is an error.</summary>
    public bool Overwrite { get; init; }

    /// <summary>JB folder: copy PS3_UPDATE (the console firmware update on the disc). RPCS3 doesn't use it.</summary>
    public bool IncludeUpdate { get; init; } = true;

    /// <summary>
    /// ISO conversions (<see cref="Ps3Converter.ToDecryptedIso"/>, <see cref="Ps3Converter.ToEncryptedIso"/>): called
    /// with every byte of the source image as stored, in order, once each, so its hashes can be worked out on the
    /// same read instead of a second one. The span is only good during the call. A JB folder reads the files only,
    /// not the whole image, so it never calls this.
    /// </summary>
    public SourceBytesHandler? SourceBytes { get; init; }

    /// <summary>
    /// JB folder: how many small files are extracted at once. 1 (the default) takes every file in disc order, which
    /// suits a hard drive; 4 or so is much faster on an SSD, where opening and closing each file is the slow part.
    /// </summary>
    public int MaxParallelFiles { get; init; } = 1;

    /// <summary>How much is read at a time (and the size up to which a file counts as small). Tests make it tiny.</summary>
    internal int ChunkSize { get; init; } = 4 << 20;
}

/// <summary>Receives a source image's bytes as stored (see <see cref="Ps3Options.SourceBytes"/>).</summary>
public delegate void SourceBytesHandler(ReadOnlySpan<byte> bytes);

/// <summary>What a conversion's check found.</summary>
public sealed record Ps3Verification(bool Success, IReadOnlyList<string> Problems);

/// <summary>What a conversion made: the output (an ISO's path or a folder), how many bytes, and its check.</summary>
public sealed record Ps3Result(string Output, long BytesWritten, int FilesWritten, Ps3Verification? Verification, string? TitleId, string? Title);

/// <summary>
/// PS3 disc conversions. Each writes to "&lt;output&gt;.partial", checks it when asked, and only then gives it its
/// name, so a failed or cancelled run never leaves a half-written output under the real name.
/// <list type="bullet">
/// <item><see cref="ToDecryptedIso"/>: a Redump image becomes a decrypted ISO (same size and layout; RPCS3 loads it).</item>
/// <item><see cref="ToJbFolder"/>: the disc's files in a folder (PS3_DISC.SFB, PS3_GAME/…), the "JB" layout.</item>
/// <item><see cref="ToEncryptedIso"/>: a decrypted ISO back to the Redump image, byte for byte.</item>
/// </list>
/// </summary>
public static class Ps3Converter
{
    private const int BufferSize = 1 << 20;

    public static Ps3Result ToDecryptedIso(string source, string output, DiscKey? key, Ps3Options? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new();
        var report = new ThrottledProgress(progress);
        using var disc = OpenChecked(source, key, report);
        if (disc.State is EncryptionState.Decrypted or EncryptionState.NoEncryptedRegions) key = null;
        using var view = disc.OpenDecrypted(key);
        return WriteImage(disc, view, output, options, report, token, verifyDecrypted: true);
    }

    public static Ps3Result ToEncryptedIso(string source, string output, DiscKey key, Ps3Options? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new();
        var report = new ThrottledProgress(progress);
        report.Report(new(OperationStage.Opening, 0, 0, source));
        using var disc = Ps3Disc.Open(source);
        if (disc.State == EncryptionState.Encrypted) throw new Ps3Exception(Ps3Error.WrongKey, "This image is encrypted already.");
        using var view = disc.OpenEncrypted(key);
        return WriteImage(disc, view, output, options, report, token, verifyDecrypted: false);
    }

    public static Ps3Result ToJbFolder(string source, string outputFolder, DiscKey? key, Ps3Options? options = null,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new();
        var report = new ThrottledProgress(progress);
        using var disc = OpenChecked(source, key, report);
        if (disc.State is EncryptionState.Decrypted or EncryptionState.NoEncryptedRegions) key = null;
        // Read front to back with the OS reading ahead; the files are taken in disc order, so the reads seldom jump.
        using var view = disc.OpenDecryptedSequential(key);
        var fs = Iso9660Volume.TryOpen(view) ?? throw new Ps3Exception(Ps3Error.BadFilesystem, "The image's filesystem can't be read.");
        var entries = fs.Entries().Where(e => options.IncludeUpdate || !InUpdate(e.Path)).ToList();
        var sfo = ReadSfo(fs);

        var final = Path.GetFullPath(outputFolder);
        PrepareOutput(final, options.Overwrite, isFolder: true);
        var partial = final + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(partial);
        var hashes = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var files = entries.Where(e => !e.IsDirectory).OrderBy(e => e.FirstSector < 0 ? long.MaxValue : e.FirstSector).ToList();
        long total = files.Sum(e => e.Size), done = 0;
        var gate = new object();
        void Advance(long n, string path)
        {
            var now = Interlocked.Add(ref done, n);
            lock (gate) report.Report(new(OperationStage.Extracting, now, total, path));
        }
        try
        {
            // Every folder first (the files' own too, in case the tree leaves one out), so files can go in any order.
            foreach (var folder in entries.Where(e => e.IsDirectory).Select(e => SafeJoin(partial, e.Path))
                         .Concat(files.Select(e => Path.GetDirectoryName(SafeJoin(partial, e.Path))!)).Distinct(StringComparer.OrdinalIgnoreCase))
                Directory.CreateDirectory(folder);

            // Large files one at a time, each read while the chunk before is written and hashed; small ones, where
            // creating and closing the file costs more than its bytes, several at once when that's allowed.
            var parallel = Math.Max(1, options.MaxParallelFiles) > 1;
            var small = parallel ? files.Where(e => e.Size <= options.ChunkSize).ToList() : [];
            byte[] a = new byte[options.ChunkSize], b = new byte[options.ChunkSize];
            foreach (var entry in parallel ? files.Where(e => e.Size > options.ChunkSize) : files)
            {
                token.ThrowIfCancellationRequested();
                using var input = fs.OpenFile(entry);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using (var outFile = CreateOutput(SafeJoin(partial, entry.Path), entry.Size))
                    Pump(input, a, b, null, (chunk, n) => WriteAndHash(outFile, sha, chunk, n), n => Advance(n, entry.Path), token);
                hashes[entry.Path] = sha.GetHashAndReset();
            }
            if (small.Count > 0) ExtractSmall(disc, key, small, partial, options, hashes, Advance, token);

            Ps3Verification? verification = null;
            if (options.Verify)
            {
                verification = VerifyFolder(partial, entries, hashes, report, token);
                if (!verification.Success) throw new Ps3Exception(Ps3Error.VerifyFailed, "The folder failed its check: " + string.Join("; ", verification.Problems));
            }
            if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
            Directory.Move(partial, final);
            report.Report(new(OperationStage.Finished, total, total));
            return new Ps3Result(final, done, hashes.Count, verification, sfo?.TitleId ?? disc.DiscTitleId, sfo?.Title);
        }
        catch
        {
            TryDelete(partial, isFolder: true);
            throw;
        }
    }

    /// <summary>
    /// Small files, several at once: each worker has its own view of the image (its own handle) and reads a whole
    /// file in one go, then writes and hashes it.
    /// </summary>
    private static void ExtractSmall(Ps3Disc disc, DiscKey? key, List<IsoEntry> small, string partial, Ps3Options options,
        ConcurrentDictionary<string, byte[]> hashes, Action<long, string> advance, CancellationToken token)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelFiles, CancellationToken = token };
        try
        {
            Parallel.ForEach(small, parallel,
                () => (View: disc.OpenDecryptedSequential(key), Buffer: new byte[options.ChunkSize]),
                (entry, _, local) =>
                {
                    token.ThrowIfCancellationRequested();
                    using var input = Iso9660Volume.OpenFile(local.View, entry);
                    var n = input.ReadAtLeast(local.Buffer.AsSpan(0, (int)entry.Size), (int)entry.Size, throwOnEndOfStream: false);
                    using (var outFile = CreateOutput(SafeJoin(partial, entry.Path), n))
                        outFile.Write(local.Buffer, 0, n);
                    hashes[entry.Path] = SHA1.HashData(local.Buffer.AsSpan(0, n));
                    advance(n, entry.Path);
                    return local;
                },
                local => local.View.Dispose());
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Throw what the extraction threw (a Ps3Exception, an IOException…), as the one-at-a-time path does.
            ExceptionDispatchInfo.Throw(ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerExceptions[0]);
        }
    }

    /// <summary>A new output file, sized up front, unbuffered (the writes are whole chunks).</summary>
    private static FileStream CreateOutput(string path, long size)
    {
        var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
        try
        {
            s.SetLength(size);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>Writes a chunk and hashes it at the same time.</summary>
    private static void WriteAndHash(FileStream output, IncrementalHash sha, byte[] chunk, int n)
    {
        var write = Task.Run(() => output.Write(chunk, 0, n));
        try { sha.AppendData(chunk, 0, n); }
        finally { write.GetAwaiter().GetResult(); }
    }

    /// <summary>
    /// Reads <paramref name="input"/> to its end in chunks, on this thread, while the chunk before is with
    /// <paramref name="consume"/> on another; chunks are consumed one at a time, in order. <paramref name="onRead"/>
    /// sees each chunk on this thread as soon as it's read, before it's consumed. Returns how many bytes were read.
    /// </summary>
    private static long Pump(Stream input, byte[] a, byte[] b, Action<byte[], int>? onRead, Action<byte[], int> consume,
        Action<int> consumed, CancellationToken token)
    {
        byte[] reading = a, working = b;
        var pending = Task.CompletedTask;
        var handed = 0;
        long total = 0;
        try
        {
            while (true)
            {
                var n = input.ReadAtLeast(reading, reading.Length, throwOnEndOfStream: false);
                pending.GetAwaiter().GetResult();
                if (handed > 0) consumed(handed);
                if (n == 0) break;
                token.ThrowIfCancellationRequested();
                onRead?.Invoke(reading, n);
                (reading, working) = (working, reading);
                var chunk = working;
                pending = Task.Run(() => consume(chunk, n), CancellationToken.None);
                handed = n;
                total += n;
            }
        }
        finally
        {
            // Never leave a chunk being written to a file the caller is about to close.
            try { pending.Wait(CancellationToken.None); }
            catch (AggregateException) { }
        }
        return total;
    }

    /// <summary>Opens the image and makes sure the key is there and fits when it's needed.</summary>
    private static Ps3Disc OpenChecked(string source, DiscKey? key, IProgress<OperationProgress> report)
    {
        report.Report(new(OperationStage.Opening, 0, 0, source));
        var disc = Ps3Disc.Open(source);
        try
        {
            switch (disc.State)
            {
                case EncryptionState.Encrypted when key is null:
                    throw new Ps3Exception(Ps3Error.KeyMissing, "This image is encrypted; its disc key (.dkey) is needed.");
                case EncryptionState.Unknown when key is null:
                    throw new Ps3Exception(Ps3Error.KeyMissing, "Whether this image is encrypted can't be told; give its disc key (.dkey).");
                case EncryptionState.Encrypted or EncryptionState.Unknown:
                    report.Report(new(OperationStage.CheckingKey, 0, 0));
                    if (disc.KeyFits(key!) == false)
                        throw new Ps3Exception(Ps3Error.WrongKey, "The disc key doesn't fit this image (its files don't decrypt).");
                    break;
            }
            return disc;
        }
        catch
        {
            disc.Dispose();
            throw;
        }
    }

    private static Ps3Result WriteImage(Ps3Disc disc, Ps3DiscStream view, string output, Ps3Options options, IProgress<OperationProgress> report,
        CancellationToken token, bool verifyDecrypted)
    {
        var final = Path.GetFullPath(output);
        if (string.Equals(final, Path.GetFullPath(disc.Path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output can't be the source image.", nameof(output));
        PrepareOutput(final, options.Overwrite, isFolder: false);
        var partial = final + ".partial";
        var total = view.Length;
        try
        {
            byte[] written;
            // The image is read as stored on a handle of its own (front to back, read-ahead on) and handed to
            // SourceBytes as it is; then, on another thread while the next chunk is read, transformed, written and hashed.
            if (File.Exists(partial)) File.Delete(partial);
            using (var source = Ps3Disc.OpenSequential(disc.Path))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var outFile = CreateOutput(partial, total))
            {
                var sink = options.SourceBytes;
                long offset = 0, done = 0;
                // Whole sectors per chunk: each is transformed where it lies.
                var size = Math.Max(1, options.ChunkSize / RegionTable.SectorSize) * RegionTable.SectorSize;
                Pump(source, new byte[size], new byte[size],
                    sink is null ? null : (chunk, n) => sink(chunk.AsSpan(0, n)),
                    (chunk, n) =>
                    {
                        view.TransformInPlace(offset, chunk.AsSpan(0, n));
                        offset += n;
                        WriteAndHash(outFile, sha, chunk, n);
                    },
                    n =>
                    {
                        done += n;
                        report.Report(new(OperationStage.Writing, done, total));
                    }, token);
                if (done != total) throw new IOException($"The image ended early ({done} of {total} bytes read).");
                written = sha.GetHashAndReset();
            }

            Ps3Verification? verification = null;
            if (options.Verify)
            {
                verification = VerifyImage(partial, written, verifyDecrypted, report, token);
                if (!verification.Success) throw new Ps3Exception(Ps3Error.VerifyFailed, "The new image failed its check: " + string.Join("; ", verification.Problems));
            }
            if (File.Exists(final)) File.Delete(final);
            File.Move(partial, final);
            report.Report(new(OperationStage.Finished, total, total));
            ParamSfo? sfo = null;
            if (verifyDecrypted)
            {
                using var result = Ps3Disc.Open(final);
                sfo = result.ReadParamSfo();
            }
            return new Ps3Result(final, total, 1, verification, sfo?.TitleId ?? disc.DiscTitleId, sfo?.Title);
        }
        catch
        {
            TryDelete(partial, isFolder: false);
            throw;
        }
    }

    private static Ps3Verification VerifyImage(string path, byte[] written, bool decrypted, IProgress<OperationProgress> report, CancellationToken token)
    {
        var problems = new List<string>();
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan))
        {
            if (!HashOf(s, new byte[BufferSize], s.Length, report, token).AsSpan().SequenceEqual(written)) problems.Add("it doesn't read back as written");
        }
        if (decrypted)
        {
            using var disc = Ps3Disc.Open(path);
            if (disc.State == EncryptionState.Encrypted) problems.Add("its files still look encrypted");
        }
        return new Ps3Verification(problems.Count == 0, problems);
    }

    private static Ps3Verification VerifyFolder(string root, List<IsoEntry> entries, IReadOnlyDictionary<string, byte[]> hashes,
        IProgress<OperationProgress> report, CancellationToken token)
    {
        var problems = new List<string>();
        var buffer = new byte[BufferSize];
        long total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size), done = 0;
        foreach (var entry in entries)
        {
            var path = SafeJoin(root, entry.Path);
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(path)) problems.Add($"{entry.Path} is missing");
                continue;
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size) { problems.Add($"{entry.Path} is missing or the wrong size"); continue; }
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
            var hash = HashOf(s, buffer, total, report, token, done);
            done += entry.Size;
            if (!hash.AsSpan().SequenceEqual(hashes[entry.Path])) problems.Add($"{entry.Path} doesn't read back as written");
        }
        var eboot = SafeJoin(root, "PS3_GAME/USRDIR/EBOOT.BIN");
        if (File.Exists(eboot))
        {
            var head = new byte[4];
            using var s = File.OpenRead(eboot);
            if (s.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 || !head.AsSpan().SequenceEqual("SCE\0"u8))
                problems.Add("PS3_GAME/USRDIR/EBOOT.BIN isn't a decrypted SELF");
        }
        return new Ps3Verification(problems.Count == 0, problems.Take(20).ToList());
    }

    private static byte[] HashOf(Stream s, byte[] buffer, long total, IProgress<OperationProgress> report, CancellationToken token, long doneBefore = 0)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long done = doneBefore;
        int n;
        while ((n = s.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            done += n;
            report.Report(new(OperationStage.Verifying, done, total));
        }
        return sha.GetHashAndReset();
    }

    private static ParamSfo? ReadSfo(Iso9660Volume fs)
    {
        var entry = fs.Find("PS3_GAME/PARAM.SFO");
        if (entry is null || entry.IsDirectory) return null;
        using var s = fs.OpenFile(entry);
        return ParamSfo.TryRead(s);
    }

    private static bool InUpdate(string path) =>
        path.Equals("PS3_UPDATE", StringComparison.OrdinalIgnoreCase) || path.StartsWith("PS3_UPDATE/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Joins a disc path under <paramref name="root"/>, refusing anything that would land outside it.</summary>
    private static string SafeJoin(string root, string discPath)
    {
        var full = Path.GetFullPath(Path.Combine(root, discPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new Ps3Exception(Ps3Error.BadFilesystem, $"The disc path {discPath} would land outside the output folder.");
        return full;
    }

    private static void PrepareOutput(string final, bool overwrite, bool isFolder)
    {
        var exists = isFolder ? Directory.Exists(final) || File.Exists(final) : File.Exists(final) || Directory.Exists(final);
        if (exists && !overwrite) throw new Ps3Exception(Ps3Error.OutputExists, $"{final} already exists.");
        if (Path.GetDirectoryName(final) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
    }

    private static void TryDelete(string path, bool isFolder)
    {
        try
        {
            if (isFolder && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (!isFolder && File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
