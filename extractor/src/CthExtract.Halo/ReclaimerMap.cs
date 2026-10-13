using System.IO;
using Reclaimer.Blam.Common;
using Reclaimer.Blam.Common.Gen3;
using Reclaimer.Blam.Utilities;
using Reclaimer.Geometry;

namespace CthExtract.Halo;

/// <summary>
/// An MCC Halo 3 cache file opened with the vendored Reclaimer (read-only, shared; see vendor/Reclaimer/VENDORED.md).
/// Raw reads go through one DependencyReader per map; geometry through Reclaimer's ContentFactory (which reads
/// resources from shared.map next to the map).
/// </summary>
public sealed class ReclaimerMap : IHaloMap
{
    private readonly ICacheFile cache;
    private readonly IMccCacheFile mcc;
    private readonly DependencyReader reader;
    private readonly object gate = new();
    private readonly Dictionary<int, IIndexItem> items = new();
    private readonly List<HaloTag> tags = new();
    private readonly Dictionary<int, HaloTag> byIndex = new();

    public string Path { get; }
    public string Description { get; }
    public IReadOnlyList<HaloTag> Tags => tags;

    private ReclaimerMap(string path, ICacheFile cache, IMccCacheFile mcc)
    {
        Path = path; this.cache = cache; this.mcc = mcc;
        reader = cache.CreateReader(cache.DefaultAddressTranslator);
        foreach (var item in cache.TagIndex)
        {
            if (item is null) continue;
            var t = new HaloTag(item.Id, item.ClassCode ?? "", item.TagName ?? "");
            items[item.Id] = item;
            byIndex[item.Id] = t;
            tags.Add(t);
        }
        string scenario = "";
        try { scenario = mcc.Header?.ScenarioName ?? ""; } catch { }
        Description = $"{System.IO.Path.GetFileName(path)}: build '{cache.BuildString}', cache type {cache.CacheType} " +
                      $"(engine {cache.Metadata?.Engine}, mcc {cache.Metadata?.IsMcc}, codec {cache.Metadata?.ResourceCodec}), " +
                      $"scenario '{scenario}', {tags.Count} tags";
    }

    /// <summary>Open a map; throws with a readable message for anything that is not an MCC Gen3 Halo 3 cache.</summary>
    public static ReclaimerMap Open(string path)
    {
        var cache = CacheFactory.ReadCacheFile(path);
        if (cache is not IMccCacheFile mcc)
            throw new NotSupportedException($"{System.IO.Path.GetFileName(path)} is a {cache.CacheType} cache, not an MCC Halo 3 one");
        if (cache.Metadata?.Engine != BlamEngine.Halo3)
            throw new NotSupportedException($"{System.IO.Path.GetFileName(path)} is a {cache.Metadata?.Engine} cache, not Halo 3");
        return new ReclaimerMap(path, cache, mcc);
    }

    public HaloTag? Global(string tagClass)
    {
        try
        {
            var item = cache.TagIndex.GetGlobalTag(tagClass);
            return item is null ? null : byIndex.GetValueOrDefault(item.Id);
        }
        catch { return null; }
    }

    public HaloTag? Tag(int index) => byIndex.GetValueOrDefault(index);

    public long TagAddress(HaloTag tag) => items.TryGetValue(tag.Index, out var item) ? item.MetaPointer.Address : 0;

    public byte[] Read(long address, int count)
    {
        lock (gate)
        {
            if (address <= 0 || address + count > reader.BaseStream.Length)
                throw new InvalidDataException($"read of {count} bytes at 0x{address:x} is outside {System.IO.Path.GetFileName(Path)}");
            reader.Seek(address, SeekOrigin.Begin);
            return reader.ReadBytes(count);
        }
    }

    public long Pointer(int compressed) => cache.DefaultAddressTranslator.GetAddress(mcc.PointerExpander.Expand(compressed));

    public string? StringId(int id)
    {
        try { return cache.StringIndex.TryGetValue(id, out var s) ? s : null; }
        catch { return null; }
    }

    public Scene? Geometry(HaloTag mode)
    {
        if (!items.TryGetValue(mode.Index, out var item)) return null;
        return ContentFactory.TryGetGeometryContent(item, out var provider) ? provider.GetContent() : null;
    }

    public void Dispose()
    {
        lock (gate) reader.Dispose();
    }
}
