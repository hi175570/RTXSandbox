using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Infrastructure;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

public sealed class RayTracingRenderer : IRenderBackend, IMaterialPreviewSource
{
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly ShaderProgram _compute;
    private readonly ShaderProgram _display;
    private readonly int _voxelTexture;
    private readonly int _microCellTexture;
    private readonly int _microBoxBuffer;
    private readonly int _dynamicBoxBuffer;
    private readonly int _materialSurfaceTexture;
    private readonly int _materialEmissionTexture;
    private readonly int _materialFlagsTexture;
    private readonly int _materialAtlasTexture;
    private readonly int _materialDetailAtlasTexture;
    private readonly int _vertexArray;
    private ProceduralMaterialAtlas _materialAtlas = null!;
    private int _accumulationTexture;
    private int _width;
    private int _height;
    private int _viewportWidth;
    private int _viewportHeight;
    private float _renderScale;
    private int _frameIndex;
    private bool _disposed;
    private int _dynamicBoxCount;
    private bool _dynamicGridsDirty;
    private int _voxelOriginX;
    private int _voxelOriginZ;
    private bool _windowDirty;
    private readonly HashSet<ChunkCoordinate> _dirtyVoxelChunks = new();

    public int AccumulatedSamples => _frameIndex;
    public RenderBackendKind Kind => RenderBackendKind.SoftwareRayTracing;
    public RenderBackendCapabilities Capabilities =>
        RenderBackendCapabilities.ProgressiveAccumulation |
        RenderBackendCapabilities.GlobalIllumination |
        RenderBackendCapabilities.Reflections |
        RenderBackendCapabilities.Transmission |
        RenderBackendCapabilities.MicroVoxels;
    public string Name => "Compute Path Tracing";
    public int MaterialAtlasTexture => _materialAtlasTexture;
    public int MaterialTextureResolution => ProceduralMaterialAtlas.TileSize;

    public RayTracingRenderer(VoxelWorld world, BlockRegistry blocks, int width, int height, float renderScale = 1f)
    {
        _world = world;
        _blocks = blocks;

        string shaderRoot = Path.Combine(RuntimePaths.OutputRoot, "Shaders");
        _compute = new ShaderProgram(
            (ShaderType.ComputeShader, File.ReadAllText(Path.Combine(shaderRoot, "pathtrace.comp.glsl"))));
        _display = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "fullscreen.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "tonemap.frag.glsl"))));

        _vertexArray = GL.GenVertexArray();
        _voxelTexture = CreateVoxelTexture(world.CreateRenderWindowSnapshot(_voxelOriginX, _voxelOriginZ));
        _microCellTexture = CreateMicroCellTexture();
        _microBoxBuffer = GL.GenBuffer();
        _dynamicBoxBuffer = GL.GenBuffer();
        _materialSurfaceTexture = GL.GenTexture();
        _materialEmissionTexture = GL.GenTexture();
        _materialFlagsTexture = GL.GenTexture();
        _materialAtlasTexture = GL.GenTexture();
        _materialDetailAtlasTexture = GL.GenTexture();
        UploadMaterialTextures();
        UploadMicroVoxels();
        UploadDynamicGrids();
        _world.VoxelChanged += OnVoxelChanged;
        _world.WorldReloaded += UploadAllVoxels;
        _world.MicroVoxelsChanged += UploadMicroVoxels;
        _world.ChunkGenerated += OnChunkGenerated;
        _world.PhysicsGrids.Changed += OnPhysicsGridsChanged;
        _blocks.DefinitionsChanged += UploadMaterialTextures;
        Resize(width, height, renderScale);
    }

    public void Resize(int width, int height, float renderScale)
    {
        _viewportWidth = Math.Max(width, 1);
        _viewportHeight = Math.Max(height, 1);
        renderScale = Math.Clamp(renderScale, 0.35f, 1f);
        int traceWidth = Math.Max(1, (int)MathF.Round(_viewportWidth * renderScale));
        int traceHeight = Math.Max(1, (int)MathF.Round(_viewportHeight * renderScale));
        if (traceWidth == _width && traceHeight == _height && MathF.Abs(_renderScale - renderScale) < 0.001f)
        {
            return;
        }

        _width = traceWidth;
        _height = traceHeight;
        _renderScale = renderScale;
        if (_accumulationTexture != 0)
        {
            GL.DeleteTexture(_accumulationTexture);
        }

        _accumulationTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _accumulationTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba32f,
            _width,
            _height,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        ResetAccumulation();
    }

    public void ResetAccumulation() => _frameIndex = 0;

    public void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure)
    {
        ChunkCoordinate wantedOrigin = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(cameraPosition.X) - VoxelWorld.Width / 2,
            (int)MathF.Floor(cameraPosition.Z) - VoxelWorld.Depth / 2);
        if (wantedOrigin.MinimumX != _voxelOriginX || wantedOrigin.MinimumZ != _voxelOriginZ)
        {
            _voxelOriginX = wantedOrigin.MinimumX;
            _voxelOriginZ = wantedOrigin.MinimumZ;
            _windowDirty = true;
        }
        if (_windowDirty)
        {
            UploadAllVoxels();
            UploadMicroVoxels();
            _dirtyVoxelChunks.Clear();
            _windowDirty = false;
        }
        else if (_dirtyVoxelChunks.Count > 0)
        {
            UploadDirtyVoxelChunks();
        }

        // Physics can publish several substep changes between displayed frames.
        // Coalesce them into one SSBO upload instead of uploading the complete grid
        // set for every substep.
        if (_dynamicGridsDirty)
        {
            UploadDynamicGrids();
            _dynamicGridsDirty = false;
        }
        Vector3 forward = camera.Forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        _compute.Use();
        GL.Uniform2(_compute.Uniform("uResolution"), _width, _height);
        GL.Uniform3(_compute.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform3(_compute.Uniform("uCameraForward"), forward.X, forward.Y, forward.Z);
        GL.Uniform3(_compute.Uniform("uCameraRight"), right.X, right.Y, right.Z);
        GL.Uniform3(_compute.Uniform("uCameraUp"), up.X, up.Y, up.Z);
        GL.Uniform1(_compute.Uniform("uTanHalfFov"), MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f));
        GL.Uniform1(_compute.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(_compute.Uniform("uMaxBounces"), Math.Clamp(quality.PathBounces, 1, 12));
        GL.Uniform3(
            _compute.Uniform("uWorldOrigin"),
            _voxelOriginX,
            VoxelWorld.MinimumY,
            _voxelOriginZ);

        if (selection.HasValue)
        {
            VoxelHit hit = selection.Value;
            GL.Uniform1(_compute.Uniform("uHasSelection"), 1);
            GL.Uniform3(_compute.Uniform("uSelectedCell"), hit.X, hit.Y, hit.Z);
            GL.Uniform1(_compute.Uniform("uSelectedGrid"), (int)hit.GridId);
        }
        else
        {
            GL.Uniform1(_compute.Uniform("uHasSelection"), 0);
            GL.Uniform3(_compute.Uniform("uSelectedCell"), -1, -1, -1);
            GL.Uniform1(_compute.Uniform("uSelectedGrid"), -1);
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _voxelTexture);
        GL.Uniform1(_compute.Uniform("uVoxels"), 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture1D, _materialSurfaceTexture);
        GL.Uniform1(_compute.Uniform("uMaterialSurface"), 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture1D, _materialEmissionTexture);
        GL.Uniform1(_compute.Uniform("uMaterialEmission"), 2);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture1D, _materialFlagsTexture);
        GL.Uniform1(_compute.Uniform("uMaterialFlags"), 3);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.Uniform1(_compute.Uniform("uMaterialAtlas"), 4);
        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(TextureTarget.Texture2D, _materialDetailAtlasTexture);
        GL.Uniform1(_compute.Uniform("uMaterialDetailAtlas"), 5);
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture3D, _microCellTexture);
        GL.Uniform1(_compute.Uniform("uMicroCells"), 6);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _microBoxBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _dynamicBoxBuffer);
        GL.Uniform1(_compute.Uniform("uDynamicBoxCount"), _dynamicBoxCount);
        GL.BindImageTexture(0, _accumulationTexture, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba32f);
        int samplesThisFrame = Math.Clamp(quality.PathSamplesPerFrame, 1, 8);
        int maximumSamples = Math.Clamp(quality.PathMaxAccumulatedSamples, 64, 16_384);
        for (int sample = 0; sample < samplesThisFrame && _frameIndex < maximumSamples; sample++)
        {
            GL.Uniform1(_compute.Uniform("uFrameIndex"), _frameIndex);
            GL.DispatchCompute((_width + 7) / 8, (_height + 7) / 8, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
            _frameIndex++;
        }

        GL.Disable(EnableCap.DepthTest);
        GL.Viewport(0, 0, _viewportWidth, _viewportHeight);
        _display.Use();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _accumulationTexture);
        GL.Uniform1(_display.Uniform("uImage"), 0);
        GL.Uniform2(_display.Uniform("uViewport"), (float)_viewportWidth, (float)_viewportHeight);
        GL.Uniform1(_display.Uniform("uExposure"), exposure);
        GL.BindVertexArray(_vertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

    }

    private static int CreateVoxelTexture(ushort[] data)
    {
        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.R16ui,
            VoxelWorld.Width,
            VoxelWorld.Height,
            VoxelWorld.Depth,
            0,
            PixelFormat.RedInteger,
            PixelType.UnsignedShort,
            data);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
        return texture;
    }

    private static int CreateMicroCellTexture()
    {
        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture3D, texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            0,
            PixelInternalFormat.Rg32ui,
            VoxelWorld.Width,
            VoxelWorld.Height,
            VoxelWorld.Depth,
            0,
            PixelFormat.RgInteger,
            PixelType.UnsignedInt,
            IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
        return texture;
    }

    private void UploadMicroVoxels()
    {
        var cells = new uint[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth * 2];
        var boxes = new List<float>();
        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in _world.SnapshotMicroVoxels())
        {
            int localX = x - _voxelOriginX;
            int localY = y - VoxelWorld.MinimumY;
            int localZ = z - _voxelOriginZ;
            if (localX < 0 || localX >= VoxelWorld.Width || localY < 0 || localY >= VoxelWorld.Height ||
                localZ < 0 || localZ >= VoxelWorld.Depth) continue;
            int cellIndex = localX + VoxelWorld.Width * (localY + VoxelWorld.Height * localZ);
            cells[cellIndex * 2] = (uint)(boxes.Count / 8);
            cells[cellIndex * 2 + 1] = (uint)snapshot.Boxes.Count;
            float inverse = 1f / snapshot.Resolution;
            foreach (MicroVoxelBox box in snapshot.Boxes)
            {
                boxes.Add(x + box.MinX * inverse);
                boxes.Add(y + box.MinY * inverse);
                boxes.Add(z + box.MinZ * inverse);
                boxes.Add(box.MaterialId);
                boxes.Add(x + box.MaxX * inverse);
                boxes.Add(y + box.MaxY * inverse);
                boxes.Add(z + box.MaxZ * inverse);
                boxes.Add(0f);
            }
        }

        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture3D, _microCellTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage3D(
            TextureTarget.Texture3D,
            0,
            0,
            0,
            0,
            VoxelWorld.Width,
            VoxelWorld.Height,
            VoxelWorld.Depth,
            PixelFormat.RgInteger,
            PixelType.UnsignedInt,
            cells);

        float[] boxData = boxes.Count == 0 ? new float[8] : boxes.ToArray();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _microBoxBuffer);
        GL.BufferData(
            BufferTarget.ShaderStorageBuffer,
            boxData.Length * sizeof(float),
            boxData,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        ResetAccumulation();
    }

    private void UploadDynamicGrids()
    {
        var data = new List<float>();
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            Vector3 center = grid.LocalToWorld(element.Center);
            Vector3 half = element.HalfExtents;
            Quaternion rotation = grid.Orientation;
            data.Add(center.X); data.Add(center.Y); data.Add(center.Z); data.Add(element.MaterialId);
            data.Add(half.X); data.Add(half.Y); data.Add(half.Z); data.Add(grid.Id);
            data.Add(rotation.X); data.Add(rotation.Y); data.Add(rotation.Z); data.Add(rotation.W);
            data.Add(element.CellX); data.Add(element.CellY); data.Add(element.CellZ); data.Add(0f);
        }
        _dynamicBoxCount = data.Count / 16;
        float[] upload = data.Count == 0 ? new float[16] : data.ToArray();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _dynamicBoxBuffer);
        GL.BufferData(
            BufferTarget.ShaderStorageBuffer,
            upload.Length * sizeof(float),
            upload,
            BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        ResetAccumulation();
    }

    private void OnPhysicsGridsChanged() => _dynamicGridsDirty = true;

    private void OnVoxelChanged(int x, int y, int z, VoxelMaterial material)
    {
        int localX = x - _voxelOriginX;
        int localY = y - VoxelWorld.MinimumY;
        int localZ = z - _voxelOriginZ;
        if (localX < 0 || localX >= VoxelWorld.Width || localY < 0 || localY >= VoxelWorld.Height ||
            localZ < 0 || localZ >= VoxelWorld.Depth) return;
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _voxelTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage3D(
            TextureTarget.Texture3D,
            0,
            localX,
            localY,
            localZ,
            1,
            1,
            1,
            PixelFormat.RedInteger,
            PixelType.UnsignedShort,
            new[] { (ushort)material });
        ResetAccumulation();
    }

    private void UploadAllVoxels()
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _voxelTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage3D(
            TextureTarget.Texture3D,
            0,
            0,
            0,
            0,
            VoxelWorld.Width,
            VoxelWorld.Height,
            VoxelWorld.Depth,
            PixelFormat.RedInteger,
            PixelType.UnsignedShort,
            _world.CreateRenderWindowSnapshot(_voxelOriginX, _voxelOriginZ));
        ResetAccumulation();
    }

    private void UploadDirtyVoxelChunks()
    {
        bool uploaded = false;
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _voxelTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        foreach (ChunkCoordinate chunk in _dirtyVoxelChunks.ToArray())
        {
            _dirtyVoxelChunks.Remove(chunk);
            int localX = chunk.MinimumX - _voxelOriginX;
            int localZ = chunk.MinimumZ - _voxelOriginZ;
            if (localX < 0 || localX + ChunkCoordinate.Size > VoxelWorld.Width ||
                localZ < 0 || localZ + ChunkCoordinate.Size > VoxelWorld.Depth)
                continue;
            GL.TexSubImage3D(
                TextureTarget.Texture3D, 0,
                localX, 0, localZ,
                ChunkCoordinate.Size, VoxelWorld.Height, ChunkCoordinate.Size,
                PixelFormat.RedInteger, PixelType.UnsignedShort,
                _world.CreateRenderChunkSnapshot(chunk));
            uploaded = true;
        }
        if (uploaded) ResetAccumulation();
    }

    private void OnChunkGenerated(ChunkCoordinate chunk)
    {
        int maximumX = _voxelOriginX + VoxelWorld.Width;
        int maximumZ = _voxelOriginZ + VoxelWorld.Depth;
        if (chunk.MinimumX < maximumX && chunk.MinimumX + ChunkCoordinate.Size > _voxelOriginX &&
            chunk.MinimumZ < maximumZ && chunk.MinimumZ + ChunkCoordinate.Size > _voxelOriginZ)
            _dirtyVoxelChunks.Add(chunk);
    }

    private void UploadMaterialTextures()
    {
        _materialAtlas = new ProceduralMaterialAtlas(_blocks);
        var surface = new float[BlockRegistry.MaterialCapacity * 4];
        var emission = new float[BlockRegistry.MaterialCapacity * 4];
        var flags = new byte[BlockRegistry.MaterialCapacity * 4];
        for (int id = 0; id < BlockRegistry.MaterialCapacity; id++)
        {
            surface[id * 4] = 1f;
            surface[id * 4 + 1] = 0f;
            surface[id * 4 + 2] = 1f;
            surface[id * 4 + 3] = 0.7f;
        }

        foreach (BlockDefinition definition in _blocks.Definitions)
        {
            int offset = definition.Id * 4;
            surface[offset] = definition.Albedo.X;
            surface[offset + 1] = definition.Albedo.Y;
            surface[offset + 2] = definition.Albedo.Z;
            surface[offset + 3] = definition.Roughness;
            emission[offset] = definition.Emission.X;
            emission[offset + 1] = definition.Emission.Y;
            emission[offset + 2] = definition.Emission.Z;
            emission[offset + 3] = definition.Metallic;
            flags[offset] = definition.IsFluid ? (byte)1 : (byte)0;
            flags[offset + 1] = (byte)definition.SurfacePattern;
            flags[offset + 2] = (byte)Math.Clamp((int)MathF.Round(definition.Transmission * 255f), 0, 255);
            flags[offset + 3] = (byte)Math.Clamp((int)MathF.Round(definition.IndexOfRefraction * 100f), 100, 255);
        }

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture1D, _materialSurfaceTexture);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgba32f, BlockRegistry.MaterialCapacity, 0, PixelFormat.Rgba, PixelType.Float, surface);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture1D, _materialEmissionTexture);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgba32f, BlockRegistry.MaterialCapacity, 0, PixelFormat.Rgba, PixelType.Float, emission);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture1D, _materialFlagsTexture);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgba8ui, BlockRegistry.MaterialCapacity, 0, PixelFormat.RgbaInteger, PixelType.UnsignedByte, flags);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        GL.ActiveTexture(TextureUnit.Texture4);
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

        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(TextureTarget.Texture2D, _materialDetailAtlasTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rg8,
            ProceduralMaterialAtlas.Width,
            ProceduralMaterialAtlas.Height,
            0,
            PixelFormat.Rg,
            PixelType.UnsignedByte,
            _materialAtlas.DetailPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        ResetAccumulation();
    }

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        _materialAtlas.GetTileUv(materialId, face);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _world.VoxelChanged -= OnVoxelChanged;
        _world.WorldReloaded -= UploadAllVoxels;
        _world.MicroVoxelsChanged -= UploadMicroVoxels;
        _world.ChunkGenerated -= OnChunkGenerated;
        _world.PhysicsGrids.Changed -= OnPhysicsGridsChanged;
        _blocks.DefinitionsChanged -= UploadMaterialTextures;
        GL.DeleteTexture(_voxelTexture);
        GL.DeleteTexture(_microCellTexture);
        GL.DeleteBuffer(_microBoxBuffer);
        GL.DeleteBuffer(_dynamicBoxBuffer);
        GL.DeleteTexture(_materialSurfaceTexture);
        GL.DeleteTexture(_materialEmissionTexture);
        GL.DeleteTexture(_materialFlagsTexture);
        GL.DeleteTexture(_materialAtlasTexture);
        GL.DeleteTexture(_materialDetailAtlasTexture);
        GL.DeleteTexture(_accumulationTexture);
        GL.DeleteVertexArray(_vertexArray);
        _compute.Dispose();
        _display.Dispose();
    }
}
