using System.Reflection;
using System.Threading;
using SPTarkov.Server.Core.Utils.Json;

namespace Manimal.Interchange.Server;

internal static class InterchangeLazyLoadCompatibility
{
    /// <summary>
    /// Copies the source callback snapshot into the replacement during startup. Callbacks added to the
    /// source after this snapshot remain attached to the stale source instance.
    /// </summary>
    public static void PreserveTransformers<T>(LazyLoad<T>? source, LazyLoad<T>? destination)
    {
        if (source is null || destination is null || ReferenceEquals(source, destination))
        {
            return;
        }

        foreach (var transformer in Accessor<T>.Snapshot(source))
        {
            destination.AddTransformer(transformer);
        }
    }

    private static class Accessor<T>
    {
        // SPT 4.1.5 has no public API for transferring LazyLoad callbacks, so these private fields
        // are validated once per closed LazyLoad<T> type before they are used.
        private static readonly FieldInfo TransformersField = ResolveField(
            "_lazyLoadTransformers",
            typeof(List<Func<T?, T?>>));
        private static readonly FieldInfo TransformersLockField = ResolveField(
            "_lazyLoadTransformersLock",
            typeof(ReaderWriterLockSlim));

        public static Func<T?, T?>[] Snapshot(LazyLoad<T> source)
        {
            if (TransformersField.GetValue(source) is not List<Func<T?, T?>> transformers)
            {
                throw new InvalidOperationException(
                    $"Unsupported {typeof(LazyLoad<T>)} layout: field '{TransformersField.Name}' is not the expected transformer list.");
            }

            if (TransformersLockField.GetValue(source) is not ReaderWriterLockSlim transformerLock)
            {
                throw new InvalidOperationException(
                    $"Unsupported {typeof(LazyLoad<T>)} layout: field '{TransformersLockField.Name}' is not the expected read/write lock.");
            }

            transformerLock.EnterReadLock();
            try
            {
                return transformers.ToArray();
            }
            finally
            {
                transformerLock.ExitReadLock();
            }
        }

        private static FieldInfo ResolveField(string name, Type expectedType)
        {
            var lazyLoadType = typeof(LazyLoad<T>);
            var field = lazyLoadType.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (field is null || !field.IsPrivate || !field.IsInitOnly || field.FieldType != expectedType)
            {
                throw new InvalidOperationException(
                    $"Unsupported {lazyLoadType} layout: expected private readonly field '{name}' of type {expectedType}.");
            }

            return field;
        }
    }
}
