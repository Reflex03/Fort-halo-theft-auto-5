using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Objects.UObject;

namespace CthExtract.Fortnite.Unreal;

/// <summary>Finds the SoundWaves behind a sound reference: a SoundWave itself, the wave players of a SoundCue graph,
/// or the waves a MetaSound / sound library references.</summary>
public static class SoundGraph
{
    public static List<USoundWave> Waves(ILoadableObject start, int maxLoads = 64)
    {
        var waves = new List<USoundWave>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int loads = 0;
        var queue = new Queue<(ILoadableObject Ref, int Depth)>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0 && loads < maxLoads)
        {
            var (r, depth) = queue.Dequeue();
            UObject? obj;
            try { obj = r.Load(); loads++; } catch { continue; }
            if (obj == null) continue;
            var key = obj.GetPathName();
            if (!seen.Add(key)) continue;
            switch (obj)
            {
                case USoundWave w when obj is not USoundWaveProcedural:
                    waves.Add(w);
                    break;
                case USoundCue cue:
                    if (cue.FirstNode is { IsNull: false } first) queue.Enqueue((first, depth + 1));
                    break;
                case USoundNodeWavePlayer wp:
                    if (wp.SoundWave is { IsNull: false } hw) queue.Enqueue((hw, depth + 1));
                    else if (!wp.SoundWaveAssetPtr.AssetPathName.IsNone) queue.Enqueue((wp.SoundWaveAssetPtr, depth + 1));
                    break;
                case USoundNode node:
                    foreach (var c in node.ChildNodes ?? []) if (c is { IsNull: false }) queue.Enqueue((c, depth + 1));
                    foreach (var fr in PropertyWalker.Refs(node, 3)) if (fr.PropertyName != "ChildNodes") queue.Enqueue((fr.Target, depth + 1));
                    break;
                default:
                    // MetaSoundSource, sound libraries and other containers: follow their references a few levels
                    if (depth < 3)
                        foreach (var fr in PropertyWalker.Refs(obj, 16)) // MetaSound documents nest wave literals deeply
                            if (LooksLikeSound(fr)) queue.Enqueue((fr.Target, depth + 1));
                    break;
            }
        }
        return waves;
    }

    /// <summary>Cheap filter so container walks do not load every texture/material they mention.</summary>
    public static bool LooksLikeSound(FoundRef r)
    {
        var cls = r.KnownClass;
        if (cls != null) return cls.Contains("Sound", StringComparison.OrdinalIgnoreCase) || cls.Contains("MetaSound", StringComparison.OrdinalIgnoreCase);
        var p = r.TargetPath;
        int slash = p.LastIndexOf('/');
        var leaf = slash >= 0 ? p[(slash + 1)..] : p;
        return leaf.StartsWith("S_", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("SC_", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("SW_", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("MS_", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("A_", StringComparison.OrdinalIgnoreCase) || leaf.Contains("Sound", StringComparison.OrdinalIgnoreCase)
               || leaf.Contains("Audio", StringComparison.OrdinalIgnoreCase) || leaf.Contains("_Cue", StringComparison.OrdinalIgnoreCase)
               || r.PropertyName.Contains("Sound", StringComparison.OrdinalIgnoreCase) || r.PropertyName.Contains("Audio", StringComparison.OrdinalIgnoreCase);
    }

    public static float Duration(USoundWave w) => w.GetOrDefault("Duration", 0f);
}
