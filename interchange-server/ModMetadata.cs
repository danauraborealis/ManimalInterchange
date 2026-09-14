using JetBrains.Annotations;
using Manimal.Interchange.Shared;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace Manimal.Interchange.Server;

[UsedImplicitly]
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = ModIdentity.Guid;
    public string Name { get; init; } = ModIdentity.ServerName;
    public string Author { get; init; } = ModIdentity.Author;
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new(ModIdentity.Version);
    public Range SptVersion { get; init; } = new(ModIdentity.SupportedSptRange);
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new Range("~3.0.6"),
        ["com.wtt.contentbackport"] = new Range("~2.0.2")
    };
    public string? Url { get; init; } = string.IsNullOrEmpty(ModIdentity.SourceUrl) ? null : ModIdentity.SourceUrl;
    public string License { get; init; } = "All rights reserved";
}
