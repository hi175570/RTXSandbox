using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;
using RTXSandbox.Rendering;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RTXSandbox.UI;

internal sealed unsafe class ImGuiController : IDisposable
{
    private const int ImDrawVertexSize = 20;

    private readonly ShaderProgram _shader;
    private readonly int _vertexArray;
    private readonly int _vertexBuffer;
    private readonly int _indexBuffer;
    private readonly int _fontTexture;
    private bool _disposed;
    private static IntPtr s_imeWindowHandle;
    private static int s_imePositionUpdateCount;

    internal bool ImePositionUpdated =>
        !OperatingSystem.IsWindows() || Volatile.Read(ref s_imePositionUpdateCount) > 0;

    internal bool IsImeCompositionActive => IsImeComposing();

    internal bool WantsTextEntry
    {
        get
        {
            if (ImGui.GetCurrentContext() == IntPtr.Zero) return IsImeComposing();
            return ImGui.GetIO().WantTextInput || IsImeComposing();
        }
    }

    internal bool WantsKeyboardInput
    {
        get
        {
            if (ImGui.GetCurrentContext() == IntPtr.Zero) return IsImeComposing();
            ImGuiIOPtr io = ImGui.GetIO();
            return io.WantCaptureKeyboard || io.WantTextInput || IsImeComposing();
        }
    }

    public ImGuiController(IntPtr nativeWindowHandle)
    {
        ImGui.CreateContext();
        ImGui.StyleColorsDark();
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        LoadInternationalFonts(io.Fonts);
        ConfigureIme(nativeWindowHandle);

        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 14f;
        style.ChildRounding = 10f;
        style.FrameRounding = 7f;
        style.PopupRounding = 10f;
        style.GrabRounding = 7f;
        style.TabRounding = 7f;
        style.WindowBorderSize = 1.2f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new System.Numerics.Vector2(16f, 14f);
        style.FramePadding = new System.Numerics.Vector2(10f, 7f);
        style.ItemSpacing = new System.Numerics.Vector2(9f, 8f);
        style.Colors[(int)ImGuiCol.WindowBg] = new System.Numerics.Vector4(0.035f, 0.052f, 0.078f, 0.97f);
        style.Colors[(int)ImGuiCol.ChildBg] = new System.Numerics.Vector4(0.045f, 0.065f, 0.092f, 0.92f);
        style.Colors[(int)ImGuiCol.PopupBg] = new System.Numerics.Vector4(0.04f, 0.06f, 0.09f, 0.98f);
        style.Colors[(int)ImGuiCol.Border] = new System.Numerics.Vector4(0.18f, 0.48f, 0.70f, 0.65f);
        style.Colors[(int)ImGuiCol.FrameBg] = new System.Numerics.Vector4(0.075f, 0.11f, 0.16f, 0.96f);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = new System.Numerics.Vector4(0.10f, 0.25f, 0.36f, 0.96f);
        style.Colors[(int)ImGuiCol.FrameBgActive] = new System.Numerics.Vector4(0.12f, 0.36f, 0.52f, 1f);
        style.Colors[(int)ImGuiCol.Button] = new System.Numerics.Vector4(0.08f, 0.24f, 0.36f, 0.95f);
        style.Colors[(int)ImGuiCol.ButtonHovered] = new System.Numerics.Vector4(0.10f, 0.48f, 0.68f, 1f);
        style.Colors[(int)ImGuiCol.ButtonActive] = new System.Numerics.Vector4(0.08f, 0.62f, 0.82f, 1f);
        style.Colors[(int)ImGuiCol.Header] = new System.Numerics.Vector4(0.08f, 0.34f, 0.50f, 0.86f);
        style.Colors[(int)ImGuiCol.HeaderHovered] = new System.Numerics.Vector4(0.10f, 0.51f, 0.70f, 0.92f);
        style.Colors[(int)ImGuiCol.HeaderActive] = new System.Numerics.Vector4(0.12f, 0.64f, 0.84f, 1f);
        style.Colors[(int)ImGuiCol.CheckMark] = new System.Numerics.Vector4(0.25f, 0.86f, 1f, 1f);
        style.Colors[(int)ImGuiCol.SliderGrab] = new System.Numerics.Vector4(0.20f, 0.72f, 0.94f, 1f);
        style.Colors[(int)ImGuiCol.Tab] = new System.Numerics.Vector4(0.06f, 0.16f, 0.24f, 0.98f);
        style.Colors[(int)ImGuiCol.TabHovered] = new System.Numerics.Vector4(0.10f, 0.46f, 0.66f, 1f);
        style.Colors[(int)ImGuiCol.TabSelected] = new System.Numerics.Vector4(0.08f, 0.34f, 0.52f, 1f);

        _shader = new ShaderProgram(
            (ShaderType.VertexShader, VertexShaderSource),
            (ShaderType.FragmentShader, FragmentShaderSource));

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, ImDrawVertexSize, IntPtr.Zero);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, ImDrawVertexSize, (IntPtr)8);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, ImDrawVertexSize, (IntPtr)16);

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
    }

    public void BeginFrame(int width, int height, float deltaSeconds, MouseState mouse, KeyboardState keyboard)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(Math.Max(width, 1), Math.Max(height, 1));
        io.DisplayFramebufferScale = System.Numerics.Vector2.One;
        io.DeltaTime = Math.Max(deltaSeconds, 1f / 1000f);
        io.AddMousePosEvent(mouse.Position.X, mouse.Position.Y);
        io.AddMouseButtonEvent(0, mouse.IsButtonDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, mouse.IsButtonDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, mouse.IsButtonDown(MouseButton.Middle));
        io.AddMouseWheelEvent(mouse.ScrollDelta.X, mouse.ScrollDelta.Y);
        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.Backspace, keyboard.IsKeyDown(Keys.Backspace));
        io.AddKeyEvent(ImGuiKey.Delete, keyboard.IsKeyDown(Keys.Delete));
        io.AddKeyEvent(ImGuiKey.Enter, keyboard.IsKeyDown(Keys.Enter));
        io.AddKeyEvent(ImGuiKey.Space, keyboard.IsKeyDown(Keys.Space));
        io.AddKeyEvent(ImGuiKey.Escape, keyboard.IsKeyDown(Keys.Escape));
        io.AddKeyEvent(ImGuiKey.Tab, keyboard.IsKeyDown(Keys.Tab));
        io.AddKeyEvent(ImGuiKey.LeftArrow, keyboard.IsKeyDown(Keys.Left));
        io.AddKeyEvent(ImGuiKey.RightArrow, keyboard.IsKeyDown(Keys.Right));
        io.AddKeyEvent(ImGuiKey.UpArrow, keyboard.IsKeyDown(Keys.Up));
        io.AddKeyEvent(ImGuiKey.DownArrow, keyboard.IsKeyDown(Keys.Down));
        io.AddKeyEvent(ImGuiKey.PageUp, keyboard.IsKeyDown(Keys.PageUp));
        io.AddKeyEvent(ImGuiKey.PageDown, keyboard.IsKeyDown(Keys.PageDown));
        io.AddKeyEvent(ImGuiKey.Home, keyboard.IsKeyDown(Keys.Home));
        io.AddKeyEvent(ImGuiKey.End, keyboard.IsKeyDown(Keys.End));
        io.AddKeyEvent(ImGuiKey.A, keyboard.IsKeyDown(Keys.A));
        io.AddKeyEvent(ImGuiKey.C, keyboard.IsKeyDown(Keys.C));
        io.AddKeyEvent(ImGuiKey.V, keyboard.IsKeyDown(Keys.V));
        io.AddKeyEvent(ImGuiKey.X, keyboard.IsKeyDown(Keys.X));
        ImGui.NewFrame();
    }

    public void AddInputCharacter(uint codepoint)
    {
        if (IsUnicodeScalar(codepoint)) ImGui.GetIO().AddInputCharacter(codepoint);
    }

    internal static bool IsUnicodeScalar(uint codepoint) =>
        codepoint is > 0 and <= 0x10FFFF && codepoint is not (>= 0xD800 and <= 0xDFFF);

    public void EndFrame(int framebufferWidth, int framebufferHeight)
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData(), framebufferWidth, framebufferHeight);
    }

    private void RenderDrawData(ImDrawDataPtr drawData, int framebufferWidth, int framebufferHeight)
    {
        if (drawData.CmdListsCount == 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        GL.Viewport(0, 0, framebufferWidth, framebufferHeight);
        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);

        _shader.Use();
        GL.Uniform2(_shader.Uniform("uScreenSize"), (float)framebufferWidth, (float)framebufferHeight);
        GL.Uniform1(_shader.Uniform("uTexture"), 0);
        GL.BindVertexArray(_vertexArray);

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr commandList = drawData.CmdLists[listIndex];
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                commandList.VtxBuffer.Size * ImDrawVertexSize,
                commandList.VtxBuffer.Data,
                BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                commandList.IdxBuffer.Size * sizeof(ushort),
                commandList.IdxBuffer.Data,
                BufferUsageHint.StreamDraw);

            for (int commandIndex = 0; commandIndex < commandList.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr command = commandList.CmdBuffer[commandIndex];
                if (command.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                System.Numerics.Vector4 clip = command.ClipRect;
                int clipX = Math.Max(0, (int)clip.X);
                int clipY = Math.Max(0, (int)clip.Y);
                int clipWidth = Math.Min(framebufferWidth, (int)clip.Z) - clipX;
                int clipHeight = Math.Min(framebufferHeight, (int)clip.W) - clipY;
                if (clipWidth <= 0 || clipHeight <= 0)
                {
                    continue;
                }

                GL.Scissor(clipX, framebufferHeight - (clipY + clipHeight), clipWidth, clipHeight);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)command.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(command.IdxOffset * sizeof(ushort)),
                    (int)command.VtxOffset);
            }
        }

        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        platformIO.Platform_SetImeDataFn = IntPtr.Zero;
        platformIO.Platform_ImeUserData = IntPtr.Zero;
        s_imeWindowHandle = IntPtr.Zero;
        GL.DeleteTexture(_fontTexture);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        _shader.Dispose();
        ImGui.DestroyContext();
    }

    private static void LoadInternationalFonts(ImFontAtlasPtr fonts)
    {
        string fontDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string? baseFont = FirstExisting(fontDirectory, "segoeui.ttf", "arial.ttf");
        if (baseFont is null)
        {
            fonts.AddFontDefault();
            return;
        }

        const float size = 18f;
        fonts.AddFontFromFileTTF(baseFont, size, null, fonts.GetGlyphRangesDefault());
        ImFontConfigPtr merge = ImGuiNative.ImFontConfig_ImFontConfig();
        merge.MergeMode = true;
        merge.PixelSnapH = true;
        try
        {
            MergeFont(fonts, baseFont, size, merge, fonts.GetGlyphRangesGreek());
            MergeFont(fonts, baseFont, size, merge, fonts.GetGlyphRangesCyrillic());
            MergeFont(fonts, baseFont, size, merge, fonts.GetGlyphRangesThai());
            MergeFont(fonts, baseFont, size, merge, fonts.GetGlyphRangesVietnamese());
            string? chinese = FirstExisting(fontDirectory,
                "NotoSansTC-VF.ttf", "msjh.ttc", "msyh.ttc");
            string? japanese = FirstExisting(fontDirectory, "msgothic.ttc", "meiryo.ttc");
            string? korean = FirstExisting(fontDirectory, "malgun.ttf");
            if (chinese is not null)
                MergeFont(fonts, chinese, size, merge, fonts.GetGlyphRangesChineseFull());
            if (japanese is not null)
                MergeFont(fonts, japanese, size, merge, fonts.GetGlyphRangesJapanese());
            if (korean is not null)
                MergeFont(fonts, korean, size, merge, fonts.GetGlyphRangesKorean());
        }
        finally
        {
            merge.Destroy();
        }
    }

    private static void MergeFont(
        ImFontAtlasPtr fonts,
        string path,
        float size,
        ImFontConfigPtr config,
        IntPtr ranges) => fonts.AddFontFromFileTTF(path, size, config, ranges);

    private static string? FirstExisting(string directory, params string[] names) => names
        .Select(name => Path.Combine(directory, name))
        .FirstOrDefault(File.Exists);

    private static void ConfigureIme(IntPtr nativeWindowHandle)
    {
        if (!OperatingSystem.IsWindows() || nativeWindowHandle == IntPtr.Zero) return;
        s_imeWindowHandle = nativeWindowHandle;
        Volatile.Write(ref s_imePositionUpdateCount, 0);
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        viewport.PlatformHandle = nativeWindowHandle;
        viewport.PlatformHandleRaw = nativeWindowHandle;
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
        platformIO.Platform_ImeUserData = nativeWindowHandle;
        platformIO.Platform_SetImeDataFn = (IntPtr)(delegate* unmanaged[Cdecl]<
            void*, ImGuiViewport*, ImGuiPlatformImeData*, void>)&SetImeData;
    }

    public void HandleWindowFocusChanged(bool focused, IntPtr nativeWindowHandle)
    {
        if (!focused || !OperatingSystem.IsWindows() || nativeWindowHandle == IntPtr.Zero) return;
        ImmAssociateContextEx(nativeWindowHandle, IntPtr.Zero, IaceDefault);
        ConfigureIme(nativeWindowHandle);
    }

    private static bool IsImeComposing()
    {
        if (!OperatingSystem.IsWindows() || s_imeWindowHandle == IntPtr.Zero) return false;
        IntPtr inputContext = ImmGetContext(s_imeWindowHandle);
        if (inputContext == IntPtr.Zero) return false;
        try
        {
            return ImmGetCompositionStringW(inputContext, CompositionString, IntPtr.Zero, 0) > 0;
        }
        finally
        {
            ImmReleaseContext(s_imeWindowHandle, inputContext);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetImeData(
        void* context,
        ImGuiViewport* viewport,
        ImGuiPlatformImeData* data)
    {
        if (s_imeWindowHandle == IntPtr.Zero || data is null || data->WantVisible == 0) return;
        Interlocked.Increment(ref s_imePositionUpdateCount);
        IntPtr inputContext = ImmGetContext(s_imeWindowHandle);
        if (inputContext == IntPtr.Zero) return;
        try
        {
            float viewportX = viewport is null ? 0f : viewport->Pos.X;
            float viewportY = viewport is null ? 0f : viewport->Pos.Y;
            int x = (int)MathF.Round(data->InputPos.X - viewportX);
            int inputTop = (int)MathF.Round(data->InputPos.Y - viewportY);
            int y = (int)MathF.Round(data->InputPos.Y - viewportY + data->InputLineHeight);
            var composition = new CompositionForm
            {
                Style = CompositionFormPoint,
                CurrentPosition = new NativePoint(x, y),
            };
            ImmSetCompositionWindow(inputContext, ref composition);
            var candidate = new CandidateForm
            {
                Index = 0,
                Style = CandidateFormExclude,
                CurrentPosition = new NativePoint(x, y),
                ExcludedArea = new NativeRectangle(x, inputTop, x + 1, y),
            };
            ImmSetCandidateWindow(inputContext, ref candidate);
        }
        finally
        {
            ImmReleaseContext(s_imeWindowHandle, inputContext);
        }
    }

    private const int CompositionFormPoint = 0x0002;
    private const int CandidateFormExclude = 0x0080;
    private const uint CompositionString = 0x0008;
    private const uint IaceDefault = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionForm
    {
        public int Style;
        public NativePoint CurrentPosition;
        public NativeRectangle Area;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateForm
    {
        public int Index;
        public int Style;
        public NativePoint CurrentPosition;
        public NativeRectangle ExcludedArea;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr windowHandle);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr windowHandle, IntPtr inputContext);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr inputContext, ref CompositionForm form);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCandidateWindow(IntPtr inputContext, ref CandidateForm form);

    [DllImport("imm32.dll")]
    private static extern int ImmGetCompositionStringW(
        IntPtr inputContext,
        uint index,
        IntPtr buffer,
        int bufferLength);

    [DllImport("imm32.dll")]
    private static extern bool ImmAssociateContextEx(IntPtr windowHandle, IntPtr inputContext, uint flags);

    private const string VertexShaderSource = """
        #version 430 core
        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec2 inUv;
        layout(location = 2) in vec4 inColor;
        uniform vec2 uScreenSize;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vec2 clip = inPosition / uScreenSize * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            vUv = inUv;
            vColor = inColor;
        }
        """;

    private const string FragmentShaderSource = """
        #version 430 core
        in vec2 vUv;
        in vec4 vColor;
        layout(location = 0) out vec4 outColor;
        uniform sampler2D uTexture;
        void main()
        {
            outColor = vColor * texture(uTexture, vUv);
        }
        """;
}
