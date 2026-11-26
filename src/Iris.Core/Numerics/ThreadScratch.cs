using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iris.Numerics;

/// <summary>
/// Scratch memory a thread keeps for itself from call to call (the packed panels of a product, the tiles of a
/// convolution): a pinned array held in a thread-static field. When the thread ends – the thread pool lets idle
/// threads go after some seconds, and the learning rests every day – the collector frees the array with it; native
/// memory behind a thread-static pointer would stay allocated for ever, a little more with every thread that ended.
/// </summary>
internal static unsafe class ThreadScratch
{
    private const int Align = 64;

    /// <summary>At least the needed floats, the start aligned to 64 bytes (the array is pinned, it never moves).</summary>
    public static float* Get(ref float[]? array, long needed)
    {
        var length = checked((int)needed + Align / sizeof(float));
        if (array is null || array.Length < length)
        {
            array = null; // the smaller one goes to the collector before the bigger one is made
            array = GC.AllocateUninitializedArray<float>(length, pinned: true);
        }

        var start = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        return (float*)((start + (Align - 1)) & ~(nint)(Align - 1));
    }
}
