using System;
using System.Collections.Generic;

namespace Manimal.Interchange.Shared;

[Serializable]
public sealed class ContentManifest
{
    public int Schema = 1;
    public string ContentId = "";
    public string ModVersion = ModIdentity.Version;
    public string TargetClientBuild = ModIdentity.TargetClientBuild;
    public string SourceBuild = "";
    public string Mode = "development";
    public bool Ready;
    public List<SceneEntry> Scenes = new();
    public List<PayloadFile> Bundles = new();
    public List<PayloadFile> Sidecars = new();
    public List<PayloadFile> ServerFiles = new();
    public List<PayloadFile> ItemFiles = new();
    public List<PayloadFile> NativeFiles = new();
    public Dictionary<string, bool> Features = new();
}

[Serializable]
public sealed class SceneEntry
{
    public string OriginalPath = "";
    public string ReplacementPath = "";
    public bool OnlyOffline;
}

[Serializable]
public sealed class PayloadFile
{
    public string Path = "";
    public string Sha256 = "";
    public long Bytes;
}

[Serializable]
public sealed class ServerCapability
{
    public int Schema = 1;
    public string ModVersion = ModIdentity.Version;
    public string ContentId = "";
    public string ManifestSha256 = "";
    public string Selection = "native";
    public bool Ready;
    public string Error = "";
}
