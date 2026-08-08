using System.Numerics;
using System.Runtime.InteropServices;
using Android.Opengl;
using ImGuiNET;
using Java.Nio;

namespace RTXSandbox.Android.Game;

/// <summary>
/// Dear ImGui renderer for the Android GLES2 framebuffer. Android owns the
/// surface and input bridge; the game continues to own every menu and widget.
/// </summary>
internal sealed unsafe class AndroidImGuiController : IDisposable
{
    private const int ImDrawVertexSize = 20;

    private readonly int _program;
    private readonly int _vertexBuffer;
    private readonly int _indexBuffer;
    private readonly int _fontTexture;
    private readonly int _positionLocation;
    private readonly int _uvLocation;
    private readonly int _colorLocation;
    private readonly int _screenLocation;
    private readonly int _textureLocation;
    private byte[] _vertexStagingBytes = Array.Empty<byte>();
    private byte[] _indexStagingBytes = Array.Empty<byte>();
    private ByteBuffer? _vertexStagingBuffer;
    private ByteBuffer? _indexStagingBuffer;
    private bool _disposed;

    public AndroidImGuiController(float density, IEnumerable<string> glyphTexts)
    {
        ImGui.CreateContext();
        ImGui.StyleColorsDark();
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        float scale = Math.Clamp(density, 1f, 1.8f);
        ConfigureStyle(scale);
        IntPtr ranges = BuildGlyphRanges(glyphTexts);
        try
        {
            string? font = FirstExisting(
                "/system/fonts/NotoSansCJK-Regular.ttc",
                "/system/fonts/NotoSansCJK-VF.ttf",
                "/system/fonts/NotoSans-Regular.ttf",
                "/system/fonts/Roboto-Regular.ttf");
            if (font is null) io.Fonts.AddFontDefault();
            else io.Fonts.AddFontFromFileTTF(font, 18f * scale, null, ranges);

            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
            byte[] fontPixels = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, fontPixels, 0, fontPixels.Length);
            ByteBuffer fontBuffer = DirectBuffer(fontPixels);

            int[] texture = new int[1];
            GLES20.GlGenTextures(1, texture, 0);
            _fontTexture = texture[0];
            GLES20.GlBindTexture(GLES20.GlTexture2d, _fontTexture);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0,
                GLES20.GlRgba, GLES20.GlUnsignedByte, fontBuffer);
            io.Fonts.SetTexID((IntPtr)_fontTexture);
            io.Fonts.ClearTexData();
        }
        finally
        {
            Marshal.FreeHGlobal(ranges);
        }

        _program = CreateProgram(VertexShader, FragmentShader);
        _positionLocation = GLES20.GlGetAttribLocation(_program, "aPosition");
        _uvLocation = GLES20.GlGetAttribLocation(_program, "aUv");
        _colorLocation = GLES20.GlGetAttribLocation(_program, "aColor");
        _screenLocation = GLES20.GlGetUniformLocation(_program, "uScreenSize");
        _textureLocation = GLES20.GlGetUniformLocation(_program, "uTexture");
        int[] buffers = new int[2];
        GLES20.GlGenBuffers(buffers.Length, buffers, 0);
        _vertexBuffer = buffers[0];
        _indexBuffer = buffers[1];
    }

    public void BeginFrame(int width, int height, float deltaTime, float pointerX, float pointerY, bool pointerDown)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(Math.Max(1, width), Math.Max(1, height));
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = Math.Max(deltaTime, 1f / 1000f);
        io.AddMousePosEvent(pointerX, pointerY);
        io.AddMouseButtonEvent(0, pointerDown);
        ImGui.NewFrame();
    }

    public void EndFrame(int width, int height)
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData(), width, height);
    }

    private void RenderDrawData(ImDrawDataPtr drawData, int width, int height)
    {
        if (drawData.CmdListsCount <= 0 || width <= 0 || height <= 0) return;

        GLES20.GlViewport(0, 0, width, height);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendEquation(GLES20.GlFuncAdd);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlDisable(0x0B44); // GL_CULL_FACE; binding name collides with GlCullFace().
        GLES20.GlDisable(GLES20.GlDepthTest);
        GLES20.GlEnable(GLES20.GlScissorTest);
        GLES20.GlUseProgram(_program);
        GLES20.GlUniform2f(_screenLocation, width, height);
        GLES20.GlUniform1i(_textureLocation, 0);

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = drawData.CmdLists[listIndex];
            int vertexBytes = checked(list.VtxBuffer.Size * ImDrawVertexSize);
            int indexBytes = checked(list.IdxBuffer.Size * sizeof(ushort));
            GLES20.GlBindBuffer(GLES20.GlArrayBuffer, _vertexBuffer);
            GLES20.GlBufferData(GLES20.GlArrayBuffer, vertexBytes,
                CopyToStaging(list.VtxBuffer.Data, vertexBytes, vertex: true), GLES20.GlStreamDraw);
            GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, _indexBuffer);
            GLES20.GlBufferData(GLES20.GlElementArrayBuffer, indexBytes,
                CopyToStaging(list.IdxBuffer.Data, indexBytes, vertex: false), GLES20.GlStreamDraw);

            GLES20.GlEnableVertexAttribArray(_positionLocation);
            GLES20.GlEnableVertexAttribArray(_uvLocation);
            GLES20.GlEnableVertexAttribArray(_colorLocation);

            for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr command = list.CmdBuffer[commandIndex];
                if (command.UserCallback != IntPtr.Zero) continue;
                Vector4 clip = command.ClipRect;
                int clipX = Math.Max(0, (int)clip.X);
                int clipY = Math.Max(0, (int)clip.Y);
                int clipWidth = Math.Min(width, (int)clip.Z) - clipX;
                int clipHeight = Math.Min(height, (int)clip.W) - clipY;
                if (clipWidth <= 0 || clipHeight <= 0) continue;

                int vertexOffset = checked((int)command.VtxOffset * ImDrawVertexSize);
                GLES20.GlVertexAttribPointer(_positionLocation, 2, GLES20.GlFloat, false,
                    ImDrawVertexSize, vertexOffset);
                GLES20.GlVertexAttribPointer(_uvLocation, 2, GLES20.GlFloat, false,
                    ImDrawVertexSize, vertexOffset + 8);
                GLES20.GlVertexAttribPointer(_colorLocation, 4, GLES20.GlUnsignedByte, true,
                    ImDrawVertexSize, vertexOffset + 16);
                GLES20.GlScissor(clipX, height - clipY - clipHeight, clipWidth, clipHeight);
                GLES20.GlActiveTexture(GLES20.GlTexture0);
                GLES20.GlBindTexture(GLES20.GlTexture2d, (int)command.TextureId);
                GLES20.GlDrawElements(GLES20.GlTriangles, (int)command.ElemCount,
                    GLES20.GlUnsignedShort, checked((int)command.IdxOffset * sizeof(ushort)));
            }

            GLES20.GlDisableVertexAttribArray(_positionLocation);
            GLES20.GlDisableVertexAttribArray(_uvLocation);
            GLES20.GlDisableVertexAttribArray(_colorLocation);
        }

        GLES20.GlDisable(GLES20.GlScissorTest);
        GLES20.GlDisable(GLES20.GlBlend);
        GLES20.GlEnable(GLES20.GlDepthTest);
        GLES20.GlEnable(0x0B44);
        GLES20.GlUseProgram(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GLES20.GlDeleteProgram(_program);
        GLES20.GlDeleteTextures(1, new[] { _fontTexture }, 0);
        GLES20.GlDeleteBuffers(2, new[] { _vertexBuffer, _indexBuffer }, 0);
        _vertexStagingBuffer?.Dispose();
        _indexStagingBuffer?.Dispose();
        ImGui.DestroyContext();
    }

    private ByteBuffer CopyToStaging(IntPtr source, int length, bool vertex)
    {
        ref byte[] bytes = ref (vertex ? ref _vertexStagingBytes : ref _indexStagingBytes);
        ref ByteBuffer? buffer = ref (vertex ? ref _vertexStagingBuffer : ref _indexStagingBuffer);
        if (bytes.Length < length || buffer is null)
        {
            int capacity = 4096;
            while (capacity < length) capacity = checked(capacity * 2);
            bytes = new byte[capacity];
            buffer?.Dispose();
            buffer = ByteBuffer.AllocateDirect(capacity)!;
            buffer.Order(ByteOrder.NativeOrder()!);
        }
        if (length > 0) Marshal.Copy(source, bytes, 0, length);
        buffer.Clear();
        buffer.Put(bytes, 0, length);
        buffer.Position(0);
        return buffer;
    }

    private static ByteBuffer DirectBuffer(byte[] bytes)
    {
        ByteBuffer buffer = ByteBuffer.AllocateDirect(bytes.Length)!;
        buffer.Order(ByteOrder.NativeOrder()!);
        buffer.Put(bytes);
        buffer.Position(0);
        return buffer;
    }

    private static IntPtr BuildGlyphRanges(IEnumerable<string> glyphTexts)
    {
        var glyphs = new SortedSet<ushort>();
        for (int value = 0x20; value <= 0xFF; value++) glyphs.Add((ushort)value);
        foreach (string text in glyphTexts)
        foreach (char character in text)
        {
            if (!char.IsSurrogate(character)) glyphs.Add(character);
        }

        var ranges = new List<ushort>();
        ushort start = 0;
        ushort previous = 0;
        bool active = false;
        foreach (ushort glyph in glyphs)
        {
            if (!active)
            {
                start = previous = glyph;
                active = true;
                continue;
            }
            if (glyph == previous + 1)
            {
                previous = glyph;
                continue;
            }
            ranges.Add(start);
            ranges.Add(previous);
            start = previous = glyph;
        }
        if (active)
        {
            ranges.Add(start);
            ranges.Add(previous);
        }
        ranges.Add(0);
        IntPtr memory = Marshal.AllocHGlobal(ranges.Count * sizeof(ushort));
        ushort* destination = (ushort*)memory;
        for (int index = 0; index < ranges.Count; index++) destination[index] = ranges[index];
        return memory;
    }

    private static void ConfigureStyle(float scale)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 2f * scale;
        style.ChildRounding = 2f * scale;
        style.FrameRounding = 1f * scale;
        style.WindowPadding = new Vector2(16f, 14f) * scale;
        style.FramePadding = new Vector2(10f, 8f) * scale;
        style.ItemSpacing = new Vector2(10f, 10f) * scale;
        style.ScrollbarSize = 20f * scale;
        style.GrabMinSize = 20f * scale;
        style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(0.035f, 0.052f, 0.078f, 0.97f);
        style.Colors[(int)ImGuiCol.ChildBg] = new Vector4(0.045f, 0.065f, 0.092f, 0.92f);
        style.Colors[(int)ImGuiCol.PopupBg] = new Vector4(0.04f, 0.06f, 0.09f, 0.98f);
        style.Colors[(int)ImGuiCol.Border] = new Vector4(0.18f, 0.48f, 0.70f, 0.65f);
        style.Colors[(int)ImGuiCol.FrameBg] = new Vector4(0.075f, 0.11f, 0.16f, 0.96f);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.10f, 0.25f, 0.36f, 0.96f);
        style.Colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.12f, 0.36f, 0.52f, 1f);
        style.Colors[(int)ImGuiCol.Button] = new Vector4(0.08f, 0.24f, 0.36f, 0.95f);
        style.Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.10f, 0.48f, 0.68f, 1f);
        style.Colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.08f, 0.62f, 0.82f, 1f);
        style.Colors[(int)ImGuiCol.Header] = new Vector4(0.08f, 0.34f, 0.50f, 0.86f);
        style.Colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.10f, 0.51f, 0.70f, 0.92f);
        style.Colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.12f, 0.64f, 0.84f, 1f);
        style.Colors[(int)ImGuiCol.CheckMark] = new Vector4(0.25f, 0.86f, 1f, 1f);
        style.Colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.20f, 0.72f, 0.94f, 1f);
        style.Colors[(int)ImGuiCol.Tab] = new Vector4(0.06f, 0.16f, 0.24f, 0.98f);
        style.Colors[(int)ImGuiCol.TabHovered] = new Vector4(0.10f, 0.46f, 0.66f, 1f);
        style.Colors[(int)ImGuiCol.TabSelected] = new Vector4(0.08f, 0.34f, 0.52f, 1f);
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static int CreateProgram(string vertex, string fragment)
    {
        int vertexShader = CompileShader(GLES20.GlVertexShader, vertex);
        int fragmentShader = CompileShader(GLES20.GlFragmentShader, fragment);
        int program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, vertexShader);
        GLES20.GlAttachShader(program, fragmentShader);
        GLES20.GlLinkProgram(program);
        int[] linked = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linked, 0);
        GLES20.GlDeleteShader(vertexShader);
        GLES20.GlDeleteShader(fragmentShader);
        if (linked[0] == 0)
            throw new InvalidOperationException($"Android ImGui program link failed: {GLES20.GlGetProgramInfoLog(program)}");
        return program;
    }

    private static int CompileShader(int type, string source)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        int[] compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] == 0)
            throw new InvalidOperationException($"Android ImGui shader compile failed: {GLES20.GlGetShaderInfoLog(shader)}");
        return shader;
    }

    private const string VertexShader = """
        precision highp float;
        attribute vec2 aPosition;
        attribute vec2 aUv;
        attribute vec4 aColor;
        uniform vec2 uScreenSize;
        varying vec2 vUv;
        varying vec4 vColor;
        void main() {
            vec2 clip = vec2(aPosition.x * 2.0 / uScreenSize.x - 1.0,
                             1.0 - aPosition.y * 2.0 / uScreenSize.y);
            gl_Position = vec4(clip, 0.0, 1.0);
            vUv = aUv;
            vColor = aColor;
        }
        """;

    private const string FragmentShader = """
        precision mediump float;
        varying vec2 vUv;
        varying vec4 vColor;
        uniform sampler2D uTexture;
        void main() { gl_FragColor = vColor * texture2D(uTexture, vUv); }
        """;
}
