using OpenTK.Graphics.OpenGL4;

namespace RTXSandbox.Rendering;

internal sealed class ShaderProgram : IDisposable
{
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);

    public int Handle { get; }

    public ShaderProgram(params (ShaderType Type, string Source)[] stages)
    {
        Handle = GL.CreateProgram();
        var shaderHandles = new List<int>();

        try
        {
            foreach ((ShaderType type, string source) in stages)
            {
                int shader = GL.CreateShader(type);
                shaderHandles.Add(shader);
                GL.ShaderSource(shader, PrepareSource(type, source));
                GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0)
                {
                    throw new InvalidOperationException($"{type} compilation failed:\n{GL.GetShaderInfoLog(shader)}");
                }
                GL.AttachShader(Handle, shader);
            }

            GL.LinkProgram(Handle);
            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException($"Shader link failed:\n{GL.GetProgramInfoLog(Handle)}");
            }
        }
        finally
        {
            foreach (int shader in shaderHandles)
            {
                GL.DetachShader(Handle, shader);
                GL.DeleteShader(shader);
            }
        }
    }

    public void Use() => GL.UseProgram(Handle);

    public int Uniform(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int cached)) return cached;
        int location = GL.GetUniformLocation(Handle, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Required shader uniform '{name}' was optimized out or is missing.");
        }
        _uniformLocations.Add(name, location);
        return location;
    }

    public void Dispose() => GL.DeleteProgram(Handle);

    private static string PrepareSource(ShaderType type, string source)
    {
        // Apple exposes desktop OpenGL 4.1 as its final OpenGL version. Raster
        // shaders only use features available there; compute shaders remain
        // unavailable and are filtered by the renderer capability catalog.
        bool gl41Compatibility = OperatingSystem.IsMacOS() ||
                                 Environment.GetEnvironmentVariable("RTX_SANDBOX_FORCE_GL41") == "1";
        if (gl41Compatibility && type != ShaderType.ComputeShader)
            return source.Replace("#version 430 core", "#version 410 core", StringComparison.Ordinal);
        return source;
    }
}
