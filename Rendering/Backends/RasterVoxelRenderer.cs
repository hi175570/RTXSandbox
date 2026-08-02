using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.World;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OpenTkVector3 = OpenTK.Mathematics.Vector3;

namespace RTXSandbox.Rendering.Backends;

/// <summary>
/// Shared real-time voxel raster backend. PBR Forward and Classic are separate modes,
/// but deliberately share chunk-mesh and atlas infrastructure.
/// </summary>
public sealed class RasterVoxelRenderer : IRenderBackend, IMaterialPreviewSource
{
    private const int FloatsPerVertex = 20;

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly bool _classic;
    private readonly ShaderProgram _shader;
    private readonly int _vertexArray;
    private readonly int _vertexBuffer;
    private readonly int _materialAtlasTexture;
    private ProceduralMaterialAtlas _materialAtlas = null!;
    private int _vertexCount;
    private int _width;
    private int _height;
    private bool _meshDirty = true;
    private bool _disposed;

    public RenderBackendKind Kind => _classic ? RenderBackendKind.Classic : RenderBackendKind.PbrForward;
    public RenderBackendCapabilities Capabilities => _classic
        ? RenderBackendCapabilities.RealTime | RenderBackendCapabilities.LowEnd | RenderBackendCapabilities.MicroVoxels
        : RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
          RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels;
    public string Name => _classic ? "Classic Textured Raster" : "PBR Forward Raster";
    public int AccumulatedSamples => 1;
    public int MaterialAtlasTexture => _materialAtlasTexture;
    public int MaterialTextureResolution => ProceduralMaterialAtlas.TileSize;

    public RasterVoxelRenderer(
        VoxelWorld world,
        BlockRegistry blocks,
        int width,
        int height,
        bool classic)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _classic = classic;
        string shaderRoot = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _shader = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.frag.glsl"))));

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(4);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, 12 * sizeof(float));
        GL.EnableVertexAttribArray(5);
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, 16 * sizeof(float));
        GL.BindVertexArray(0);

        _materialAtlasTexture = GL.GenTexture();
        UploadMaterialAtlas();
        Resize(width, height, 1f);
        _world.VoxelChanged += OnVoxelChanged;
        _world.WorldReloaded += OnWorldReloaded;
        _world.MicroVoxelsChanged += OnWorldReloaded;
        _world.PhysicsGrids.Changed += OnWorldReloaded;
        _blocks.DefinitionsChanged += OnDefinitionsChanged;
    }

    public void Resize(int width, int height, float renderScale)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public void ResetAccumulation()
    {
        // Real-time raster backends have no history buffer.
    }

    public void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        int pathBounces,
        float exposure)
    {
        if (_meshDirty) RebuildMesh();

        float daylight = Math.Clamp(MathF.Sin((timeOfDay - 6f) * MathF.PI / 12f) * 0.72f + 0.28f, 0.04f, 1f);
        GL.ClearColor(
            0.018f + daylight * 0.10f,
            0.028f + daylight * 0.24f,
            0.06f + daylight * 0.48f,
            1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Viewport(0, 0, _width, _height);

        var eye = new OpenTkVector3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        Vector3 forward = camera.Forward;
        var target = eye + new OpenTkVector3(forward.X, forward.Y, forward.Z);
        Matrix4 view = Matrix4.LookAt(eye, target, OpenTkVector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewDegrees * MathF.PI / 180f,
            _width / (float)Math.Max(_height, 1),
            0.03f,
            220f);

        _shader.Use();
        GL.UniformMatrix4(_shader.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(_shader.Uniform("uProjection"), false, ref projection);
        GL.Uniform3(_shader.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(_shader.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(_shader.Uniform("uExposure"), exposure);
        GL.Uniform1(_shader.Uniform("uClassic"), _classic ? 1 : 0);
        if (selection.HasValue)
        {
            GL.Uniform1(_shader.Uniform("uHasSelection"), 1);
            GL.Uniform3(_shader.Uniform("uSelectedCell"), selection.Value.X, selection.Value.Y, selection.Value.Z);
            GL.Uniform1(_shader.Uniform("uSelectedGrid"), (int)selection.Value.GridId);
        }
        else
        {
            GL.Uniform1(_shader.Uniform("uHasSelection"), 0);
            GL.Uniform3(_shader.Uniform("uSelectedCell"), -1, -1, -1);
            GL.Uniform1(_shader.Uniform("uSelectedGrid"), -1);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.Uniform1(_shader.Uniform("uMaterialAtlas"), 0);
        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        GL.BindVertexArray(0);
    }

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        _materialAtlas.GetTileUv(materialId, face);

    private void RebuildMesh()
    {
        var vertices = new List<float>(400_000);
        for (int z = 0; z < VoxelWorld.Depth; z++)
        for (int y = 0; y < VoxelWorld.Height; y++)
        for (int x = 0; x < VoxelWorld.Width; x++)
        {
            ushort material = _world.GetBlockId(x, y, z);
            if (material == 0) continue;
            AddRegularVoxel(vertices, x, y, z, material);
        }

        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in _world.SnapshotMicroVoxels())
        {
            float inverse = 1f / snapshot.Resolution;
            foreach (MicroVoxelBox box in snapshot.Boxes)
            {
                AddBox(
                    vertices,
                    new Vector3(x + box.MinX * inverse, y + box.MinY * inverse, z + box.MinZ * inverse),
                    new Vector3(x + box.MaxX * inverse, y + box.MaxY * inverse, z + box.MaxZ * inverse),
                    box.MaterialId,
                    face => true,
                    x, y, z, 0);
            }
        }

        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            AddBox(
                vertices,
                element.Minimum,
                element.Maximum,
                element.MaterialId,
                face => true,
                element.CellX,
                element.CellY,
                element.CellZ,
                (int)grid.Id,
                grid);
        }

        float[] data = vertices.Count == 0 ? new float[FloatsPerVertex] : vertices.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        _vertexCount = vertices.Count / FloatsPerVertex;
        _meshDirty = false;
    }

    private void AddRegularVoxel(List<float> vertices, int x, int y, int z, ushort material)
    {
        AddBox(
            vertices,
            new Vector3(x, y, z),
            new Vector3(x + 1f, y + 1f, z + 1f),
            material,
            face => face switch
            {
                0 => ShouldRenderFace(x, y + 1, z, material),
                1 => ShouldRenderFace(x - 1, y, z, material),
                2 => ShouldRenderFace(x + 1, y, z, material),
                3 => ShouldRenderFace(x, y, z - 1, material),
                4 => ShouldRenderFace(x, y, z + 1, material),
                _ => ShouldRenderFace(x, y - 1, z, material),
            },
            x, y, z, 0);
    }

    private bool ShouldRenderFace(int x, int y, int z, ushort currentMaterial)
    {
        if (!_world.IsInside(x, y, z)) return true;
        ushort neighbor = _world.GetBlockId(x, y, z);
        if (neighbor == 0) return true;
        if (neighbor == currentMaterial) return false;
        BlockDefinition current = _blocks.Get(currentMaterial);
        BlockDefinition adjacent = _blocks.Get(neighbor);
        return current.Transmission > 0f || current.IsFluid || adjacent.Transmission > 0f || adjacent.IsFluid;
    }

    private void AddBox(
        List<float> vertices,
        Vector3 minimum,
        Vector3 maximum,
        ushort materialId,
        Func<int, bool> includeFace,
        int cellX,
        int cellY,
        int cellZ,
        int gridId,
        PhysicsGrid? grid = null)
    {
        // Face order is top, -X, +X, -Z, +Z, bottom. Atlas face is top/side/bottom.
        Vector3[][] faces =
        {
            new[] { new Vector3(minimum.X, maximum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, minimum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, minimum.Z) },
            new[] { new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(minimum.X, minimum.Y, minimum.Z) },
        };
        Vector3[] normals = { Vector3.UnitY, -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitY };
        Vector2[] uvs = { new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f) };
        int[] triangles = { 0, 1, 2, 0, 2, 3 };
        BlockDefinition definition = _blocks.Get(materialId);
        for (int face = 0; face < faces.Length; face++)
        {
            if (!includeFace(face)) continue;
            int atlasFace = face == 0 ? 0 : face == 5 ? 2 : 1;
            foreach (int corner in triangles)
            {
                Vector3 position = faces[face][corner];
                Vector3 normal = normals[face];
                if (grid is not null)
                {
                    position = grid.LocalToWorld(position);
                    normal = Vector3.Normalize(grid.LocalDirectionToWorld(normal));
                }
                Vector2 uv = uvs[corner];
                vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
                vertices.Add(normal.X); vertices.Add(normal.Y); vertices.Add(normal.Z);
                vertices.Add(uv.X); vertices.Add(uv.Y);
                vertices.Add(materialId); vertices.Add(atlasFace); vertices.Add(definition.Roughness); vertices.Add(definition.Metallic);
                vertices.Add(definition.Transmission);
                vertices.Add(definition.Emission.X); vertices.Add(definition.Emission.Y); vertices.Add(definition.Emission.Z);
                vertices.Add(cellX); vertices.Add(cellY); vertices.Add(cellZ); vertices.Add(gridId);
            }
        }
    }

    private void UploadMaterialAtlas()
    {
        _materialAtlas = new ProceduralMaterialAtlas(_blocks);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba8,
            ProceduralMaterialAtlas.Width,
            ProceduralMaterialAtlas.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            _materialAtlas.ColorPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private void OnVoxelChanged(int x, int y, int z, VoxelMaterial material) => _meshDirty = true;
    private void OnWorldReloaded() => _meshDirty = true;
    private void OnDefinitionsChanged()
    {
        UploadMaterialAtlas();
        _meshDirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.VoxelChanged -= OnVoxelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelsChanged -= OnWorldReloaded;
        _world.PhysicsGrids.Changed -= OnWorldReloaded;
        _blocks.DefinitionsChanged -= OnDefinitionsChanged;
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteVertexArray(_vertexArray);
        GL.DeleteTexture(_materialAtlasTexture);
        _shader.Dispose();
    }
}
