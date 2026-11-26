namespace Iris.Numerics;

/// <summary>
/// How much memory outside the managed heap the numerics hold now (the activations of the learning, packed panels):
/// counted where it is allocated and freed, so a diagnosis can tell a growing native part from a growing heap.
/// </summary>
public static class NativeUse
{
    private static long _bytes;

    /// <summary>Bytes held now.</summary>
    public static long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Bytes allocated (positive) or freed (negative).</summary>
    public static void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);
}
