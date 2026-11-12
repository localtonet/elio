using System.Reflection;
using System.Runtime.InteropServices;

namespace Elio.Terminal.Native;

/// <summary>The parts of libghostty-vt 1.0 that Elio uses. Values come from include/ghostty/vt.</summary>
internal static unsafe partial class Ghostty
{
    private const string Library = "ghostty-vt";

    public const int Success = 0;

    // GhosttySysOption
    public const int SysOptDecodePng = 2;

    // GhosttyRenderStateData and GhosttyRenderStateImageData
    public const int RenderDataImageIterator = 22;
    public const int ImageDataImageId = 2;
    public const int ImageDataGeneration = 3;
    public const int ImageDataLayer = 4;
    public const int ImageDataZ = 5;
    public const int ImageDataViewportCol = 6;
    public const int ImageDataViewportRow = 7;
    public const int ImageDataCellOffsetX = 10;
    public const int ImageDataCellOffsetY = 11;
    public const int ImageDataPixelWidth = 12;
    public const int ImageDataPixelHeight = 13;
    public const int ImageDataSourceX = 14;
    public const int ImageDataSourceY = 15;
    public const int ImageDataSourceWidth = 16;
    public const int ImageDataSourceHeight = 17;

    // GhosttyTerminalData, GhosttyKittyGraphicsImageData and GhosttyKittyImageFormat
    public const int TerminalDataKittyGraphics = 30;
    public const int KittyImageDataWidth = 3;
    public const int KittyImageDataHeight = 4;
    public const int KittyImageDataFormat = 5;
    public const int KittyImageDataPixels = 7;
    public const int KittyFormatRgb = 0;
    public const int KittyFormatRgba = 1;
    public const int KittyFormatGrayAlpha = 3;
    public const int KittyFormatGray = 4;

    /// <summary>GhosttySysImage: what a PNG decoder hands back.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SysImage
    {
        public uint Width;
        public uint Height;
        public byte* Data;
        public nuint Length;
    }

    public const int SearchOptNeedle = 1;
    public const int SearchDataTotalMatches = 3;
    public const int SearchDataSelectedIndex = 4;
    public const int SearchDataSelectedMatch = 5;

    public const int TerminalOptUserdata = 1;
    public const int TerminalOptWritePty = 2;
    public const int TerminalOptBell = 3;
    public const int TerminalOptPwdChanged = 26;
    public const int TerminalOptDesktopNotification = 30;
    public const int TerminalOptProgressReport = 31;
    public const int TerminalOptScrollbackMaxBytes = 28;
    public const int TerminalOptScrollbackMaxLines = 29;

    public const int ScrollViewportBottom = 1;
    public const int ScrollViewportDelta = 2;
    public const int ScrollViewportRow = 3;

    public const int RenderDataCols = 1;
    public const int RenderDataRows = 2;
    public const int RenderDataDirty = 3;
    public const int RenderDataRowIterator = 4;
    public const int RenderDataCursor = 18;
    public const int RenderDataColors = 19;

    public const int DirtyFalse = 0;
    public const int DirtyFull = 2;

    public const int RowDataDirty = 1;
    public const int RowDataCells = 3;

    public const int CellDataStyle = 2;
    public const int CellDataGraphemesBuf = 4;
    public const int CellDataSelected = 7;

    public const byte StyleColorPalette = 1;
    public const byte StyleColorRgb = 2;

    public const ushort StyleFlagBold = 1 << 0;
    public const ushort StyleFlagItalic = 1 << 1;
    public const ushort StyleFlagFaint = 1 << 2;
    public const ushort StyleFlagInverse = 1 << 4;
    public const ushort StyleFlagInvisible = 1 << 5;
    public const ushort StyleFlagStrikethrough = 1 << 6;
    public const ushort StyleFlagOverline = 1 << 7;
    public const int StyleUnderlineShift = 8;
    public const ushort StyleUnderlineMask = 0x7 << StyleUnderlineShift;

    public const int KeyActionPress = 1;

    public const int TerminalOptColorForeground = 12;
    public const int TerminalOptColorBackground = 13;
    public const int TerminalOptColorCursor = 14;
    public const int TerminalOptColorPalette = 15;
    public const int TerminalOptSelection = 22;

    public const int TerminalDataActiveScreen = 6;
    public const int TerminalDataScrollbar = 9;
    public const int TerminalDataPwd = 13;
    public const int TerminalDataMouseTracking = 11;
    public const int TerminalDataSelection = 31;
    public const int TerminalDataMode = 37;

    public const int ScreenAlternate = 1;
    public const ushort ModeBracketedPaste = 2004;
    public const int PointTagViewport = 1;

    public const int GestureEventPress = 0;
    public const int GestureEventRelease = 1;
    public const int GestureEventDrag = 2;
    public const int GestureOptRef = 1;
    public const int GestureOptPosition = 2;
    public const int GestureOptRepeatDistance = 3;
    public const int GestureOptTimeNs = 4;
    public const int GestureOptRepeatIntervalNs = 5;
    public const int GestureOptGeometry = 9;

    public const int FormatterOptEmit = 1;
    public const int FormatterOptUnwrap = 2;
    public const int FormatterOptTrim = 3;
    public const int FormatterOptSelection = 4;
    public const int FormatterFormatPlain = 0;

    public const int MouseEncoderOptSize = 3;
    public const int MouseEncoderOptAnyButtonPressed = 4;

    public const int NoValue = -4;

    // GhosttyTerminalBoundary, GhosttySemanticPromptKind and what goes with them
    public const uint BoundaryOscEnd = 1 << 6;
    public const int TerminalOptSemanticPrompt = 43;
    public const int SemanticPromptStart = 1;
    public const int SemanticInputStart = 2;
    public const int SemanticOutputStart = 3;
    public const int SemanticCommandEnd = 4;
    public const int SemanticPromptPrimary = 0;
    public const int ScrollViewportDeltaPrompt = 4;
    public const int TerminalDataCols = 1;
    public const int TerminalDataCursorX = 3;
    public const int TerminalDataCursorY = 4;
    public const int TerminalDataCursorAtPrompt = 39;

    // GhosttyPointTag, GhosttyRowData, GhosttyCellData, GhosttyCellContentTag and GhosttyCellWide
    public const int PointTagActive = 0;
    public const int PointTagScreen = 2;
    public const int GridRowDataWrap = 1;
    public const int GridCellDataCodepoint = 1;
    public const int GridCellDataContentTag = 2;
    public const int GridCellDataWide = 3;
    public const int CellContentGrapheme = 1;
    public const int CellWideWide = 1;
    public const int CellWideSpacerTail = 2;
    public const int CellWideSpacerHead = 3;

    /// <summary>GhosttyPointCoordinate</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointCoordinate
    {
        public ushort X;
        public uint Y;
    }

    /// <summary>GhosttyTerminalWriteStop: where a write that watches for boundaries stopped.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WriteStop
    {
        public nuint Size;
        public nuint Consumed;
        public uint Reached;
        public int Kind;
        public int Introducer;
        public int End;
        public byte Byte;
    }

    /// <summary>GhosttyTerminalSemanticPrompt: one step of a command, as a shell with integration reports it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SemanticPrompt
    {
        public int Kind;
        public int PromptKind;
        public byte HasExitCode;
        public int ExitCode;
        public NativeString Command;
        public NativeString Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GridRef
    {
        public nint Node;
        public ushort X;
        public ushort Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Selection
    {
        public GridRef Start;
        public GridRef End;
        public byte Rectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int Tag;
        public int Reserved;
        public ushort X;
        public uint Y;
        public ulong Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SurfacePosition
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GestureGeometry
    {
        public uint Columns;
        public uint CellWidth;
        public uint PaddingLeft;
        public uint ScreenHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModeConfig
    {
        public ushort Mode;
        public byte Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseEncoderSize
    {
        public nuint Size;
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint CellWidth;
        public uint CellHeight;
        public uint PaddingTop;
        public uint PaddingBottom;
        public uint PaddingRight;
        public uint PaddingLeft;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MousePosition
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rgb
    {
        public byte R, G, B;

        public readonly uint ToUInt32() => (uint)((R << 16) | (G << 8) | B);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StyleColor
    {
        public byte Tag;
        public byte V0, V1, V2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Style
    {
        public StyleColor Foreground;
        public StyleColor Background;
        public StyleColor Underline;
        public ushort Flags;
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderCursor
    {
        public nuint Size;
        public byte ViewportHasValue;
        public ushort ViewportX;
        public ushort ViewportY;
        public byte WideTail;
        public byte Visible;
        public byte Blinking;
        public byte PasswordInput;
        public int VisualStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderColors
    {
        public nuint Size;
        public Rgb Background;
        public Rgb Foreground;
        public Rgb Cursor;
        public byte CursorHasValue;
        public fixed byte Palette[256 * 3];

        public uint PaletteColor(byte index)
        {
            int offset = index * 3;
            return (uint)((Palette[offset] << 16) | (Palette[offset + 1] << 8) | Palette[offset + 2]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeString
    {
        public byte* Pointer;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CodepointBuffer
    {
        public uint* Pointer;
        public nuint Capacity;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScrollViewport
    {
        public int Tag;
        public nint Delta;
        public ulong Padding;
    }

    /// <summary>GhosttyTerminalScrollbar: every row there is, the first one on screen, and how many are on screen.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Scrollbar
    {
        public ulong Total;
        public ulong Offset;
        public ulong Length;
    }

    /// <summary>GhosttyTerminalDesktopNotification</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DesktopNotification
    {
        public NativeString Title;
        public NativeString Body;
    }

    /// <summary>GhosttyTerminalProgressReport</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProgressReport
    {
        public int State;
        public sbyte Progress;
    }

    // Said when the assembly is loaded, not when this class is first used: on a phone the runtime looks a
    // function up while it prepares the code that calls it, which is before the class has had its turn, and
    // by then it has to know already where the engine is.
#pragma warning disable CA2255 // This is the one thing that has to happen before any code here runs.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void FindEngine() => NativeLibrary.SetDllImportResolver(typeof(Ghostty).Assembly, Resolve);
#pragma warning restore CA2255

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;

        // An iPhone runs no library of its own that was not there when the app was signed, so the engine is
        // linked into the program itself and is found there.
        if (OperatingSystem.IsIOS()) return NativeLibrary.GetMainProgramHandle();

        string file = OperatingSystem.IsWindows() ? "ghostty-vt.dll"
            : OperatingSystem.IsMacOS() ? "libghostty-vt.dylib"
            : "libghostty-vt.so";

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, file),
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
        ];
        foreach (string path in candidates)
            if (NativeLibrary.TryLoad(path, out nint handle)) return handle;

        // Android keeps native libraries where only the system loader can find them.
        return NativeLibrary.TryLoad(file, assembly, searchPath, out nint fallback) ? fallback : 0;
    }

    [LibraryImport(Library, EntryPoint = "ghostty_alloc")]
    public static partial byte* Alloc(nint allocator, nuint length);

    [LibraryImport(Library, EntryPoint = "ghostty_sys_set")]
    public static partial int SysSet(int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_image_iterator_new")]
    public static partial int ImageIteratorNew(nint allocator, nint* iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_image_iterator_free")]
    public static partial void ImageIteratorFree(nint iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_image_iterator_next")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ImageIteratorNext(nint iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_image_get")]
    public static partial int ImageGet(nint iterator, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_kitty_graphics_animation_tick")]
    public static partial int KittyGraphicsAnimationTick(nint graphics, ulong now, ulong* next);

    [LibraryImport(Library, EntryPoint = "ghostty_kitty_graphics_image")]
    public static partial nint KittyGraphicsImage(nint graphics, uint imageId);

    [LibraryImport(Library, EntryPoint = "ghostty_kitty_graphics_image_get")]
    public static partial int KittyGraphicsImageGet(nint image, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_search_new")]
    public static partial int SearchNew(nint allocator, nint* search, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_search_free")]
    public static partial void SearchFree(nint search);

    [LibraryImport(Library, EntryPoint = "ghostty_search_run")]
    public static partial int SearchRun(nint search);

    [LibraryImport(Library, EntryPoint = "ghostty_search_select_next")]
    public static partial int SearchSelectNext(nint search);

    [LibraryImport(Library, EntryPoint = "ghostty_search_select_prev")]
    public static partial int SearchSelectPrevious(nint search);

    [LibraryImport(Library, EntryPoint = "ghostty_search_set")]
    public static partial int SearchSet(nint search, int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_search_get")]
    public static partial int SearchGet(nint search, int data, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_new")]
    public static partial int TerminalNew(nint allocator, nint* terminal, ushort cols, ushort rows);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_free")]
    public static partial void TerminalFree(nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_resize")]
    public static partial int TerminalResize(nint terminal, ushort cols, ushort rows, uint cellWidth, uint cellHeight);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_set")]
    public static partial int TerminalSet(nint terminal, int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_vt_write")]
    public static partial void TerminalVtWrite(nint terminal, byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_scroll_viewport")]
    public static partial int TerminalScrollViewport(nint terminal, ScrollViewport behavior);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_new")]
    public static partial int RenderStateNew(nint allocator, nint* state);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_free")]
    public static partial void RenderStateFree(nint state);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_update")]
    public static partial int RenderStateUpdate(nint state, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_clean")]
    public static partial int RenderStateClean(nint state);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_get")]
    public static partial int RenderStateGet(nint state, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_iterator_new")]
    public static partial int RowIteratorNew(nint allocator, nint* iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_iterator_free")]
    public static partial void RowIteratorFree(nint iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_iterator_next")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RowIteratorNext(nint iterator);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_get")]
    public static partial int RowGet(nint iterator, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_cells_new")]
    public static partial int RowCellsNew(nint allocator, nint* cells);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_cells_free")]
    public static partial void RowCellsFree(nint cells);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_cells_next")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RowCellsNext(nint cells);

    [LibraryImport(Library, EntryPoint = "ghostty_render_state_row_cells_get_multi")]
    public static partial int RowCellsGetMulti(nint cells, nuint count, int* keys, void** values, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_key_encoder_new")]
    public static partial int KeyEncoderNew(nint allocator, nint* encoder);

    [LibraryImport(Library, EntryPoint = "ghostty_key_encoder_free")]
    public static partial void KeyEncoderFree(nint encoder);

    [LibraryImport(Library, EntryPoint = "ghostty_key_encoder_set_from_terminal")]
    public static partial int KeyEncoderSetFromTerminal(nint encoder, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_key_encoder_encode")]
    public static partial int KeyEncoderEncode(nint encoder, nint keyEvent, byte* buffer, nuint capacity, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_new")]
    public static partial int KeyEventNew(nint allocator, nint* keyEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_free")]
    public static partial void KeyEventFree(nint keyEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_set_action")]
    public static partial int KeyEventSetAction(nint keyEvent, int action);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_set_key")]
    public static partial int KeyEventSetKey(nint keyEvent, int key);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_set_mods")]
    public static partial int KeyEventSetMods(nint keyEvent, ushort mods);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_set_unshifted_codepoint")]
    public static partial int KeyEventSetUnshiftedCodepoint(nint keyEvent, uint codepoint);

    [LibraryImport(Library, EntryPoint = "ghostty_key_event_set_utf8")]
    public static partial int KeyEventSetUtf8(nint keyEvent, NativeString text);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_get")]
    public static partial int TerminalGet(nint terminal, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_grid_ref")]
    public static partial int TerminalGridRef(nint terminal, Point point, GridRef* output);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_vt_write_until")]
    public static partial int TerminalVtWriteUntil(nint terminal, byte* data, nuint length, uint boundaries, WriteStop* stop);

    // The two below are called for every cell of the history when it is searched, and do no more than read a value.
    [LibraryImport(Library, EntryPoint = "ghostty_grid_ref_cell")]
    [SuppressGCTransition]
    public static partial int GridRefCell(GridRef* reference, ulong* cell);

    [LibraryImport(Library, EntryPoint = "ghostty_grid_ref_row")]
    public static partial int GridRefRow(GridRef* reference, ulong* row);

    [LibraryImport(Library, EntryPoint = "ghostty_grid_ref_graphemes")]
    public static partial int GridRefGraphemes(GridRef* reference, uint* buffer, nuint capacity, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_cell_get_multi")]
    [SuppressGCTransition]
    public static partial int GridCellGetMulti(ulong cell, nuint count, int* keys, void** values, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_row_get")]
    public static partial int GridRowGet(ulong row, int data, void* output);

    [LibraryImport(Library, EntryPoint = "ghostty_terminal_grid_ref_track")]
    public static partial int TerminalGridRefTrack(nint terminal, Point point, nint* tracked);

    [LibraryImport(Library, EntryPoint = "ghostty_tracked_grid_ref_free")]
    public static partial void TrackedGridRefFree(nint tracked);

    [LibraryImport(Library, EntryPoint = "ghostty_tracked_grid_ref_set")]
    public static partial int TrackedGridRefSet(nint tracked, nint terminal, Point point);

    [LibraryImport(Library, EntryPoint = "ghostty_tracked_grid_ref_point")]
    public static partial int TrackedGridRefPoint(nint tracked, int tag, PointCoordinate* point);

    [LibraryImport(Library, EntryPoint = "ghostty_color_palette_default")]
    public static partial void ColorPaletteDefault(Rgb* output);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_new")]
    public static partial int SelectionGestureNew(nint allocator, nint* gesture);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_free")]
    public static partial void SelectionGestureFree(nint gesture, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_event_new")]
    public static partial int GestureEventNew(nint allocator, nint* gestureEvent, int type);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_event_free")]
    public static partial void GestureEventFree(nint gestureEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_event_set")]
    public static partial int GestureEventSet(nint gestureEvent, int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_selection_gesture_event")]
    public static partial int SelectionGestureEvent(nint gesture, nint terminal, nint gestureEvent, Selection* selection);

    [LibraryImport(Library, EntryPoint = "ghostty_formatter_terminal_new")]
    public static partial int FormatterTerminalNew(nint allocator, nint* formatter, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_formatter_set")]
    public static partial int FormatterSet(nint formatter, int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_formatter_format_alloc")]
    public static partial int FormatterFormatAlloc(nint formatter, nint allocator, byte** output, nuint* length);

    [LibraryImport(Library, EntryPoint = "ghostty_formatter_free")]
    public static partial void FormatterFree(nint formatter);

    [LibraryImport(Library, EntryPoint = "ghostty_free")]
    public static partial void Free(nint allocator, byte* pointer, nuint length);

    [LibraryImport(Library, EntryPoint = "ghostty_paste_is_safe")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PasteIsSafe(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "ghostty_paste_encode")]
    public static partial int PasteEncode(byte* data, nuint length, [MarshalAs(UnmanagedType.U1)] bool bracketed, byte* buffer, nuint capacity, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_encoder_new")]
    public static partial int MouseEncoderNew(nint allocator, nint* encoder);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_encoder_free")]
    public static partial void MouseEncoderFree(nint encoder);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_encoder_set")]
    public static partial int MouseEncoderSet(nint encoder, int option, void* value);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_encoder_set_from_terminal")]
    public static partial int MouseEncoderSetFromTerminal(nint encoder, nint terminal);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_encoder_encode")]
    public static partial int MouseEncoderEncode(nint encoder, nint mouseEvent, byte* buffer, nuint capacity, nuint* written);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_new")]
    public static partial int MouseEventNew(nint allocator, nint* mouseEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_free")]
    public static partial void MouseEventFree(nint mouseEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_set_action")]
    public static partial int MouseEventSetAction(nint mouseEvent, int action);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_set_button")]
    public static partial int MouseEventSetButton(nint mouseEvent, int button);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_clear_button")]
    public static partial int MouseEventClearButton(nint mouseEvent);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_set_mods")]
    public static partial int MouseEventSetMods(nint mouseEvent, ushort mods);

    [LibraryImport(Library, EntryPoint = "ghostty_mouse_event_set_position")]
    public static partial int MouseEventSetPosition(nint mouseEvent, MousePosition position);
}
