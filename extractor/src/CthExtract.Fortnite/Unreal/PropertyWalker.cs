using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;

namespace CthExtract.Fortnite.Unreal;

/// <summary>An object reference found somewhere in an object's properties.</summary>
public sealed record FoundRef(string Path, string PropertyName, ILoadableObject Target, string TargetPath)
{
    /// <summary>Class name when it can be told without loading (hard references know their import class).</summary>
    public string? KnownClass => Target is FPackageIndex pi ? pi.ResolvedObject?.Class?.Name.Text : null;
    public override string ToString() => $"{Path} -> {TargetPath}";
}

/// <summary>
/// Walks UE properties generically (structs, instanced structs such as ItemDefinitionBase.DataList, arrays, sets, maps,
/// optionals) and yields every soft/hard object reference with the property path that led to it. Recipes match on
/// property names, so they keep working when a struct moves inside a DataList or a class gains a level.
/// </summary>
public static class PropertyWalker
{
    public static IEnumerable<FoundRef> Refs(UObject obj, int maxDepth = 8) => Refs(obj.Properties, obj.Name, maxDepth);

    public static IEnumerable<FoundRef> Refs(IEnumerable<FPropertyTag> props, string prefix, int maxDepth = 8)
    {
        foreach (var p in props)
        {
            var name = p.Name.Text;
            foreach (var r in Visit(p.Tag, $"{prefix}.{name}", name, maxDepth))
                yield return r;
        }
    }

    private static IEnumerable<FoundRef> Visit(FPropertyTagType? tag, string path, string propName, int depth)
    {
        if (tag == null || depth < 0) yield break;
        switch (tag)
        {
            case SoftObjectProperty soft:
                if (soft.Value is { } sp && !sp.AssetPathName.IsNone && !string.IsNullOrEmpty(sp.AssetPathName.Text))
                    yield return new FoundRef(path, propName, sp, sp.ToString());
                break;
            case ObjectProperty obj: // includes WeakObjectProperty, ClassProperty-like
                if (obj.Value is { IsNull: false } pi)
                    yield return new FoundRef(path, propName, pi, Describe(pi));
                break;
            case StructProperty st:
                foreach (var r in VisitStruct(st.Value?.StructType, path, propName, depth - 1)) yield return r;
                break;
            case ArrayProperty arr when arr.Value != null:
                for (int i = 0; i < arr.Value.Properties.Count; i++)
                    foreach (var r in Visit(arr.Value.Properties[i], $"{path}[{i}]", propName, depth - 1)) yield return r;
                break;
            case SetProperty set when set.Value != null:
                for (int i = 0; i < set.Value.Properties.Count; i++)
                    foreach (var r in Visit(set.Value.Properties[i], $"{path}{{{i}}}", propName, depth - 1)) yield return r;
                break;
            case MapProperty map when map.Value != null:
                foreach (var (k, v) in map.Value.Properties)
                {
                    var key = KeyText(k);
                    foreach (var r in Visit(k, $"{path}<key {key}>", propName, depth - 1)) yield return r;
                    foreach (var r in Visit(v, $"{path}[{key}]", propName, depth - 1)) yield return r;
                }
                break;
            case OptionalProperty opt:
                foreach (var r in Visit(opt.Value, path, propName, depth - 1)) yield return r;
                break;
        }
    }

    private static IEnumerable<FoundRef> VisitStruct(object? s, string path, string propName, int depth)
    {
        switch (s)
        {
            case FStructFallback fb:
                foreach (var p in fb.Properties)
                    foreach (var r in Visit(p.Tag, $"{path}.{p.Name.Text}", p.Name.Text, depth))
                        yield return r;
                break;
            case FInstancedStruct inst:
                var typeName = inst.ScriptStruct?.StructType?.GetType().Name;
                if (inst.NonConstStruct is { } nc)
                    foreach (var p in nc.Properties)
                        foreach (var r in Visit(p.Tag, $"{path}.{p.Name.Text}", p.Name.Text, depth))
                            yield return r;
                break;
            case FSoftObjectPath sp when !sp.AssetPathName.IsNone:
                yield return new FoundRef(path, propName, sp, sp.ToString());
                break;
        }
    }

    private static string KeyText(FPropertyTagType? k) => k?.GenericValue switch
    {
        null => "null",
        FName n => n.Text,
        var v => v.ToString() ?? "?",
    };

    public static string Describe(FPackageIndex pi)
    {
        try { return pi.ResolvedObject?.GetFullName() ?? pi.Name; }
        catch { return pi.Name; }
    }

    /// <summary>Names of the structs inside an object's DataList (for logs).</summary>
    public static IEnumerable<string> DataListStructs(UObject obj)
    {
        var dl = obj.Properties.FirstOrDefault(p => p.Name.Text == "DataList")?.Tag as ArrayProperty;
        if (dl?.Value == null) yield break;
        foreach (var e in dl.Value.Properties)
            if (e is StructProperty { Value.StructType: FInstancedStruct inst })
                yield return inst.ScriptStruct?.ToString() ?? "?";
    }
}
