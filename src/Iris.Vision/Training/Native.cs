using System.Runtime.InteropServices;

namespace Iris.Vision.Training;

/// <summary>
/// A buffer outside the managed heap, 64-byte aligned, that only grows: the activations of a batch take hundreds of
/// megabytes and are needed again in every step, so they are allocated once and reused – no garbage collection in
/// the learning loop, no pinning for the product kernels.
/// </summary>
internal sealed unsafe class Buffer<T> : IDisposable
    where T : unmanaged
{
    private T* _data;
    private long _capacity;

    public T* Data => _data;

    public long Capacity => _capacity;

    public Span<T> Span(long count) => new(_data, checked((int)count));

    /// <summary>At least count elements (old contents are not kept); clear: zeros.</summary>
    public T* Ensure(long count, bool clear = false)
    {
        if (count > _capacity)
        {
            NativeMemory.AlignedFree(_data);
            // a little more than asked, so a slightly wider batch does not allocate again
            var capacity = Math.Max(count, _capacity + _capacity / 4);
            _data = (T*)NativeMemory.AlignedAlloc((nuint)(capacity * sizeof(T)), 64);
            _capacity = capacity;
        }

        if (clear)
        {
            NativeMemory.Clear(_data, (nuint)(count * sizeof(T)));
        }

        return _data;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_data);
        _data = null;
        _capacity = 0;
    }
}

/// <summary>
/// The lines of a batch at one size: every line has Height rows and its own width, kept row after row one line after
/// the other ([pixels, channels] with the channels together), so a whole batch is one matrix for the products.
/// </summary>
internal sealed unsafe class Level : IDisposable
{
    private readonly Buffer<int> _lineOfPixel = new();

    public int Height { get; private set; }

    public int[] Widths { get; private set; } = [];

    /// <summary>The first pixel of each line.</summary>
    public int[] Offsets { get; private set; } = [];

    public int Pixels { get; private set; }

    public int* LineOfPixel => _lineOfPixel.Data;

    public void Set(int height, ReadOnlySpan<int> widths)
    {
        Height = height;
        if (Widths.Length != widths.Length)
        {
            Widths = new int[widths.Length];
            Offsets = new int[widths.Length];
        }

        var pixels = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            Widths[i] = widths[i];
            Offsets[i] = pixels;
            pixels += height * widths[i];
        }

        Pixels = pixels;
        var lines = _lineOfPixel.Ensure(pixels);
        for (var i = 0; i < widths.Length; i++)
        {
            new Span<int>(lines + Offsets[i], height * widths[i]).Fill(i);
        }
    }

    public void Dispose() => _lineOfPixel.Dispose();
}
