using System.Buffers;

namespace LibPs3Kit;

public static partial class Ps3Converter
{
    /// <summary>Where a file's bytes lie in the image: <paramref name="Length"/> bytes from <paramref name="At"/>, the file's last ones when <paramref name="Last"/>.</summary>
    internal readonly record struct FileSegment(int File, long At, int Length, bool Last);

    /// <summary>
    /// The new image's bytes front to back: the metadata, then each file at its sector with zeros up to the next.
    /// Each file must be the size it was when the folder was scanned. The stream doesn't hash: it notes where each
    /// file's bytes went (<see cref="TakeSegments"/>), so they can be hashed on another thread. With
    /// <paramref name="parallel"/> above 1, the small files coming up (up to <paramref name="smallUpTo"/> bytes) are
    /// read ahead, several at once, since opening and closing each is the slow part on an SSD.
    /// </summary>
    internal sealed class FolderImageStream(IsoLayout layout, int parallel = 1, int smallUpTo = 0) : Stream
    {
        private readonly long _length = layout.TotalSectors * RegionTable.SectorSize;
        private readonly Dictionary<int, Task<byte[]>> _ahead = [];
        private List<FileSegment> _segments = [];
        private long _position;
        private int _file = -1;  // the file being read, by index in layout.Files
        private bool _inFile;    // its bytes are being read (from _input or _data)
        private FileStream? _input;
        private byte[]? _data;   // a small file read ahead, from the pool
        private int _dataAt;
        private int _nextAhead;  // the next file to think about reading ahead

        /// <summary>The file being read, for progress.</summary>
        public string? Current => _file >= 0 && _file < layout.Files.Count ? layout.Files[_file].DiscPath : null;

        /// <summary>Where the files' bytes went since the last call, in order.</summary>
        public List<FileSegment> TakeSegments()
        {
            var taken = _segments;
            _segments = [];
            return taken;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                int n;
                if (_position < layout.Metadata.Length)
                {
                    n = (int)Math.Min(buffer.Length, layout.Metadata.Length - _position);
                    layout.Metadata.AsSpan((int)_position, n).CopyTo(buffer);
                }
                else
                {
                    var next = NextData();
                    if (next is null || _position < next.Value.Start)
                    {
                        // Zeros: the end of a file's last sector, or up to the image's end.
                        var upTo = next?.Start ?? _length;
                        n = (int)Math.Min(buffer.Length, upTo - _position);
                        buffer[..n].Clear();
                    }
                    else
                    {
                        var (start, file) = next.Value;
                        n = (int)Math.Min(buffer.Length, start + file.Size - _position);
                        if (!_inFile) Begin(file);
                        if (_data is not null)
                        {
                            _data.AsSpan(_dataAt, n).CopyTo(buffer);
                            _dataAt += n;
                        }
                        else if (_input!.ReadAtLeast(buffer[..n], n, throwOnEndOfStream: false) < n)
                            throw Shorter(file);
                        var last = _position + n == start + file.Size;
                        _segments.Add(new(_file, _position, n, last));
                        if (last) FinishFile();
                    }
                }
                _position += n;
                total += n;
                buffer = buffer[n..];
            }
            return total;
        }

        /// <summary>The file whose data comes next (its first byte's offset), skipping empty ones.</summary>
        private (long Start, IsoNode File)? NextData()
        {
            if (_inFile) return (layout.Files[_file].Sector * RegionTable.SectorSize, layout.Files[_file]);
            while (_file + 1 < layout.Files.Count)
            {
                var f = layout.Files[_file + 1];
                var start = f.Sector * RegionTable.SectorSize;
                if (f.Size == 0) { _file++; continue; }
                if (_position < start) return (start, f);
                _file++;
                return (start, f);
            }
            return null;
        }

        private void Begin(IsoNode file)
        {
            _inFile = true;
            ReadAhead();
            if (_ahead.Remove(_file, out var task))
            {
                _data = task.GetAwaiter().GetResult();
                _dataAt = 0;
            }
            else _input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        }

        /// <summary>Starts reading the small files after the current one, keeping a few on the way.</summary>
        private void ReadAhead()
        {
            if (parallel <= 1) return;
            _nextAhead = Math.Max(_nextAhead, _file);
            while (_ahead.Count < parallel * 2 && _nextAhead < layout.Files.Count)
            {
                var index = _nextAhead++;
                var f = layout.Files[index];
                if (f.Size > 0 && f.Size <= smallUpTo) _ahead[index] = Task.Run(() => ReadWhole(f));
            }
        }

        private static byte[] ReadWhole(IsoNode file)
        {
            var size = (int)file.Size;
            var data = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                using var s = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 0);
                if (s.ReadAtLeast(data.AsSpan(0, size), size, throwOnEndOfStream: false) < size) throw Shorter(file);
                return data;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(data);
                throw;
            }
        }

        private static IOException Shorter(IsoNode file) => new($"{file.DiscPath} got shorter while the image was being made.");

        private void FinishFile()
        {
            _inFile = false;
            _input?.Dispose();
            _input = null;
            if (_data is not null) ArrayPool<byte>.Shared.Return(_data);
            _data = null;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FinishFile();
                // Let the reads ahead finish (a file still open can't be left behind) and hand their buffers back.
                foreach (var task in _ahead.Values)
                {
                    try { ArrayPool<byte>.Shared.Return(task.GetAwaiter().GetResult()); }
                    catch (Exception) { }
                }
                _ahead.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
