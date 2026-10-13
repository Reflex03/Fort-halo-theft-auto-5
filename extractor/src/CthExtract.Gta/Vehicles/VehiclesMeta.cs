using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CthExtract.Gta.Vehicles;

/// <summary>
/// What we need from vehicles.meta (XML): each model's txdName, and the txdRelationships parent chain
/// (e.g. sultan -> vehicles_sultan_interior -> vehshare). Several files can be merged (base, update, DLC).
/// </summary>
public sealed class VehiclesMeta
{
    public Dictionary<string, string> TxdOfModel { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ParentOfTxd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Sources { get; } = new();

    public void Merge(byte[] bytes, string source)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿', '\0', ' ', '\r', '\n', '\t');
        bool parsed = false;
        try
        {
            var doc = XDocument.Parse(text);
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "InitDatas").SelectMany(e => e.Elements()))
            {
                var model = item.Element("modelName")?.Value.Trim();
                var txd = item.Element("txdName")?.Value.Trim();
                if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(txd)) TxdOfModel[model] = txd;
            }
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "txdRelationships").SelectMany(e => e.Elements()))
            {
                var parent = item.Element("parent")?.Value.Trim();
                var child = item.Element("child")?.Value.Trim();
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(child)) ParentOfTxd[child] = parent;
            }
            parsed = true;
        }
        catch (System.Xml.XmlException) { }
        if (!parsed)
        {
            // tolerant fallback for slightly broken XML
            foreach (Match m in Regex.Matches(text, @"<Item>\s*<parent>\s*([^<\s]+)\s*</parent>\s*<child>\s*([^<\s]+)\s*</child>", RegexOptions.IgnoreCase))
                ParentOfTxd[m.Groups[2].Value] = m.Groups[1].Value;
            foreach (Match m in Regex.Matches(text, @"<modelName>\s*([^<\s]+)\s*</modelName>\s*<txdName>\s*([^<\s]+)\s*</txdName>", RegexOptions.IgnoreCase))
                TxdOfModel[m.Groups[1].Value] = m.Groups[2].Value;
        }
        Sources.Add(source + (parsed ? "" : " (regex fallback)"));
    }

    /// <summary>Texture dictionaries to search for a model, most specific first: its txd, the +hi variant, the
    /// txdRelationships parents, then vehshare.</summary>
    public List<string> TxdChain(string model)
    {
        var list = new List<string>();
        var txd = TxdOfModel.GetValueOrDefault(model, model);
        list.Add(txd);
        list.Add(txd + "+hi");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { txd };
        for (var cur = txd; ParentOfTxd.TryGetValue(cur, out var p) && seen.Add(p); cur = p) list.Add(p);
        if (!list.Contains("vehshare", StringComparer.OrdinalIgnoreCase)) list.Add("vehshare");
        return list;
    }
}
