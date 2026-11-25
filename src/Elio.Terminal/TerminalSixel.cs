using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Elio.Terminal.Native;

namespace Elio.Terminal;

// The part of a session that shows sixel images, which the engine does not take itself, and tells programs that it does.
public sealed partial class TerminalSession
{
    // Kitty clients send at most 4096 characters of base64 in one command, and this many bytes make exactly that.
    private const int SixelChunkBytes = 3072;

    private readonly SixelFilter _sixel;

    private unsafe void WriteToEngine(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data) Write(pointer, (nuint)data.Length);
    }

    /// <summary>
    /// Makes the decoder for a sixel image that starts. Pixels it does not paint get the background colour, unless it
    /// asked for them to stay clear. The engine refuses a picture larger than it may store in all, so the image is held
    /// to that from the start, rather than decoded only to be thrown away.
    /// </summary>
    private unsafe SixelDecoder BeginSixel(bool clear)
    {
        Ghostty.Rgb background = default;
        uint? fill = clear || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataColorBackground, &background) != Ghostty.Success ? null : background.ToUInt32();

        ulong storage = 0;
        long maxPixels = Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataKittyImageStorageLimit, &storage) == Ghostty.Success
            ? (long)Math.Min(storage / 4, long.MaxValue)
            : long.MaxValue;

        return new SixelDecoder(fill, maxPixels);
    }

    /// <summary>
    /// Puts a decoded sixel image on screen. Called with the terminal held, from the middle of <see cref="Feed"/>: all
    /// output before the image has reached the engine, so the cursor is where the image goes.
    /// </summary>
    private void ShowSixel(ImagePixels image)
    {
        // The engine draws pictures sent with the Kitty graphics protocol, so the image goes to it as one: plain RGBA in
        // base64, a chunk to a command. With no id given, the engine picks one that no program will. C=1 keeps the cursor
        // where it is, and q=2 keeps the engine from answering, since the program never asked it anything.
        Span<byte> command = stackalloc byte[64 + 4096];
        ReadOnlySpan<byte> rgba = image.Rgba;
        for (int at = 0; at < rgba.Length; at += SixelChunkBytes)
        {
            ReadOnlySpan<byte> chunk = rgba.Slice(at, Math.Min(SixelChunkBytes, rgba.Length - at));
            int more = at + chunk.Length < rgba.Length ? 1 : 0;
            int length;
            if (at == 0) Utf8.TryWrite(command, $"\u001b_Ga=T,f=32,s={image.Width},v={image.Height},C=1,q=2,m={more};", out length);
            else Utf8.TryWrite(command, $"\u001b_Gq=2,m={more};", out length);

            Base64.EncodeToUtf8(chunk, command[length..], out _, out int encoded);
            length += encoded;
            command[length++] = 0x1B;
            command[length++] = (byte)'\\';
            WriteToEngine(command[..length]);
        }

        // The cursor then goes below the image, in the column it started in, which is where xterm leaves it with sixel
        // scrolling on. Index moves down a row without changing the column, and at the bottom it scrolls the screen,
        // image and all.
        int rows = _cellHeightPixels > 0 ? (image.Height + _cellHeightPixels - 1) / _cellHeightPixels : 1;
        for (int done = 0; done < rows;)
        {
            int count = Math.Min(rows - done, command.Length / 2);
            for (int i = 0; i < count; i++)
            {
                command[2 * i] = 0x1B;
                command[(2 * i) + 1] = (byte)'D';
            }
            WriteToEngine(command[..(count * 2)]);
            done += count;
        }
    }

    // Runs inside the engine's write when a program asks what this terminal is (CSI c). The answer is the engine's own,
    // a VT220 with colour, plus 4 for sixel graphics, which is what programs look for before they send sixel images.
    [UnmanagedCallersOnly]
    private static unsafe byte OnEngineDeviceAttributes(nint terminal, nint userdata, Ghostty.DeviceAttributes* attributes)
    {
        attributes->ConformanceLevel = 62;
        attributes->Features[0] = 4;
        attributes->Features[1] = 22;
        attributes->FeatureCount = 2;
        attributes->DeviceType = 1;
        attributes->FirmwareVersion = 0;
        attributes->RomCartridge = 0;
        attributes->UnitId = 0;
        return 1;
    }

    // Runs inside the engine's write when a program asks for the size of the window or a cell in pixels (CSI 14 t,
    // 16 t), which is how it sizes a picture to the text around it. Unanswered until the view has said how large a cell is.
    [UnmanagedCallersOnly]
    private static unsafe byte OnEngineSize(nint terminal, nint userdata, Ghostty.SizeReport* size)
    {
        var session = (TerminalSession)GCHandle.FromIntPtr(userdata).Target!;
        if (session._cellWidthPixels <= 0 || session._cellHeightPixels <= 0) return 0;

        size->Rows = (ushort)session._rows;
        size->Columns = (ushort)session._columns;
        size->CellWidth = (uint)session._cellWidthPixels;
        size->CellHeight = (uint)session._cellHeightPixels;
        return 1;
    }
}
