using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace LibPs3Kit;

public static partial class Ps3Converter
{
    private static Ps3Verification VerifyImage(string path, byte[] written, bool decrypted, Ps3Options options,
        IProgress<OperationProgress> report, CancellationToken token)
    {
        var problems = new List<string>();
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan))
        {
            long total = s.Length, done = 0;
            var hash = HashOf(s, total, new byte[options.ChunkSize], new byte[options.ChunkSize],
                n => report.Report(new(OperationStage.Verifying, done += n, total)), token);
            if (!hash.AsSpan().SequenceEqual(written)) problems.Add("it doesn't read back as written");
        }
        if (decrypted)
        {
            using var disc = Ps3Disc.Open(path);
            if (disc.State == EncryptionState.Encrypted) problems.Add("its files still look encrypted");
        }
        return new Ps3Verification(problems.Count == 0, problems);
    }

    /// <summary>
    /// Reads every file of a JB folder back. Large files one at a time, each read while the chunk before is hashed;
    /// small ones several at once when <see cref="Ps3Options.MaxParallelFiles"/> allows, as they were extracted. The
    /// problems come out in the entries' order however the files were taken.
    /// </summary>
    internal static Ps3Verification VerifyFolder(string root, List<IsoEntry> entries, IReadOnlyDictionary<string, byte[]> hashes,
        Ps3Options options, IProgress<OperationProgress> report, CancellationToken token)
    {
        var found = new string?[entries.Count];
        long total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size), done = 0;
        var gate = new object();
        void Advance(int n)
        {
            var now = Interlocked.Add(ref done, n);
            lock (gate) report.Report(new(OperationStage.Verifying, now, total));
        }
        string? Check(IsoEntry entry, byte[] a, byte[] b)
        {
            var path = SafeJoin(root, entry.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size) return $"{entry.Path} is missing or the wrong size";
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
            var hash = HashOf(s, entry.Size, a, b, Advance, token);
            return hash.AsSpan().SequenceEqual(hashes[entry.Path]) ? null : $"{entry.Path} doesn't read back as written";
        }

        var parallel = Math.Max(1, options.MaxParallelFiles) > 1;
        byte[] a = new byte[options.ChunkSize], b = new byte[options.ChunkSize];
        var small = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(SafeJoin(root, entry.Path))) found[i] = $"{entry.Path} is missing";
            }
            else if (parallel && entry.Size < options.ChunkSize) small.Add(i);
            else
            {
                token.ThrowIfCancellationRequested();
                found[i] = Check(entry, a, b);
            }
        }
        if (small.Count > 0)
        {
            var settings = new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelFiles, CancellationToken = token };
            try
            {
                // A small file is read in one go (HashOf never pumps one smaller than its buffer), so a worker needs one buffer.
                Parallel.ForEach(small, settings, () => new byte[options.ChunkSize],
                    (i, _, buffer) =>
                    {
                        found[i] = Check(entries[i], buffer, buffer);
                        return buffer;
                    },
                    _ => { });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Throw(ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerExceptions[0]);
            }
        }

        var problems = found.OfType<string>().ToList();
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

    /// <summary>
    /// The SHA-1 of the rest of <paramref name="s"/>, which holds about <paramref name="size"/> bytes. What fits in one
    /// buffer is read and hashed in one go; anything longer is read a chunk at a time while the chunk before is
    /// hashed on another thread. <paramref name="advance"/> hears how many bytes each chunk held.
    /// </summary>
    private static byte[] HashOf(Stream s, long size, byte[] a, byte[] b, Action<int> advance, CancellationToken token)
    {
        if (size < a.Length)
        {
            // Asking for one byte more than expected tells a file that is longer than it should be.
            var n = s.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            if (n < a.Length)
            {
                token.ThrowIfCancellationRequested();
                if (n > 0) advance(n);
                return SHA1.HashData(a.AsSpan(0, n));
            }
            // Longer after all (it grew since it was measured): hash the rest a chunk at a time, in this one buffer.
            using var rest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            do
            {
                token.ThrowIfCancellationRequested();
                rest.AppendData(a, 0, n);
                advance(n);
            } while ((n = s.Read(a)) > 0);
            return rest.GetHashAndReset();
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Pump(s, a, b, null, (chunk, n) => sha.AppendData(chunk, 0, n), advance, token);
        return sha.GetHashAndReset();
    }
}
