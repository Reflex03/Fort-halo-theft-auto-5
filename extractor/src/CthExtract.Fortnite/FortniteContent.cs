using System.Text.RegularExpressions;
using CthExtract.Core;
using CthExtract.Core.Generated;
using CthExtract.Fortnite.Audio;
using CthExtract.Fortnite.Conversion;
using CthExtract.Fortnite.Unreal;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;

namespace CthExtract.Fortnite;

/// <summary>Size limits (largest bounding-box side, metres) a converted mesh must respect.</summary>
public sealed record SizeRange(float Min, float Max);

/// <summary>Knobs for the recipes (tests point them at fixture assets).</summary>
public sealed class RecipeOptions
{
    public SizeRange BusSize { get; init; } = new(2f, 80f);
    public SizeRange GliderSize { get; init; } = new(0.4f, 15f);
    public SizeRange ChestSize { get; init; } = new(0.25f, 6f);
    public int MaxCandidates { get; init; } = 8;
    public int IconMaxSize { get; init; } = 256;
    public int MaterialMaxSize { get; init; } = 512;
    /// <summary>Asset classes searched for each mesh row (sheet locator class first).</summary>
    public string BusItemClass { get; init; } = "AthenaBattleBusItemDefinition";
    public string GliderItemClass { get; init; } = "AthenaGliderItemDefinition";
    public string ChestNativeParent { get; init; } = "BuildingContainer";
    /// <summary>Item properties holding the icon, best first (any other property containing Icon/Image follows).</summary>
    public IReadOnlyList<string> IconProperties { get; init; } =
        ["LargeIcon", "Icon", "LargePreviewImage", "SmallPreviewImage", "LargeImage", "SmallImage", "WideIcon"];
    /// <summary>Mesh asset names used when no Battle Bus definition leads to a mesh.</summary>
    public string BusMeshPattern { get; init; } = "(?i)battle_?bus";
    public string BusMeshExclude { get; init; } = "(?i)Armored|Toy|Emoji|Spray|LoadingScreen|LSID|Banner|Glider|Backpack|Icon|Collision|Proxy";
}

/// <summary>The per-row recipes. Every lookup logs what it tried and what it picked; every output is sanity-checked;
/// every row ends Ok, Missing or Failed with the reason.</summary>
public sealed class FortniteContent
{
    private readonly GameArchive _a;
    private readonly ExtractContext _ctx;
    private readonly Log _log;
    private readonly RecipeOptions _opt;
    private readonly Dictionary<string, (UObject? Item, string Note)> _items = new();
    private (UStruct? Class, string Note)? _chest;

    public FortniteContent(GameArchive archive, ExtractContext ctx, RecipeOptions? options = null)
    {
        _a = archive; _ctx = ctx; _log = ctx.Log; _opt = options ?? new RecipeOptions();
    }

    private void Info(string m) => _log.Info("[fortnite] " + m);
    private void Warn(string m) => _log.Warn("[fortnite] " + m);

    // =================================================================== item definitions

    /// <summary>The item definition an icon row names (cached; the sound rows reuse it).</summary>
    public (UObject? Item, string Note) FindItem(SourceRow row)
    {
        if (_items.TryGetValue(row.Id, out var hit)) return hit;
        var tried = new List<string>();
        var names = RecipeBook.ItemNames(row);
        Info($"{row.Id}: looking for item definitions named [{string.Join(", ", names)}]");

        foreach (var name in names)
        {
            foreach (var e in _a.Index.ByName(name).OrderBy(e => e.AssetClass.Contains("ItemDefinition", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var obj = _a.TryLoad(e.ObjectPath, out var err);
                tried.Add($"{e.ObjectPath} ({e.AssetClass}): {(obj != null ? "loaded" : err)}");
                if (obj != null) return Remember(row, obj, $"registry {e.AssetClass} {e.ObjectPath}");
            }
            foreach (var f in _a.Index.FilesNamed(name))
            {
                var path = AssetIndex.ObjectPathForFile(f);
                var obj = _a.TryLoad(path, out var err);
                tried.Add($"{f}: {(obj != null ? "loaded" : err)}");
                if (obj != null) return Remember(row, obj, $"package file {f}");
            }
        }

        // fallback: any item definition whose display name is the item's name
        var display = RecipeBook.DisplayName(row);
        var want = RecipeBook.Squash(display);
        var keywords = RecipeBook.Keywords(row);
        var pool = _a.Index.Entries
            .Where(e => e.AssetClass.EndsWith("ItemDefinition", StringComparison.OrdinalIgnoreCase)
                        && !e.AssetClass.StartsWith("AthenaCharacter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var e in pool)
        {
            var tag = e.Tag("DisplayName") ?? e.Tag("ItemName");
            if (tag != null && RecipeBook.Squash(StripTextTag(tag)) == want)
            {
                var obj = _a.TryLoad(e.ObjectPath, out var err);
                tried.Add($"{e.ObjectPath} (DisplayName tag '{tag}'): {(obj != null ? "loaded" : err)}");
                if (obj != null) return Remember(row, obj, $"display-name tag match {e.ObjectPath}");
            }
        }
        int loads = 0;
        foreach (var e in pool.Where(e => keywords.Any(k => e.AssetName.Contains(k, StringComparison.OrdinalIgnoreCase))).Take(60))
        {
            if (loads++ >= 40) break;
            var obj = _a.TryLoad(e.ObjectPath, out _);
            if (obj == null) continue;
            var itemName = ItemName(obj);
            if (itemName != null && RecipeBook.Squash(itemName) == want)
                return Remember(row, obj, $"display name '{itemName}' of {e.ObjectPath}");
        }
        tried.Add($"display name '{display}': {loads} candidate definitions loaded, none matched");

        var note = $"no item definition found (tried: {string.Join("; ", tried.TakeLast(12))})";
        _items[row.Id] = (null, note);
        return (null, note);

        (UObject?, string) Remember(SourceRow r, UObject o, string how)
        {
            var n = $"{how} (class {o.ExportType}, ItemName '{ItemName(o) ?? "?"}', DataList [{string.Join(", ", PropertyWalker.DataListStructs(o).Take(12))}])";
            Info($"{r.Id}: item {n}");
            _items[r.Id] = (o, n);
            return (o, n);
        }
    }

    private static string StripTextTag(string t)
    {
        // cooked registries store FText tags as NSLOCTEXT("ns", "key", "Source") or plain text
        var m = Regex.Match(t, "\"([^\"]*)\"\\)\\s*$");
        return m.Success ? m.Groups[1].Value : t;
    }

    private static string? ItemName(UObject item)
    {
        try { return item.GetOrDefault<FText?>("ItemName")?.Text; } catch { return null; }
    }

    // =================================================================== icons

    public void Icon(SourceRow row)
    {
        var (item, note) = FindItem(row);
        if (item == null) { _ctx.Missing(row.Id, note); return; }

        var refs = PropertyWalker.Refs(item).ToList();
        var ordered = _opt.IconProperties
            .SelectMany(p => refs.Where(r => string.Equals(r.PropertyName, p, StringComparison.OrdinalIgnoreCase)))
            .Concat(refs.Where(r => r.PropertyName.Contains("Icon", StringComparison.OrdinalIgnoreCase) || r.PropertyName.Contains("Image", StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        Info($"{row.Id}: icon candidates [{string.Join("; ", ordered.Take(10))}]");
        var tried = new List<string>();
        foreach (var r in ordered.Take(12))
        {
            try
            {
                var obj = r.Target.Load();
                var tex = obj as UTexture ?? (obj is UMaterialInterface mat ? TextureOf(mat, out _) : null);
                if (tex == null) { tried.Add($"{r.TargetPath}: {obj?.ExportType ?? "not loadable"}"); continue; }
                var img = TextureConvert.Decode(tex, Math.Max(_opt.IconMaxSize * 2, 256), out var detail);
                if (!TextureConvert.LooksLikeImage(img, out var why)) { tried.Add($"{r.TargetPath}: {why}"); continue; }
                _ctx.Texture(row.Id, img, _opt.IconMaxSize, $"{detail} via {r.Path}; item: {note}");
                return;
            }
            catch (Exception e) { tried.Add($"{r.TargetPath}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        }
        if (ordered.Count == 0) _ctx.Missing(row.Id, $"item has no icon reference ({refs.Count} references seen); item: {note}");
        else _ctx.Failed(row.Id, $"no icon could be decoded: {string.Join("; ", tried)}");
    }

    // =================================================================== materials -> textures

    private static readonly string[] DiffuseNames =
    [
        "Diffuse", "D", "Base Color", "BaseColor", "Concrete", "Trunk_BaseColor", "Diffuse Top", "Diffuse Texture", "BaseColor_Trunk",
        "CliffTexture", "PM_Diffuse", "___Diffuse", "BaseColorTexture", "BaseColor Map", "Diffuse_Texture_2", "Diffuse_Texture_3",
        "Diffuse_Texture_4", "MainTex", "Texture",
    ];

    /// <summary>The diffuse texture of a material (instance), FortnitePorting's parameter names first, then CUE4Parse's.</summary>
    public static UTexture? TextureOf(UMaterialInterface mat, out string how)
    {
        var p = new CMaterialParams2();
        try { mat.GetParams(p, EMaterialDepth.AllLayers); } catch (Exception e) { how = $"GetParams failed: {e.Message}"; return null; }
        foreach (var names in new[] { DiffuseNames, CMaterialParams2.Diffuse[0], [CMaterialParams2.FallbackDiffuse] })
            foreach (var n in names)
                if (p.TryGetTexture2d(out var t, n)) { how = $"parameter '{n}'"; return t; }
        var rx = new Regex(CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase);
        var normals = new Regex(CMaterialParams2.RegexNormals, RegexOptions.IgnoreCase);
        foreach (var (name, ptr) in p.Textures)
            if (rx.IsMatch(name) && !normals.IsMatch(name) && ptr.TryLoad<UTexture>(out var t)) { how = $"parameter '{name}' (name pattern)"; return t; }
        foreach (var (name, ptr) in p.Textures)
            if (!normals.IsMatch(name) && ptr.TryLoad<UTexture>(out var t) && !t.Name.EndsWith("_N", StringComparison.OrdinalIgnoreCase)
                && !t.Name.EndsWith("_M", StringComparison.OrdinalIgnoreCase) && !t.Name.EndsWith("_S", StringComparison.OrdinalIgnoreCase))
            { how = $"first texture parameter '{name}'"; return t; }
        how = $"no diffuse among [{string.Join(", ", p.Textures.Keys.Take(12))}]";
        return null;
    }

    private void WriteMaterialTextures(MeshBuild build, string rowId, List<string> notes)
    {
        int ok = 0, flat = 0;
        foreach (var slot in build.Materials)
        {
            Rgba? img = null;
            string why = "no material";
            try
            {
                if (slot.Material?.Load() is UMaterialInterface mat)
                {
                    var tex = TextureOf(mat, out why);
                    if (tex != null)
                    {
                        img = Images.Opaque(TextureConvert.Decode(tex, _opt.MaterialMaxSize, out var d));
                        why = $"{why}: {d}";
                    }
                }
            }
            catch (Exception e) { why = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"; img = null; }
            if (img == null) { img = TextureConvert.Flat(160, 160, 160); flat++; }
            else ok++;
            _ctx.MaterialTexture(slot.Name, img, _opt.MaterialMaxSize);
            Info($"{rowId}: material {slot.Name} <- {slot.SlotName}: {why}");
        }
        notes.Add($"{ok} textured materials, {flat} flat grey");
    }

    // =================================================================== meshes

    private bool TryWriteMesh(SourceRow row, IReadOnlyList<MeshSource> parts, SizeRange size, Func<MeshData, string> place, string how, List<string> tried)
    {
        MeshBuild build;
        try { build = MeshConvert.Build(parts, row.MaxTris, MaterialPrefix(row)); }
        catch (Exception e) { tried.Add($"{how}: conversion failed: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); return false; }
        foreach (var n in build.Notes) Info($"{row.Id}: {n}");
        if (build.Mesh.TriangleCount == 0) { tried.Add($"{how}: no triangles ({string.Join("; ", build.Notes)})"); return false; }
        var b0 = Placement.Measure(build.Mesh);
        if (b0.Largest < size.Min || b0.Largest > size.Max || !float.IsFinite(b0.Largest))
        {
            tried.Add($"{how}: size {b0} outside {size.Min}-{size.Max} m");
            Warn($"{row.Id}: rejected {how}: bounds {b0}");
            return false;
        }
        var placeNote = place(build.Mesh);
        var b = Placement.Measure(build.Mesh);
        var (agree, total) = MeshConvert.WindingAgreement(build.Mesh);
        var notes = new List<string> { how, placeNote, $"bounds {b}", $"winding {agree}/{total} CCW" };
        WriteMaterialTextures(build, row.Id, notes);
        _ctx.Mesh(row.Id, build.Mesh, string.Join("; ", notes));
        return true;
    }

    private static string MaterialPrefix(SourceRow row) =>
        Path.ChangeExtension(row.Out, null)!.Replace('\\', '/').Replace("meshes/", "", StringComparison.Ordinal);

    private IEnumerable<AssetEntry> ItemDefinitions(string cls, string? preferred, IReadOnlyCollection<string> preferredNames, Regex? exclude)
    {
        var defs = _a.Index.ByClass(cls).ToList();
        if (defs.Count == 0)
        {
            // registry without that class (or none at all): fall back to package file names of the usual prefix
            var prefix = preferred is { Length: > 4 } ? preferred[..(preferred.IndexOf('_') + 1)] : "";
            foreach (var n in _a.Index.FileNames.Where(n => prefix.Length > 1 && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(200))
                foreach (var f in _a.Index.FilesNamed(n))
                {
                    var (pkg, name) = AssetIndex.SplitObjectPath(AssetIndex.ObjectPathForFile(f));
                    defs.Add(new AssetEntry(pkg, name!, cls, new Dictionary<string, string>(), "file name"));
                }
        }
        Info($"{cls}: {defs.Count} definitions [{string.Join(", ", defs.Take(30).Select(d => d.AssetName))}{(defs.Count > 30 ? ", …" : "")}]");
        return defs
            .Select(d => (d, rank: preferred != null && d.AssetName.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0
                : preferredNames.Any(p => d.AssetName.Equals(p, StringComparison.OrdinalIgnoreCase)) ? 1
                : exclude != null && exclude.IsMatch(d.AssetName) ? 9
                : d.AssetName.Contains("Default", StringComparison.OrdinalIgnoreCase) ? 2 : 3))
            .Where(t => t.rank < 9)
            .OrderBy(t => t.rank).ThenBy(t => t.d.AssetName.Length).ThenBy(t => t.d.AssetName, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.d);
    }

    // ------------------------------------------------------------------- battle bus

    public void BattleBus(SourceRow row)
    {
        var preferred = RecipeBook.PreferredName(row, "BBID_");
        var exclude = RecipeBook.Exclusion(row, "Armored|Toy|Emoji|Spray");
        Info($"{row.Id}: preferred {preferred ?? "-"}, excluding {exclude}");
        var tried = new List<string>();
        foreach (var def in ItemDefinitions(_opt.BusItemClass, preferred, [], exclude).Take(_opt.MaxCandidates))
        {
            var item = _a.TryLoad(def.ObjectPath, out var err);
            if (item == null) { tried.Add($"{def.AssetName}: {err}"); continue; }
            var prefabRef = PropertyWalker.Refs(item).FirstOrDefault(r => r.PropertyName == "BusPrefabClass");
            if (prefabRef == null) { tried.Add($"{def.AssetName}: no BusPrefabClass"); continue; }
            var cls = LoadClass(prefabRef, out var cerr);
            if (cls == null) { tried.Add($"{def.AssetName}: prefab {prefabRef.TargetPath}: {cerr}"); continue; }
            if (TryBlueprintMesh(row, cls, "BusStaticMesh", _opt.BusSize, m => Placement.PlaceVehicle(m, longAxisAlongZ: true),
                    $"{def.AssetName} -> {cls.GetPathName()}", tried))
                return;
        }

        // fallback: meshes named like the bus
        var rx = new Regex(_opt.BusMeshPattern);
        var junk = new Regex(_opt.BusMeshExclude);
        foreach (var e in MeshAssets(rx, junk))
        {
            var mesh = _a.TryLoad(e.ObjectPath, out var err);
            if (mesh == null) { tried.Add($"{e.AssetName}: {err}"); continue; }
            if (TryWriteMesh(row, [new MeshSource(e.AssetName, mesh)], _opt.BusSize, m => Placement.PlaceVehicle(m, true), $"mesh asset {e.ObjectPath} (name fallback)", tried))
                return;
        }
        Report(row, tried, "no Battle Bus definition or mesh found");
    }

    private IEnumerable<AssetEntry> MeshAssets(Regex include, Regex exclude)
    {
        var list = _a.Index.ByClass("StaticMesh").Concat(_a.Index.ByClass("SkeletalMesh"))
            .Where(e => include.IsMatch(e.AssetName) && !exclude.IsMatch(e.AssetName)).ToList();
        if (list.Count == 0)
            foreach (var n in _a.Index.FileNames.Where(n => include.IsMatch(n) && !exclude.IsMatch(n)).Take(50))
                foreach (var f in _a.Index.FilesNamed(n))
                {
                    var (pkg, name) = AssetIndex.SplitObjectPath(AssetIndex.ObjectPathForFile(f));
                    list.Add(new AssetEntry(pkg, name!, "?", new Dictionary<string, string>(), "file name"));
                }
        Info($"mesh assets matching {include}: [{string.Join(", ", list.Take(20).Select(e => e.AssetName))}]");
        return list.OrderBy(e => e.AssetName.Length).Take(_opt.MaxCandidates);
    }

    private UStruct? LoadClass(FoundRef r, out string? error)
    {
        error = null;
        try
        {
            var o = r.Target.Load();
            if (o is UStruct s) return s;
            if (o != null) { error = $"is a {o.ExportType}"; }
        }
        catch (Exception e) { error = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"; }
        // soft class paths sometimes name the Blueprint asset, not the generated class
        var path = r.TargetPath;
        if (!path.EndsWith("_C", StringComparison.Ordinal))
        {
            var c = _a.TryLoad<UStruct>(path + "_C", out var e2);
            if (c != null) return c;
            error ??= e2;
        }
        return null;
    }

    private bool TryBlueprintMesh(SourceRow row, UStruct cls, string? namedComponentProperty, SizeRange size, Func<MeshData, string> place, string how, List<string> tried)
    {
        var comps = Blueprints.Components(cls, m => Info($"{row.Id}: {m}"));
        comps.AddRange(Blueprints.DefaultSubobjectComponents(cls, m => Info($"{row.Id}: {m}")));
        Info($"{row.Id}: {cls.Name} components: [{string.Join("; ", comps)}]");
        var meshComps = comps.Where(c => c.Mesh is UStaticMesh or USkeletalMesh).ToList();

        // CDO mesh properties (BuildingSMActor.StaticMesh on chests)
        var cdoMesh = Blueprints.DefaultProperty(cls, "StaticMesh")?.Tag?.GenericValue as FPackageIndex;
        UObject? cdoMeshObj = null;
        try { cdoMeshObj = cdoMesh is { IsNull: false } ? cdoMesh.Load() : null; } catch { }

        var ordered = new List<(string Name, IReadOnlyList<MeshSource> Parts, string Why)>();
        if (namedComponentProperty != null)
        {
            var prop = Blueprints.DefaultProperty(cls, namedComponentProperty);
            // the property may resolve straight to the component object
            try
            {
                if (prop?.Tag?.GenericValue is FPackageIndex { IsNull: false } pidx && pidx.Load() is { } compObj
                    && Blueprints.MeshComponent(compObj, $"{namedComponentProperty} target") is { Mesh: { } direct } dc)
                {
                    Info($"{row.Id}: {namedComponentProperty} -> {compObj.GetPathName()} ({direct.GetPathName()})");
                    var scsTwin = meshComps.FirstOrDefault(c => c.Name.Equals(dc.Name, StringComparison.OrdinalIgnoreCase));
                    ordered.Add((dc.Name, [new MeshSource(dc.Name, direct, scsTwin?.Chain ?? dc.Chain)], $"{namedComponentProperty} component object"));
                }
            }
            catch (Exception e) { Info($"{row.Id}: {namedComponentProperty} target unreadable: {e.Message}"); }
            var target = Blueprints.ComponentNameOf(prop);
            if (target != null)
            {
                var c = meshComps.FirstOrDefault(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                Info($"{row.Id}: {namedComponentProperty} -> component '{target}'{(c == null ? " (no mesh component of that name)" : "")}");
                if (c != null) ordered.Add((c.Name, [new MeshSource(c.Name, c.Mesh!, c.Chain)], $"{namedComponentProperty} component"));
            }
            else Info($"{row.Id}: {namedComponentProperty} not set on the class defaults");
        }
        if (cdoMeshObj is UStaticMesh sm)
        {
            var root = comps.FirstOrDefault(c => c.ComponentClass.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase));
            ordered.Add(("StaticMesh", [new MeshSource(sm.Name, sm, root?.Chain ?? [])], "class default StaticMesh"));
        }
        foreach (var c in meshComps.OrderByDescending(c => Extent(c.Mesh!)))
            ordered.Add((c.Name, [new MeshSource(c.Name, c.Mesh!, c.Chain)], $"component (largest first)"));

        if (ordered.Count == 0) { tried.Add($"{how}: no mesh components ({comps.Count} components)"); return false; }
        foreach (var (name, parts, why) in ordered.DistinctBy(o => o.Parts[0].Mesh.GetPathName()).Take(4))
            if (TryWriteMesh(row, parts, size, place, $"{how} / {name} ({why}: {parts[0].Mesh.GetPathName()})", tried))
                return true;
        return false;
    }

    private static float Extent(UObject mesh)
    {
        try
        {
            return mesh switch
            {
                UStaticMesh s when s.RenderData?.Bounds is { } b => b.SphereRadius,
                USkeletalMesh k => k.ImportedBounds.SphereRadius,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------- glider

    public void Glider(SourceRow row)
    {
        var names = RecipeBook.ItemNames(row);
        Info($"{row.Id}: preferred [{string.Join(", ", names)}]");
        var tried = new List<string>();
        foreach (var def in ItemDefinitions(_opt.GliderItemClass, names.FirstOrDefault(), names, null).Take(_opt.MaxCandidates))
        {
            var item = _a.TryLoad(def.ObjectPath, out var err);
            if (item == null) { tried.Add($"{def.AssetName}: {err}"); continue; }
            var meshRef = PropertyWalker.Refs(item).FirstOrDefault(r => r.PropertyName == "SkeletalMesh");
            if (meshRef == null) { tried.Add($"{def.AssetName}: no SkeletalMesh"); continue; }
            UObject? mesh = null; string? merr = null;
            try { mesh = meshRef.Target.Load(); } catch (Exception e) { merr = e.Message.Split('\n')[0]; }
            if (mesh is not (USkeletalMesh or UStaticMesh)) { tried.Add($"{def.AssetName}: mesh {meshRef.TargetPath}: {merr ?? mesh?.ExportType ?? "not loadable"}"); continue; }
            var chain = new List<FTransform>();
            if (item.TryGetValue(out FTransform gt, "GliderMeshTransform") && gt.IsRotationNormalized)
            {
                chain.Add(gt);
                Info($"{row.Id}: GliderMeshTransform rot {gt.Rotation} pos {gt.Translation} scale {gt.Scale3D}");
            }
            if (TryWriteMesh(row, [new MeshSource(mesh.Name, mesh, chain)], _opt.GliderSize, Placement.PlaceGlider,
                    $"{def.AssetName} -> {mesh.GetPathName()} (bind pose)", tried))
                return;
        }
        Report(row, tried, "no glider definition found");
    }

    // ------------------------------------------------------------------- chest

    /// <summary>The chest blueprint class (cached for the chest sound row).</summary>
    public (UStruct? Class, string Note) FindChestClasses(SourceRow row, out List<(UStruct Class, string Name)> all)
    {
        all = new List<(UStruct, string)>();
        var nameRx = RecipeBook.InlineRegex(row.Locator) ?? new Regex("(?i)tiered_chest");
        var fallbackRx = RecipeBook.InlineRegex(row.Fallback) ?? new Regex("(?i)chest");
        var exclude = RecipeBook.Exclusion(row, "ammo|cooler|safe|toolbox|stw");
        bool IsContainer(AssetEntry e) =>
            (e.Tag("NativeParentClass") ?? e.Tag("ParentClass") ?? "").Contains(_opt.ChestNativeParent, StringComparison.OrdinalIgnoreCase);
        var classes = _a.Index.ByClass("BlueprintGeneratedClass").Concat(_a.Index.ByClass("Blueprint")).ToList();
        var primary = classes.Where(e => nameRx.IsMatch(e.AssetName)).OrderBy(e => IsContainer(e) ? 0 : 1).ThenBy(e => e.AssetName.Length).ToList();
        var secondary = classes.Where(e => IsContainer(e) && fallbackRx.IsMatch(e.AssetName) && (exclude == null || !exclude.IsMatch(e.AssetName)) && !primary.Contains(e))
            .OrderBy(e => e.AssetName.Length).ToList();
        Info($"{row.Id}: chest classes by {nameRx}: [{string.Join(", ", primary.Take(15).Select(e => e.AssetName))}]; " +
             $"{_opt.ChestNativeParent} children by {fallbackRx}: [{string.Join(", ", secondary.Take(15).Select(e => e.AssetName))}]");
        var cands = primary.Concat(secondary).ToList();
        if (cands.Count == 0)
            foreach (var n in _a.Index.FileNames.Where(n => nameRx.IsMatch(n)).Take(20))
                foreach (var f in _a.Index.FilesNamed(n))
                {
                    var (pkg, name) = AssetIndex.SplitObjectPath(AssetIndex.ObjectPathForFile(f));
                    cands.Add(new AssetEntry(pkg, name! + "_C", "BlueprintGeneratedClass", new Dictionary<string, string>(), "file name"));
                }
        foreach (var e in cands.DistinctBy(e => e.PackageName).Take(_opt.MaxCandidates))
        {
            var path = e.AssetClass == "Blueprint" ? $"{e.PackageName}.{e.AssetName}_C" : e.ObjectPath;
            var cls = _a.TryLoad<UStruct>(path, out var err);
            if (cls == null) { Info($"{row.Id}: {path}: {err}"); continue; }
            all.Add((cls, path));
        }
        var first = all.FirstOrDefault();
        return (first.Class, first.Class == null ? "no chest blueprint found" : first.Name);
    }

    public void Chest(SourceRow row)
    {
        var (_, note) = FindChestClasses(row, out var all);
        var tried = new List<string>();
        foreach (var (cls, path) in all)
        {
            if (TryBlueprintMesh(row, cls, null, _opt.ChestSize, m => Placement.PlaceVehicle(m, longAxisAlongZ: false), path, tried))
            {
                _chest = (cls, path);
                return;
            }
        }
        Report(row, tried, note);
    }

    // =================================================================== sounds

    private bool TryWriteSound(SourceRow row, ILoadableObject start, string how, List<string> tried, ref bool sawUnsupported)
    {
        var waves = SoundGraph.Waves(start);
        if (waves.Count == 0) { tried.Add($"{how}: no SoundWave behind it"); return false; }
        foreach (var w in waves.Take(6))
        {
            var res = SoundConvert.FromWave(w);
            Info($"{row.Id}: {how}: {res.Detail}");
            if (res.Ogg != null)
            {
                _ctx.Sound(row.Id, res.Ogg, $"{how}; {res.Detail}");
                return true;
            }
            if (res.Unsupported) sawUnsupported = true;
            tried.Add(res.Detail);
        }
        return false;
    }

    public void ChestSound(SourceRow row, SourceRow chestRow)
    {
        var tried = new List<string>();
        bool unsupported = false;
        var classes = new List<(UStruct Class, string Name)>();
        if (_chest is { Class: { } c } ch) classes.Add((c, ch.Note));
        else { FindChestClasses(chestRow, out var all); classes.AddRange(all); }
        foreach (var (cls, name) in classes.Take(4))
        {
            foreach (var prop in new[] { "SearchingSoundCueLoop", "LootRepeatSoundCue", "OpenSound", "SearchedSound" })
            {
                var tag = Blueprints.DefaultProperty(cls, prop);
                if (tag?.Tag?.GenericValue is ILoadableObject target && target is not FPackageIndex { IsNull: true })
                {
                    if (TryWriteSound(row, target, $"{name}.{prop}", tried, ref unsupported)) return;
                }
                else { tried.Add($"{name}.{prop}: not set"); Info($"{row.Id}: {name}.{prop}: not set"); }
            }
        }
        if (TryRegexSounds(row, ref unsupported, tried)) return;
        Report(row, tried, "no chest sound found", unsupported);
    }

    private static readonly string[] UseWords = ["PrimaryFire", "Use", "Consume", "Drink", "Apply", "Activate", "Heal", "Fire"];
    private static readonly string[] LastResortWords = ["Pickup", "Drop", "Land", "Impact", "Reload", "Equip"];

    public void ItemSound(SourceRow row, IEnumerable<SourceRow> itemRows)
    {
        var tried = new List<string>();
        bool unsupported = false;
        var lastResort = new List<(FoundRef Ref, string Item)>();
        foreach (var ir in itemRows)
        {
            var (item, _) = FindItem(ir);
            if (item == null) { tried.Add($"{ir.Id}: item not found"); continue; }
            var refs = PropertyWalker.Refs(item).Where(SoundGraph.LooksLikeSound).ToList();
            Info($"{row.Id}: sound references of {item.Name}: [{string.Join("; ", refs.Take(16))}]");
            int Rank(FoundRef r)
            {
                var text = r.Path;
                for (int i = 0; i < UseWords.Length; i++) if (text.Contains(UseWords[i], StringComparison.OrdinalIgnoreCase)) return i;
                return LastResortWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 100 : 50;
            }
            foreach (var r in refs.OrderBy(Rank))
            {
                if (Rank(r) >= 100) { lastResort.Add((r, item.Name)); continue; }
                if (TryWriteSound(row, r.Target, $"{item.Name} {r.Path}", tried, ref unsupported)) return;
            }
        }
        if (TryRegexSounds(row, ref unsupported, tried)) return;
        foreach (var (r, itemName) in lastResort.Take(4))
            if (TryWriteSound(row, r.Target, $"{itemName} {r.Path} (last resort)", tried, ref unsupported)) return;
        Report(row, tried, "no use sound found", unsupported);
    }

    private bool TryRegexSounds(SourceRow row, ref bool unsupported, List<string> tried)
    {
        var rx = RecipeBook.InlineRegex(row.Fallback);
        if (rx == null) return false;
        var waves = _a.Index.ByClass("SoundWave").Where(e => rx.IsMatch(e.AssetName) || rx.IsMatch(e.PackageName)).ToList();
        if (waves.Count == 0)
            foreach (var n in _a.Index.FileNames.Where(n => rx.IsMatch(n)).Take(40))
                foreach (var f in _a.Index.FilesNamed(n))
                {
                    var (pkg, name) = AssetIndex.SplitObjectPath(AssetIndex.ObjectPathForFile(f));
                    waves.Add(new AssetEntry(pkg, name!, "?", new Dictionary<string, string>(), "file name"));
                }
        static float Dur(AssetEntry e) => float.TryParse(e.Tag("Duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : -1;
        var ordered = waves.OrderBy(e => Dur(e) is > 0.15f and < 6f ? 0 : Dur(e) < 0 ? 1 : 2).ThenBy(e => Math.Abs(Dur(e) - 1.5f)).ThenBy(e => e.AssetName.Length).ToList();
        Info($"{row.Id}: SoundWaves matching {rx}: [{string.Join(", ", ordered.Take(15).Select(e => $"{e.AssetName} ({Dur(e):0.##} s)"))}]");
        foreach (var e in ordered.Take(10))
        {
            var w = _a.TryLoad(e.ObjectPath, out var err);
            if (w == null) { tried.Add($"{e.AssetName}: {err}"); continue; }
            if (TryWriteSound(row, new LoadedRef(w), $"{e.ObjectPath} (name fallback {rx})", tried, ref unsupported)) return true;
        }
        return false;
    }

    private void Report(SourceRow row, List<string> tried, string what, bool unsupported = false)
    {
        var detail = tried.Count == 0 ? $"{what} (no candidates in this install)"
            : $"{what}; tried: {string.Join("; ", tried.Take(10))}{(tried.Count > 10 ? $"; … ({tried.Count} attempts)" : "")}";
        // nothing usable found / codec without a decoder -> Missing; candidates found but conversion broke -> Failed
        bool anyConverted = tried.Any(t => t.Contains("failed", StringComparison.OrdinalIgnoreCase) || t.Contains("outside", StringComparison.OrdinalIgnoreCase));
        if (tried.Count == 0 || unsupported || !anyConverted) _ctx.Missing(row.Id, detail);
        else _ctx.Failed(row.Id, detail);
    }

    /// <summary>An already-loaded object as an ILoadableObject (for the sound graph).</summary>
    private sealed class LoadedRef(UObject obj) : ILoadableObject
    {
        public Type? GetObjectType() => obj.GetType();
        public UObject? Load() => obj;
        public bool TryLoad(out UObject export) { export = obj; return true; }
        public Task<UObject?> LoadAsync() => Task.FromResult<UObject?>(obj);
        public Task<UObject?> TryLoadAsync() => Task.FromResult<UObject?>(obj);
    }
}
