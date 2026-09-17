using System.Text.Json;
using Manimal.Interchange.Shared;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace Manimal.Interchange.Server;

public static class InterchangeLocationData
{
    private static readonly string[] Names = 
    [
        "base.json", 
        "looseLoot.json", 
        "staticLoot.json", 
        "staticContainers.json", 
        "staticAmmo.json", 
        "statics.json", 
        "allExtracts.json"
    ];

    public static Location Read(string root, ContentManifest manifest, JsonUtil json, Dictionary<MongoId, TemplateItem> templates, LooseLoot? baseline = null)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in Names)
        {
            var relative = "db/locations/interchange/" + name;
            PayloadFile? declaration = null;

            foreach (var file in manifest.ServerFiles)
            {
                if (file.Path != relative) { continue; }

                declaration = file;
                break;
            }

            if (declaration is null)
            {
                throw new InvalidDataException("Missing Interchange database declaration: " + relative);
            }

            ManifestRules.VerifyFile(root, declaration);

            var text = File.ReadAllText(ManifestRules.Resolve(root, relative));
            using var document = JsonDocument.Parse(text);

            ValidateTemplates(document.RootElement, templates);
            texts.Add(name, text);
        }

        _ = ReadLooseLoot();
        _ = Parse<StaticContainerDetails>("staticContainers.json");

        var loot = Parse<Dictionary<MongoId, StaticLootDetails>>("staticLoot.json");

        foreach (var identity in loot.Keys)
        {
            if (!templates.ContainsKey(identity))
            {
                throw new InvalidDataException("Unknown static container template: " + identity);
            }
        }

        return new Location
        {
            Base = Parse<LocationBase>("base.json"),
            LooseLoot = new LazyLoad<LooseLoot>(ReadLooseLoot, false),
            StaticContainers = new LazyLoad<StaticContainerDetails>(() => Parse<StaticContainerDetails>("staticContainers.json"), false),
            StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => Parse<Dictionary<MongoId, StaticLootDetails>>("staticLoot.json"), false),
            StaticAmmo = Parse<Dictionary<string, IEnumerable<StaticAmmoDetails>>>("staticAmmo.json"),
            Statics = Parse<StaticContainer>("statics.json"),
            AllExtracts = Parse<IEnumerable<AllExtractsExit>>("allExtracts.json")
        };

        T Parse<T>(string name) => json.Deserialize<T>(texts[name]) ?? throw new InvalidDataException("Null Interchange database: " + name);
        LooseLoot ReadLooseLoot()
        {
            var loot = Parse<LooseLoot>("looseLoot.json");
            return baseline is null ? loot : Manimal.MapBackport.LegacyLootCompatibility.Reconcile(loot, baseline, templates, json);
        }
    }

    private static void ValidateTemplates(JsonElement value, Dictionary<MongoId, TemplateItem> templates)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                foreach (var field in value.EnumerateObject())
                {
                    if (field.Name is "_tpl" or "tpl" && field.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = new MongoId(field.Value.GetString()!);

                        if (!templates.ContainsKey(id))
                        {
                            throw new InvalidDataException("Missing Interchange loot template: " + id);
                        }
                    }
                    else
                    {
                        ValidateTemplates(field.Value, templates);
                    }
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var child in value.EnumerateArray())
                {
                    ValidateTemplates(child, templates);
                }

                break;
            }
        }
    }

    public static void Apply(Location target, Location replacement)
    {
        InterchangeLazyLoadCompatibility.PreserveTransformers(target.LooseLoot, replacement.LooseLoot);
        InterchangeLazyLoadCompatibility.PreserveTransformers(target.StaticLoot, replacement.StaticLoot);
        InterchangeLazyLoadCompatibility.PreserveTransformers(target.StaticContainers, replacement.StaticContainers);

        target.Base = replacement.Base;
        target.LooseLoot = replacement.LooseLoot;
        target.StaticLoot = replacement.StaticLoot;
        target.StaticContainers = replacement.StaticContainers;
        target.StaticAmmo = replacement.StaticAmmo;
        target.Statics = replacement.Statics;
        target.AllExtracts = replacement.AllExtracts;
    }

    /// <summary>Retains the exact original lazy-load instances for startup rollback.</summary>
    public static Location Snapshot(Location source) => new()
    {
        Base = source.Base, LooseLoot = source.LooseLoot, StaticLoot = source.StaticLoot,
        StaticContainers = source.StaticContainers, StaticAmmo = source.StaticAmmo,
        Statics = source.Statics, AllExtracts = source.AllExtracts
    };

    public static void Restore(Location target, Location snapshot)
    {
        target.Base = snapshot.Base; target.LooseLoot = snapshot.LooseLoot;
        target.StaticLoot = snapshot.StaticLoot; target.StaticContainers = snapshot.StaticContainers;
        target.StaticAmmo = snapshot.StaticAmmo; target.Statics = snapshot.Statics; target.AllExtracts = snapshot.AllExtracts;
    }
}
