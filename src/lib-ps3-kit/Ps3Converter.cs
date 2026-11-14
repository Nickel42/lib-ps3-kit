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
}

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
        using var view = disc.OpenDecrypted(key);
        var fs = Iso9660Volume.TryOpen(view) ?? throw new Ps3Exception(Ps3Error.BadFilesystem, "The image's filesystem can't be read.");
        var entries = fs.Entries().Where(e => options.IncludeUpdate || !InUpdate(e.Path)).ToList();
        var sfo = ReadSfo(fs);

        var final = Path.GetFullPath(outputFolder);
        PrepareOutput(final, options.Overwrite, isFolder: true);
        var partial = final + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(partial);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size), done = 0;
        try
        {
            var buffer = new byte[BufferSize];
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var target = SafeJoin(partial, entry.Path);
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = fs.OpenFile(entry);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using (var outFile = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                {
                    outFile.SetLength(entry.Size);
                    int n;
                    while ((n = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        outFile.Write(buffer, 0, n);
                        sha.AppendData(buffer, 0, n);
                        done += n;
                        report.Report(new(OperationStage.Extracting, done, total, entry.Path));
                    }
                }
                hashes[entry.Path] = sha.GetHashAndReset();
            }

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

    private static Ps3Result WriteImage(Ps3Disc disc, Stream view, string output, Ps3Options options, IProgress<OperationProgress> report,
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
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var outFile = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                outFile.SetLength(total);
                var buffer = new byte[BufferSize];
                view.Position = 0;
                long done = 0;
                int n;
                while ((n = view.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    outFile.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    report.Report(new(OperationStage.Writing, done, total));
                }
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
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
        {
            if (!HashOf(s, s.Length, report, token).AsSpan().SequenceEqual(written)) problems.Add("it doesn't read back as written");
        }
        if (decrypted)
        {
            using var disc = Ps3Disc.Open(path);
            if (disc.State == EncryptionState.Encrypted) problems.Add("its files still look encrypted");
        }
        return new Ps3Verification(problems.Count == 0, problems);
    }

    private static Ps3Verification VerifyFolder(string root, List<IsoEntry> entries, Dictionary<string, byte[]> hashes,
        IProgress<OperationProgress> report, CancellationToken token)
    {
        var problems = new List<string>();
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
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            var hash = HashOf(s, total, report, token, done);
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

    private static byte[] HashOf(Stream s, long total, IProgress<OperationProgress> report, CancellationToken token, long doneBefore = 0)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[BufferSize];
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
