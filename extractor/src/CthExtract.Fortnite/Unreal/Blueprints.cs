using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace CthExtract.Fortnite.Unreal;

/// <summary>A mesh-carrying component of a blueprint with its transform chain (own relative first, then parents).</summary>
public sealed record BpComponent(string Name, string ComponentClass, UObject? Mesh, IReadOnlyList<FTransform> Chain, string Source)
{
    public override string ToString() =>
        $"{Name} ({ComponentClass}, {Source}) mesh={(Mesh == null ? "-" : Mesh.GetPathName())}";
}

/// <summary>Blueprint helpers: class chain, class default objects, and the construction-script components.</summary>
public static class Blueprints
{
    /// <summary>The class and its blueprint parents (most derived first), at most 10.</summary>
    public static List<UStruct> ClassChain(UStruct cls)
    {
        var chain = new List<UStruct> { cls };
        var cur = cls;
        while (chain.Count < 10)
        {
            UStruct? super = null;
            try { super = cur.SuperStruct is { IsNull: false } s ? s.Load<UStruct>() : null; } catch { }
            if (super == null || chain.Contains(super)) break;
            chain.Add(super);
            cur = super;
        }
        return chain;
    }

    /// <summary>Class default objects along the chain (most derived first).</summary>
    public static List<UObject> Defaults(UStruct cls)
    {
        var list = new List<UObject>();
        foreach (var c in ClassChain(cls))
        {
            if (c is not UClass uc || uc.ClassDefaultObject is not { IsNull: false } cdoIdx) continue;
            try { if (cdoIdx.Load() is { } cdo) list.Add(cdo); } catch { }
        }
        return list;
    }

    /// <summary>First value of a property on the class defaults, most derived first.</summary>
    public static FPropertyTag? DefaultProperty(UStruct cls, string name)
    {
        foreach (var cdo in Defaults(cls))
        {
            var p = cdo.Properties.FirstOrDefault(t => string.Equals(t.Name.Text, name, StringComparison.OrdinalIgnoreCase));
            if (p?.Tag != null) return p;
        }
        return null;
    }

    /// <summary>
    /// Every scene component created by the construction scripts of the class chain, with inherited-component
    /// overrides (UInheritableComponentHandler) applied. Walks the SCS tree itself (USimpleConstructionScript.
    /// GetAllNodesRecursive throws on leaf nodes in this CUE4Parse version).
    /// </summary>
    public static List<BpComponent> Components(UStruct cls, Action<string>? log = null)
    {
        var chain = ClassChain(cls);
        var overrides = new Dictionary<string, (UObject Template, string From)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chain) // most derived first: first one wins
        {
            if (c is not UBlueprintGeneratedClass bp || bp.InheritableComponentHandler is not { IsNull: false } ichIdx) continue;
            try
            {
                if (ichIdx.Load() is not UInheritableComponentHandler ich) continue;
                foreach (var rec in ich.Records)
                {
                    var key = rec.ComponentKey.SCSVariableName.Text;
                    if (rec.ComponentTemplate is { IsNull: false } t && !overrides.ContainsKey(key) && t.Load() is { } tmpl)
                        overrides[key] = (tmpl, $"override in {bp.Name}");
                }
            }
            catch (Exception e) { log?.Invoke($"inheritable component handler of {c.Name}: {e.Message}"); }
        }

        var result = new List<BpComponent>();
        // an SCS is a tree, but the node graph comes from the player's files: never walk a node twice (a cycle with two
        // children per node would otherwise mean 2^32 visits within the depth limit) and never walk more than 4096
        var walked = new HashSet<string>(StringComparer.Ordinal);
        int budget = 4096;
        foreach (var c in Enumerable.Reverse(chain)) // base first, like the engine runs them
        {
            if (c is not UBlueprintGeneratedClass bp || bp.SimpleConstructionScript is not { IsNull: false } scsIdx) continue;
            USimpleConstructionScript? scs = null;
            try { scs = scsIdx.Load<USimpleConstructionScript>(); } catch (Exception e) { log?.Invoke($"SCS of {c.Name}: {e.Message}"); }
            if (scs == null) continue;
            USCS_Node[] roots;
            try { roots = scs.GetRootNodes(); } catch (Exception e) { log?.Invoke($"SCS roots of {c.Name}: {e.Message}"); continue; }
            foreach (var root in roots) Walk(root, [], c.Name, 0);
        }
        return result;

        void Walk(USCS_Node node, List<FTransform> parentChain, string owner, int depth)
        {
            if (depth > 32 || --budget < 0) return;
            string nodeKey;
            try { nodeKey = node.GetPathName(); } catch { nodeKey = $"{owner}/{node.InternalVariableName.Text}/{depth}"; }
            if (!walked.Add(nodeKey)) return;
            var name = node.InternalVariableName.Text;
            UObject? original = null;
            try { original = node.ComponentTemplate is { IsNull: false } ct ? ct.Load() : null; } catch { }
            var (tmpl, src) = overrides.TryGetValue(name, out var o) ? (o.Template, o.From) : (original, $"SCS of {owner}");
            var chainHere = new List<FTransform>(parentChain);
            if (tmpl is USceneComponent sc)
            {
                try { chainHere.Insert(0, sc.GetRelativeTransform()); } catch { }
            }
            UObject? mesh = null;
            try
            {
                mesh = tmpl switch
                {
                    UStaticMeshComponent smc => smc.GetLoadedStaticMesh() ?? (original as UStaticMeshComponent)?.GetLoadedStaticMesh(),
                    USkinnedMeshComponent skc => LoadSkeletal(skc) ?? (original is USkinnedMeshComponent os ? LoadSkeletal(os) : null),
                    _ => null,
                };
            }
            catch (Exception e) { log?.Invoke($"component {name}: mesh unreadable: {e.Message}"); }
            if (tmpl != null)
                result.Add(new BpComponent(name, tmpl.ExportType, mesh, chainHere, src));
            USCS_Node[] children = [];
            try { children = node.GetChildNodes(); } catch { }
            foreach (var child in children) Walk(child, chainHere, owner, depth + 1);
        }
    }

    /// <summary>Mesh components that are default subobjects of the class defaults (components created in C++, e.g.
    /// BuildingSMActor's StaticMeshComponent), most derived class first.</summary>
    public static List<BpComponent> DefaultSubobjectComponents(UStruct cls, Action<string>? log = null)
    {
        var result = new List<BpComponent>();
        foreach (var cdo in Defaults(cls))
        {
            IEnumerable<UObject> exports;
            try { exports = cdo.Owner?.GetExports() ?? []; } catch (Exception e) { log?.Invoke($"exports of {cdo.Name}: {e.Message}"); continue; }
            foreach (var e in exports)
            {
                if (e.Outer?.Name.Text != cdo.Name) continue;
                var c = MeshComponent(e, "default subobject of " + cdo.Name, log);
                if (c != null) result.Add(c);
            }
        }
        return result;
    }

    /// <summary>A mesh component object as a <see cref="BpComponent"/> (its relative transform only), or null.</summary>
    public static BpComponent? MeshComponent(UObject comp, string source, Action<string>? log = null)
    {
        UObject? mesh = null;
        try
        {
            mesh = comp switch
            {
                UStaticMeshComponent smc => smc.GetLoadedStaticMesh(),
                USkinnedMeshComponent skc => LoadSkeletal(skc),
                _ => null,
            };
        }
        catch (Exception e) { log?.Invoke($"component {comp.Name}: mesh unreadable: {e.Message}"); }
        if (comp is not (UStaticMeshComponent or USkinnedMeshComponent)) return null;
        var chain = new List<FTransform>();
        if (comp is USceneComponent sc) { try { chain.Add(sc.GetRelativeTransform()); } catch { } }
        var name = comp.Name.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal) ? comp.Name[..^"_GEN_VARIABLE".Length] : comp.Name;
        return new BpComponent(name, comp.ExportType, mesh, chain, source);
    }

    private static USkeletalMesh? LoadSkeletal(USkinnedMeshComponent c)
    {
        var idx = c.GetSkeletalMesh();
        return idx is { IsNull: false } ? idx.Load<USkeletalMesh>() : null;
    }

    /// <summary>The component a WeakObject/Object property of the class defaults points at (e.g. BusStaticMesh), by name.</summary>
    public static string? ComponentNameOf(FPropertyTag? tag)
    {
        if (tag?.Tag?.GenericValue is FPackageIndex { IsNull: false } pi)
        {
            var n = pi.Name;
            // component templates are named "<Var>_GEN_VARIABLE" in cooked blueprints
            return n.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal) ? n[..^"_GEN_VARIABLE".Length] : n;
        }
        return null;
    }
}
