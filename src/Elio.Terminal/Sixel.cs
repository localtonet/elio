using System.Numerics;
using System.Runtime.InteropServices;

namespace Elio.Terminal;

/// <summary>
/// Takes sixel images out of terminal output on its way to the engine, which has no sixel support of its own and would
/// only swallow them. Everything else goes on unchanged and in order. Each image is decoded as its data arrives, so a
/// large one is never held as text, and handed over when it ends.
/// </summary>
/// <remarks>
/// An image starts with DCS, its numbers and q (ESC P 0;1;0 q), and ends with ST (ESC \). Like the engine,
/// this reads output as UTF-8 and takes no 8-bit C1 controls, and takes any other escape as the end of the image too.
/// CAN and SUB cancel it.
/// </remarks>
/// <param name="pass">Takes the output that goes on to the engine.</param>
/// <param name="show">Takes each image that ended with pixels in it.</param>
/// <param name="begin">Makes the decoder for an image that starts, told whether the image wants unpainted pixels left clear.</param>
internal sealed class SixelFilter(Action<ReadOnlySpan<byte>> pass, Action<ImagePixels> show, Func<bool, SixelDecoder> begin)
{
    /// <summary>The most data one image may have. Past it the image is given up and the rest of it read and dropped.</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    // What may come between ESC P and q. Anything longer is some other string, left for the engine.
    private const int MaxParameterBytes = 32;

    private const byte Esc = 0x1B;
    private const byte Can = 0x18;
    private const byte Sub = 0x1A;

    private enum State
    {
        // Ordinary output, looked through for ESC P.
        Ground,

        // An ESC, or ESC P and numbers, ended the last piece of output, and what comes next says whether it is an image.
        Escape,
        Parameters,

        // Inside an image, or just after an ESC inside one.
        Image,
        ImageEscape,
    }

    private State _state;

    // The start of a string that may yet be an image, held back until the bytes that decide it arrive.
    private readonly byte[] _held = new byte[2 + MaxParameterBytes];
    private int _heldLength;

    // The image being read. Null inside one that was given up, whose data is then only skipped.
    private SixelDecoder? _image;
    private long _imageBytes;

    /// <summary>Takes the next piece of output, which may end anywhere, even in the middle of an image.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        // Where the bytes that go on unchanged begin. They are passed on in one piece when the data runs out or an image starts.
        int run = 0;
        int i = 0;
        while (i < data.Length)
        {
            switch (_state)
            {
                case State.Ground:
                    i = Scan(data, i, run);
                    break;

                case State.Escape:
                    if (data[i] == (byte)'P')
                    {
                        _held[_heldLength++] = data[i++];
                        _state = State.Parameters;
                    }
                    else
                    {
                        // Not a DCS. The ESC goes on, and the byte after it is looked at as ordinary output.
                        Release();
                        run = i;
                    }
                    break;

                case State.Parameters:
                    if (data[i] == (byte)'q')
                    {
                        Begin(_held.AsSpan(2, _heldLength - 2));
                        _heldLength = 0;
                        i++;
                    }
                    else if (IsParameter(data[i]) && _heldLength < _held.Length)
                    {
                        _held[_heldLength++] = data[i++];
                    }
                    else
                    {
                        Release();
                        run = i;
                    }
                    break;

                case State.Image:
                    i = TakeImage(data, i);
                    run = i;
                    break;

                case State.ImageEscape:
                    End();
                    if (data[i] == (byte)'\\')
                    {
                        _state = State.Ground;
                        run = ++i;
                    }
                    else
                    {
                        // Any other escape ends the image as well, and is itself the start of whatever comes next.
                        _held[_heldLength++] = Esc;
                        _state = State.Escape;
                    }
                    break;
            }
        }

        if (_state == State.Ground && run < data.Length) pass(data[run..]);
    }

    private static bool IsParameter(byte value) => value is >= (byte)'0' and <= (byte)'9' or (byte)';';

    /// <summary>Looks through ordinary output from <paramref name="i"/> on. Returns where to carry on from.</summary>
    private int Scan(ReadOnlySpan<byte> data, int i, int run)
    {
        while (true)
        {
            int found = data[i..].IndexOf(Esc);
            if (found < 0) return data.Length;

            int at = i + found;
            int next = at + 1;

            // Most escapes are colours and cursor movement, which carry on the run as they are.
            if (next < data.Length && data[next] != (byte)'P')
            {
                i = next;
                continue;
            }

            int end = next + 1;
            while (end < data.Length && end - next - 1 < MaxParameterBytes && IsParameter(data[end])) end++;

            if (next == data.Length || end == data.Length)
            {
                // The output ends before it says what this is. What came before goes on now, and this waits for the rest.
                if (at > run) pass(data[run..at]);
                data[at..].CopyTo(_held);
                _heldLength = data.Length - at;
                _state = next == data.Length ? State.Escape : State.Parameters;
                return data.Length;
            }

            if (data[end] != (byte)'q')
            {
                // Another kind of DCS, such as DECRQSS or tmux's, which is the engine's business.
                i = end;
                continue;
            }

            if (at > run) pass(data[run..at]);
            Begin(data[(next + 1)..end]);
            return end + 1;
        }
    }

    // The held bytes were not the start of an image after all, so they go on as they came.
    private void Release()
    {
        pass(_held.AsSpan(0, _heldLength));
        _heldLength = 0;
        _state = State.Ground;
    }

    private void Begin(ReadOnlySpan<byte> parameters)
    {
        // The second number says what becomes of the pixels the image does not paint: 1 leaves what is behind them
        // showing, anything else gives them the background colour.
        int index = 0;
        int backgroundSelect = 0;
        foreach (byte value in parameters)
        {
            if (value == (byte)';') index++;
            else if (index == 1) backgroundSelect = Math.Min((backgroundSelect * 10) + (value - '0'), 10);
        }

        _image = begin(backgroundSelect == 1);
        _imageBytes = 0;
        _state = State.Image;
    }

    private int TakeImage(ReadOnlySpan<byte> data, int i)
    {
        int found = data[i..].IndexOfAny(Esc, Can, Sub);
        int end = found < 0 ? data.Length : i + found;
        Decode(data[i..end]);
        if (found < 0) return data.Length;

        if (data[end] == Esc)
        {
            _state = State.ImageEscape;
        }
        else
        {
            _image = null;
            _state = State.Ground;
        }
        return end + 1;
    }

    private void Decode(ReadOnlySpan<byte> data)
    {
        if (_image is null || data.IsEmpty) return;

        _imageBytes += data.Length;
        if (_imageBytes > MaxImageBytes)
        {
            _image = null;
            return;
        }

        _image.Write(data);
        if (_image.Failed) _image = null;
    }

    private void End()
    {
        if (_image?.Finish() is ImagePixels pixels) show(pixels);
        _image = null;
    }
}

/// <summary>
/// Turns the data of a sixel image into pixels as it arrives. The image is drawn in bands six pixels tall, from left
/// to right: each character from ? to ~ is one column of a band, its six low bits the pixels to paint from the top
/// down, in the colour last chosen.
/// </summary>
/// <remarks>
/// The data comes from whatever runs on the server, so the image is held to <see cref="MaxSide"/> pixels each way, to
/// a number of pixels in all, and the painting to a fixed amount. Past any of them the image is given up, and nothing
/// more is done with what follows.
/// </remarks>
internal sealed class SixelDecoder
{
    /// <summary>The widest and tallest an image may be.</summary>
    public const int MaxSide = 4096;

    // As many colour registers as xterm has; a VT340 had 16. Higher numbers wrap around.
    private const int Registers = 1024;

    // How many pixels one image may paint, counting each time one is painted over. Far more than an encoder needs, and
    // it keeps a few bytes that repeat a long run over the same band again and again from keeping the terminal busy.
    private const long MaxPainted = 4L * MaxSide * MaxSide;

    // No number here means anything past this, and stopping at it keeps them from overflowing.
    private const int MaxNumber = 1_000_000;

    // The VT340's colours, which an image starts with, in percent of red, green and blue.
    private static ReadOnlySpan<byte> DefaultColors =>
    [
        0, 0, 0, 20, 20, 80, 80, 13, 13, 20, 80, 20, 80, 20, 80, 20, 80, 80, 80, 80, 20, 53, 53, 53,
        26, 26, 26, 33, 33, 60, 60, 26, 26, 33, 60, 33, 60, 33, 60, 33, 60, 60, 60, 60, 33, 80, 80, 80,
    ];

    private readonly uint[] _palette = new uint[Registers];
    private readonly uint? _background;
    private readonly long _maxPixels;

    // The pixels painted so far, as RGBA in rows of _stride. Zero is a pixel nothing has painted.
    private uint[] _pixels = [];
    private int _stride;
    private int _rows;

    private int _x;
    private int _band;
    private uint _color;
    private int _repeat = 1;
    private long _painted;

    // How far the image reaches: the columns passed, the rows painted, and the size it said it has.
    private int _width;
    private int _height;
    private int _rasterWidth;
    private int _rasterHeight;

    // A command that takes numbers: # (colour), ! (repeat) or " (raster attributes). Zero between commands.
    private byte _command;
    private readonly int[] _numbers = new int[5];
    private int _number;

    /// <param name="background">The colour of pixels the image does not paint, as 0xRRGGBB, or null to leave them transparent.</param>
    /// <param name="maxPixels">The most pixels the whole image may have, for a place to show it that holds less than the largest.</param>
    public SixelDecoder(uint? background, long maxPixels = (long)MaxSide * MaxSide)
    {
        if (background is uint rgb) _background = Pack((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        _maxPixels = Math.Min(maxPixels, (long)MaxSide * MaxSide);

        // The registers past the VT340's are black, and opaque like every colour: zero would be a pixel never painted.
        Array.Fill(_palette, Pack(0, 0, 0));
        for (int i = 0; i < DefaultColors.Length / 3; i++) _palette[i] = FromPercent(DefaultColors[3 * i], DefaultColors[(3 * i) + 1], DefaultColors[(3 * i) + 2]);
        _color = _palette[0];
    }

    /// <summary>The image was too large or too much work, and is not going to be shown.</summary>
    public bool Failed { get; private set; }

    /// <summary>Takes the next piece of the image's data: what comes after the q, without the ST that ends it.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (Failed) return;
            Take(value);
        }
    }

    /// <summary>The image as RGBA, or null when it was given up or has no pixels at all.</summary>
    public ImagePixels? Finish()
    {
        if (_command != 0) EndCommand();
        if (Failed) return null;

        int width = Math.Max(_width, _rasterWidth);
        int height = Math.Max(_height, _rasterHeight);
        if (width == 0 || height == 0 || (long)width * height > _maxPixels) return null;

        byte[] rgba;
        try
        {
            rgba = new byte[width * height * 4];
        }
        catch (OutOfMemoryException)
        {
            // A phone may not have room for the largest image there can be. That one is then not shown.
            return null;
        }

        Span<uint> image = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
        for (int y = 0; y < Math.Min(height, _rows); y++)
            _pixels.AsSpan(y * _stride, Math.Min(width, _stride)).CopyTo(image.Slice(y * width));
        if (_background is uint background) image.Replace(0u, background);

        _pixels = [];
        return new ImagePixels(width, height, rgba);
    }

    private void Take(byte value)
    {
        if (_command != 0)
        {
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                if (_number < _numbers.Length) _numbers[_number] = Math.Min((_numbers[_number] * 10) + (value - '0'), MaxNumber);
                return;
            }

            if (value == (byte)';')
            {
                if (_number < _numbers.Length) _number++;
                return;
            }

            EndCommand();
        }

        if (value is >= (byte)'?' and <= (byte)'~')
        {
            Paint(value - '?');
            return;
        }

        // A repeat applies to the character right after it, or to nothing.
        _repeat = 1;
        switch (value)
        {
            case (byte)'$':
                _x = 0;
                break;

            case (byte)'-':
                _x = 0;
                _band = Math.Min(_band + 6, MaxSide);
                break;

            case (byte)'#' or (byte)'!' or (byte)'"':
                _command = value;
                _number = 0;
                Array.Clear(_numbers);
                break;

            // Spaces, line breaks and anything else mean nothing in the data.
        }
    }

    private void EndCommand()
    {
        int count = _number + 1;
        switch (_command)
        {
            case (byte)'#':
                // #n picks a colour register; #n;1;h;l;s and #n;2;r;g;b set it as well.
                int register = _numbers[0] % Registers;
                if (count >= 5 && _numbers[1] == 1) _palette[register] = FromHls(_numbers[2], _numbers[3], _numbers[4]);
                else if (count >= 5 && _numbers[1] == 2) _palette[register] = FromPercent(_numbers[2], _numbers[3], _numbers[4]);
                _color = _palette[register];
                break;

            case (byte)'!':
                _repeat = Math.Max(1, _numbers[0]);
                break;

            case (byte)'"':
                // "aspect;aspect;width;height. The aspect ratio is left alone: pixels are square, as on every terminal now.
                if (count >= 4)
                {
                    (_rasterWidth, _rasterHeight) = (_numbers[2], _numbers[3]);
                    if (_rasterWidth > MaxSide || _rasterHeight > MaxSide || (long)_rasterWidth * _rasterHeight > _maxPixels) Fail();
                }
                break;
        }

        _command = 0;
    }

    private void Paint(int bits)
    {
        int repeat = _repeat;
        _repeat = 1;

        int end = _x + repeat;
        if (end > MaxSide || Area(end, _height) > _maxPixels)
        {
            Fail();
            return;
        }

        if (bits != 0)
        {
            // One past the lowest pixel this paints.
            int bottom = _band + 32 - BitOperations.LeadingZeroCount((uint)bits);
            _painted += (long)repeat * BitOperations.PopCount((uint)bits);
            if (bottom > MaxSide || Area(end, bottom) > _maxPixels || _painted > MaxPainted || !Reserve(end, bottom))
            {
                Fail();
                return;
            }

            for (int bit = 0; bit < 6; bit++)
            {
                if ((bits & (1 << bit)) != 0) _pixels.AsSpan(((_band + bit) * _stride) + _x, repeat).Fill(_color);
            }
            _height = Math.Max(_height, bottom);
        }

        _x = end;
        _width = Math.Max(_width, _x);
    }

    // How many pixels the image has once it reaches this far.
    private long Area(int width, int height) => (long)Math.Max(Math.Max(_width, width), _rasterWidth) * Math.Max(Math.Max(_height, height), _rasterHeight);

    // Makes room for pixels up to this column and row. False when there is no memory for it.
    private bool Reserve(int width, int height)
    {
        if (width <= _stride && height <= _rows) return true;

        // Straight to the size the image said it has, or else double, so that pixels are moved only a few times.
        int stride = width <= _stride ? _stride : Math.Min(MaxSide, Math.Max(Math.Max(width, _rasterWidth), _stride * 2));
        int rows = height <= _rows ? _rows : Math.Min(MaxSide, Math.Max(Math.Max(height, _rasterHeight), _rows * 2));

        // Near the most pixels allowed there is no room to spare: rows only as wide as the image, and as many as fit.
        if ((long)stride * rows > _maxPixels)
        {
            stride = Math.Max(width, _width);
            rows = (int)Math.Min(MaxSide, _maxPixels / stride);
        }

        uint[] grown;
        try
        {
            grown = new uint[stride * rows];
        }
        catch (OutOfMemoryException)
        {
            return false;
        }

        for (int y = 0; y < Math.Min(_rows, rows); y++) _pixels.AsSpan(y * _stride, Math.Min(_stride, stride)).CopyTo(grown.AsSpan(y * stride));
        (_pixels, _stride, _rows) = (grown, stride, rows);
        return true;
    }

    private void Fail()
    {
        Failed = true;
        _pixels = [];
    }

    // Four bytes in memory, red first and the opaque alpha last, so a pixel can be written as one number.
    private static uint Pack(byte red, byte green, byte blue) =>
        BitConverter.IsLittleEndian
            ? red | ((uint)green << 8) | ((uint)blue << 16) | 0xFF000000u
            : ((uint)red << 24) | ((uint)green << 16) | ((uint)blue << 8) | 0xFFu;

    private static uint FromPercent(int red, int green, int blue)
    {
        static byte Scale(int percent) => (byte)(((Math.Clamp(percent, 0, 100) * 255) + 50) / 100);
        return Pack(Scale(red), Scale(green), Scale(blue));
    }

    private static uint FromHls(int hue, int lightness, int saturation)
    {
        // DEC's colour circle starts at blue where the usual one starts at red, a third of the way round.
        double h = ((Math.Clamp(hue, 0, 360) + 240) % 360) / 60.0;
        double l = Math.Clamp(lightness, 0, 100) / 100.0;
        double s = Math.Clamp(saturation, 0, 100) / 100.0;

        double chroma = (1 - Math.Abs((2 * l) - 1)) * s;
        double x = chroma * (1 - Math.Abs((h % 2) - 1));
        double m = l - (chroma / 2);
        (double r, double g, double b) = (int)h switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return Pack(Byte(r + m), Byte(g + m), Byte(b + m));
    }
}
