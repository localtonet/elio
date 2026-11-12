using System.Runtime.InteropServices;
using Elio.Terminal.Native;

namespace Elio.Terminal;

/// <summary>What the engine needs from outside to show pictures.</summary>
public static unsafe class TerminalImages
{
    /// <summary>
    /// The most pixels one picture may have. Far more than any screen shows, and small enough that one picture
    /// cannot take the app down. A decoder has to check the size a file claims against this before it decodes.
    /// </summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    private static Func<ReadOnlyMemory<byte>, ImagePixels?>? _decodePng;

    /// <summary>
    /// Turns a PNG file into pixels. The engine has no image library of its own, so without this a program
    /// that sends a PNG has it refused; pictures sent as plain pixels work either way. Set once, at startup.
    /// </summary>
    public static Func<ReadOnlyMemory<byte>, ImagePixels?>? DecodePng
    {
        get => _decodePng;
        set
        {
            _decodePng = value;
            delegate* unmanaged<void*, nint, byte*, nuint, Ghostty.SysImage*, byte> callback = &OnDecodePng;
            Ghostty.SysSet(Ghostty.SysOptDecodePng, value is null ? null : callback);
        }
    }

    // The picture comes from whatever is running on the server, so nothing here may throw into the engine.
    [UnmanagedCallersOnly]
    private static byte OnDecodePng(void* userdata, nint allocator, byte* data, nuint length, Ghostty.SysImage* result)
    {
        try
        {
            if (_decodePng is not { } decode || length == 0 || length > int.MaxValue) return 0;

            ImagePixels? decoded = decode(new ReadOnlySpan<byte>(data, (int)length).ToArray());
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0 || (long)decoded.Width * decoded.Height > MaxPixels || decoded.Rgba.Length != (long)decoded.Width * decoded.Height * 4) return 0;

            // The engine owns this memory from here on, which is why it comes from its allocator.
            byte* copy = Ghostty.Alloc(allocator, (nuint)decoded.Rgba.Length);
            if (copy is null) return 0;

            decoded.Rgba.CopyTo(new Span<byte>(copy, decoded.Rgba.Length));
            result->Width = (uint)decoded.Width;
            result->Height = (uint)decoded.Height;
            result->Data = copy;
            result->Length = (nuint)decoded.Rgba.Length;
            return 1;
        }
        catch (Exception)
        {
            // Running out of memory included: an exception that leaves this method ends the process, and the engine
            // only has to be told that this one picture could not be read.
            return 0;
        }
    }
}
