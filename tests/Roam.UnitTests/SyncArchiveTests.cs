using System.Formats.Tar;
using System.IO.Compression;
using Xunit;

namespace Roam.UnitTests;

public sealed class SyncArchiveTests
{
    // Proves the bytes the archive transport puts on the wire round-trip back to the same files,
    // paths, and mtimes that a remote `tar -x` would restore. The real SFTP + remote-tar leg is
    // exercised on the deployment test-ground, not here.
    [Fact]
    public async Task WriteAsyncRoundTripsNamesContentAndMtimes()
    {
        var tempRoot = Directory.CreateTempSubdirectory("roam-archive-");
        try
        {
            var fileA = Path.Combine(tempRoot.FullName, "a.dll");
            var fileB = Path.Combine(tempRoot.FullName, "b.txt");
            await File.WriteAllTextAsync(fileA, "alpha bytes");
            await File.WriteAllTextAsync(fileB, "beta nested bytes");
            var mtimeA = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var mtimeB = new DateTimeOffset(2026, 5, 27, 8, 9, 10, TimeSpan.Zero);

            var uploads = new[]
            {
                new SyncFileUpload(fileA, "a.dll", "/remote/a.dll", mtimeA),
                new SyncFileUpload(fileB, "nested/b.txt", "/remote/nested/b.txt", mtimeB),
            };

            using var buffer = new MemoryStream();
            await SyncArchive.WriteAsync(buffer, uploads, windowsTarget: false, CancellationToken.None);
            buffer.Position = 0;

            var seen = new Dictionary<string, (string Content, long Mtime)>(StringComparer.Ordinal);
            await using (var gzip = new GZipStream(buffer, CompressionMode.Decompress))
            await using (var reader = new TarReader(gzip))
            {
                while (await reader.GetNextEntryAsync() is { } entry)
                {
                    using var contentReader = new StreamReader(entry.DataStream!);
                    seen[entry.Name] = (await contentReader.ReadToEndAsync(), entry.ModificationTime.ToUnixTimeSeconds());
                }
            }

            Assert.Equal(2, seen.Count);
            Assert.Equal("alpha bytes", seen["a.dll"].Content);
            Assert.Equal(mtimeA.ToUnixTimeSeconds(), seen["a.dll"].Mtime);
            Assert.Equal("beta nested bytes", seen["nested/b.txt"].Content);
            Assert.Equal(mtimeB.ToUnixTimeSeconds(), seen["nested/b.txt"].Mtime);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
