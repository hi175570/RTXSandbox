using OpenTK.Graphics.OpenGL4;
using RTXSandbox.ManifoldGardenBridge;

namespace RTXSandbox.Infrastructure;

/// <summary>
/// Publishes the exact composited RTX framebuffer (world plus the real ImGui HUD)
/// to Manifold Garden. The mapping is memory-only; no screenshots are written to disk.
/// </summary>
public sealed class ManifoldGardenFramePublisher : IDisposable
{
    // Publish every completed RTX frame. A 30 Hz cap left the transparent block layer one
    // or two Manifold frames behind during camera motion, which looked like the blocks were
    // attached to the camera instead of the floor.
    private static readonly TimeSpan FrameInterval = TimeSpan.Zero;
    private readonly BridgeFrameWriter _writer = new();
    private byte[] _pixels = Array.Empty<byte>();
    private DateTime _nextFrameUtc;
    private int _framebuffer;
    private int _colorTexture;
    private int _depthRenderbuffer;
    private int _width;
    private int _height;

    /// <summary>The renderer's logical default framebuffer for the current bridge frame.</summary>
    public static int CurrentFramebuffer { get; private set; }

    public void BeginFrame(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        EnsureFramebuffer(width, height);
        CurrentFramebuffer = _framebuffer;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.Viewport(0, 0, width, height);
    }

    public void Publish(int width, int height, int windowWidth, int windowHeight)
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextFrameUtc || width <= 0 || height <= 0 ||
            width > BridgeFrameProtocol.MaximumWidth || height > BridgeFrameProtocol.MaximumHeight)
        {
            PresentToWindow(width, height, windowWidth, windowHeight);
            return;
        }

        _nextFrameUtc = now + FrameInterval;
        int length = checked(width * height * 4);
        if (_pixels.Length != length) _pixels = new byte[length];
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, _pixels);
        _writer.Write(width, height, _pixels);
        PresentToWindow(width, height, windowWidth, windowHeight);
    }

    private void EnsureFramebuffer(int width, int height)
    {
        if (_framebuffer != 0 && _width == width && _height == height) return;
        DeleteFramebuffer();
        _width = width;
        _height = height;
        _framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);

        _colorTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _colorTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);

        _depthRenderbuffer = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
            width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Manifold bridge presentation framebuffer is incomplete.");
    }

    private void PresentToWindow(int width, int height, int windowWidth, int windowHeight)
    {
        windowWidth = Math.Max(1, windowWidth);
        windowHeight = Math.Max(1, windowHeight);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.BlitFramebuffer(0, 0, width, height, 0, 0, windowWidth, windowHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        CurrentFramebuffer = 0;
    }

    private void DeleteFramebuffer()
    {
        if (_depthRenderbuffer != 0) GL.DeleteRenderbuffer(_depthRenderbuffer);
        if (_colorTexture != 0) GL.DeleteTexture(_colorTexture);
        if (_framebuffer != 0) GL.DeleteFramebuffer(_framebuffer);
        _depthRenderbuffer = 0;
        _colorTexture = 0;
        _framebuffer = 0;
    }

    public void Dispose()
    {
        CurrentFramebuffer = 0;
        DeleteFramebuffer();
        _writer.Dispose();
    }
}
