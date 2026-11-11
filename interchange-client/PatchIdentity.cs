using System;
using Manimal.Interchange.Shared;

namespace Manimal.Interchange.Client;

internal static class PatchIdentity
{
    internal static string For(Type patchType)
        => ModIdentity.Guid + "." + (patchType.FullName ?? throw new InvalidOperationException("Patch type has no full name: " + patchType));
}
