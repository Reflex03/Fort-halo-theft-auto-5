using CUE4Parse.UE4.IO.Objects.OnDemand;
using CUE4Parse.UE4.IO.Objects.OnDemand.V1;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

namespace CUE4Parse.UE4.IO;

public class IoChunkToc
{
    public IOnDemandToc OnDemandToc;

    public IoChunkToc(string file, VersionContainer versions) : this(new FileInfo(file), versions) { }
    public IoChunkToc(FileInfo file, VersionContainer versions) : this(new FByteArchive(file.FullName, File.ReadAllBytes(file.FullName), versions)) { }
    public IoChunkToc(FArchive Ar)
    {
        var bIsLegacy = Ar.Read<ulong>() == 0x6f6e64656d616e64;
        Ar.Position = 0;

        OnDemandToc = bIsLegacy ? new FOnDemandToc(Ar) : new Objects.OnDemand.V2.FOnDemandToc(Ar);
    }
}

// CthExtract: the vendored copy never downloads Epic content. Upstream fetched IoStore on-demand chunks from Epic's
// CDN here (optionally with the user's bearer token). Only chunks already present in ChunkCacheDirectory are read;
// anything else fails, so missing on-demand content is reported as missing instead of being streamed.
public class IoStoreOnDemandOptions
{
    public required Uri ChunkHostUri { get; init; }
    public DirectoryInfo? ChunkCacheDirectory { get; init; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

public class IoStoreOnDemandDownloader : IDisposable
{
    private readonly IoStoreOnDemandOptions _options;

    public IoStoreOnDemandDownloader(IoStoreOnDemandOptions options)
    {
        _options = options;
    }

    public Task<Stream> Download(string url, long position = 0)
    {
        var cachePath = _options.ChunkCacheDirectory is not null && _options.ChunkCacheDirectory.Exists
            ? Path.Combine(_options.ChunkCacheDirectory.FullName, url.SubstringAfterLast('/'))
            : null;
        if (cachePath is not null && File.Exists(cachePath))
        {
            var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = position;
            return Task.FromResult<Stream>(fs);
        }

        throw new FileNotFoundException($"On-demand chunk '{url}' is not available locally (network downloads are disabled in this build)");
    }

    public void Dispose()
    {
    }
}
