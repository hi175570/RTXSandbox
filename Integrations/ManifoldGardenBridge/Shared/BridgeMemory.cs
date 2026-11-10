using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

#pragma warning disable CA1416 // This integration is Windows-only and named mappings are intentional.

namespace RTXSandbox.ManifoldGardenBridge;

public readonly struct BridgeSnapshot
{
    public BridgeSnapshot(
        int producerProcessId,
        long producerUtcTicks,
        int frameCount,
        bool hasCamera,
        float positionX,
        float positionY,
        float positionZ,
        float rotationX,
        float rotationY,
        float rotationZ,
        float rotationW,
        float fieldOfView,
        int screenWidth,
        int screenHeight,
        long sequence)
    {
        ProducerProcessId = producerProcessId;
        ProducerUtcTicks = producerUtcTicks;
        FrameCount = frameCount;
        HasCamera = hasCamera;
        PositionX = positionX;
        PositionY = positionY;
        PositionZ = positionZ;
        RotationX = rotationX;
        RotationY = rotationY;
        RotationZ = rotationZ;
        RotationW = rotationW;
        FieldOfView = fieldOfView;
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
        Sequence = sequence;
    }

    public int ProducerProcessId { get; }
    public long ProducerUtcTicks { get; }
    public int FrameCount { get; }
    public bool HasCamera { get; }
    public float PositionX { get; }
    public float PositionY { get; }
    public float PositionZ { get; }
    public float RotationX { get; }
    public float RotationY { get; }
    public float RotationZ { get; }
    public float RotationW { get; }
    public float FieldOfView { get; }
    public int ScreenWidth { get; }
    public int ScreenHeight { get; }
    public long Sequence { get; }
    public DateTime ProducerUtc => new(ProducerUtcTicks, DateTimeKind.Utc);
}

public readonly struct BridgePlacementSnapshot
{
    public BridgePlacementSnapshot(
        long sequence,
        int gridX,
        int gridY,
        int gridZ,
        float worldX,
        float worldY,
        float worldZ,
        float rotationX,
        float rotationY,
        float rotationZ,
        float rotationW,
        float normalX,
        float normalY,
        float normalZ,
        ushort materialId,
        BridgeBlockAction action = BridgeBlockAction.Place,
        float blockSize = 1f)
    {
        Sequence = sequence;
        GridX = gridX;
        GridY = gridY;
        GridZ = gridZ;
        WorldX = worldX;
        WorldY = worldY;
        WorldZ = worldZ;
        RotationX = rotationX;
        RotationY = rotationY;
        RotationZ = rotationZ;
        RotationW = rotationW;
        NormalX = normalX;
        NormalY = normalY;
        NormalZ = normalZ;
        MaterialId = materialId;
        Action = action;
        BlockSize = blockSize;
    }

    public long Sequence { get; }
    public int GridX { get; }
    public int GridY { get; }
    public int GridZ { get; }
    public float WorldX { get; }
    public float WorldY { get; }
    public float WorldZ { get; }
    public float RotationX { get; }
    public float RotationY { get; }
    public float RotationZ { get; }
    public float RotationW { get; }
    public float NormalX { get; }
    public float NormalY { get; }
    public float NormalZ { get; }
    public ushort MaterialId { get; }
    public BridgeBlockAction Action { get; }
    public float BlockSize { get; }
}

public enum BridgeBlockAction
{
    None = 0,
    Place = 1,
    Remove = 2,
}

public static class BridgeProtocol
{
    public const string MapName = "Local\\RTXSandbox.ManifoldGarden.v2";
    public const int Capacity = 4096;
    public const int Magic = 0x4D475231;
    public const int Version = 2;

    internal const int MagicOffset = 0;
    internal const int VersionOffset = 4;
    internal const int SizeOffset = 8;
    internal const int FlagsOffset = 12;
    internal const int SequenceOffset = 16;
    internal const int UtcTicksOffset = 24;
    internal const int ProcessIdOffset = 32;
    internal const int FrameCountOffset = 36;
    internal const int PositionXOffset = 40;
    internal const int PositionYOffset = 44;
    internal const int PositionZOffset = 48;
    internal const int RotationXOffset = 52;
    internal const int RotationYOffset = 56;
    internal const int RotationZOffset = 60;
    internal const int RotationWOffset = 64;
    internal const int FieldOfViewOffset = 68;
    internal const int ScreenWidthOffset = 72;
    internal const int ScreenHeightOffset = 76;
    internal const int ConsumerProcessIdOffset = 80;
    internal const int ConsumerFrameCountOffset = 84;
    internal const int ConsumerUtcTicksOffset = 88;
    internal const int ConsumerSelectedBlockIdOffset = 96;
    internal const int PlacementSequenceOffset = 104;
    internal const int PlacementGridXOffset = 112;
    internal const int PlacementGridYOffset = 116;
    internal const int PlacementGridZOffset = 120;
    internal const int PlacementWorldXOffset = 124;
    internal const int PlacementWorldYOffset = 128;
    internal const int PlacementWorldZOffset = 132;
    internal const int PlacementRotationXOffset = 136;
    internal const int PlacementRotationYOffset = 140;
    internal const int PlacementRotationZOffset = 144;
    internal const int PlacementRotationWOffset = 148;
    internal const int PlacementNormalXOffset = 152;
    internal const int PlacementNormalYOffset = 156;
    internal const int PlacementNormalZOffset = 160;
    internal const int PlacementMaterialIdOffset = 164;
    internal const int ConsumerPlacementSequenceOffset = 168;
    internal const int ConsumerSelectedHotbarSlotOffset = 176;
    internal const int ConsumerHotbarStartOffset = 180;
    public const int HotbarSlotCount = 9;
    internal const int PlacementActionOffset = 216;
    internal const int PlacementBlockSizeOffset = 220;
    internal const int PayloadSize = 224;
    internal const int HasCameraFlag = 1;
}

public sealed class BridgeWriter : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private long _sequence;
    private long _placementSequence;

    public BridgeWriter()
    {
        _mapping = MemoryMappedFile.CreateOrOpen(
            BridgeProtocol.MapName,
            BridgeProtocol.Capacity,
            MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, BridgeProtocol.Capacity, MemoryMappedFileAccess.ReadWrite);
        _view.Write(BridgeProtocol.MagicOffset, BridgeProtocol.Magic);
        _view.Write(BridgeProtocol.VersionOffset, BridgeProtocol.Version);
        _view.Write(BridgeProtocol.SizeOffset, BridgeProtocol.PayloadSize);
        _view.Write(BridgeProtocol.SequenceOffset, 0L);
        _view.Write(BridgeProtocol.PlacementSequenceOffset, 0L);
    }

    public void Write(in BridgeSnapshot state)
    {
        long completedSequence = Interlocked.Add(ref _sequence, 2);
        _view.Write(BridgeProtocol.SequenceOffset, completedSequence - 1);
        Thread.MemoryBarrier();

        _view.Write(BridgeProtocol.MagicOffset, BridgeProtocol.Magic);
        _view.Write(BridgeProtocol.VersionOffset, BridgeProtocol.Version);
        _view.Write(BridgeProtocol.SizeOffset, BridgeProtocol.PayloadSize);
        _view.Write(BridgeProtocol.FlagsOffset, state.HasCamera ? BridgeProtocol.HasCameraFlag : 0);
        _view.Write(BridgeProtocol.UtcTicksOffset, state.ProducerUtcTicks);
        _view.Write(BridgeProtocol.ProcessIdOffset, state.ProducerProcessId);
        _view.Write(BridgeProtocol.FrameCountOffset, state.FrameCount);
        _view.Write(BridgeProtocol.PositionXOffset, state.PositionX);
        _view.Write(BridgeProtocol.PositionYOffset, state.PositionY);
        _view.Write(BridgeProtocol.PositionZOffset, state.PositionZ);
        _view.Write(BridgeProtocol.RotationXOffset, state.RotationX);
        _view.Write(BridgeProtocol.RotationYOffset, state.RotationY);
        _view.Write(BridgeProtocol.RotationZOffset, state.RotationZ);
        _view.Write(BridgeProtocol.RotationWOffset, state.RotationW);
        _view.Write(BridgeProtocol.FieldOfViewOffset, state.FieldOfView);
        _view.Write(BridgeProtocol.ScreenWidthOffset, state.ScreenWidth);
        _view.Write(BridgeProtocol.ScreenHeightOffset, state.ScreenHeight);

        Thread.MemoryBarrier();
        _view.Write(BridgeProtocol.SequenceOffset, completedSequence);
    }

    public long WritePlacement(
        int gridX,
        int gridY,
        int gridZ,
        float worldX,
        float worldY,
        float worldZ,
        float rotationX,
        float rotationY,
        float rotationZ,
        float rotationW,
        float normalX,
        float normalY,
        float normalZ,
        ushort materialId,
        BridgeBlockAction action = BridgeBlockAction.Place,
        float blockSize = 1f)
    {
        long completedSequence = Interlocked.Add(ref _placementSequence, 2);
        _view.Write(BridgeProtocol.PlacementSequenceOffset, completedSequence - 1);
        Thread.MemoryBarrier();
        _view.Write(BridgeProtocol.PlacementGridXOffset, gridX);
        _view.Write(BridgeProtocol.PlacementGridYOffset, gridY);
        _view.Write(BridgeProtocol.PlacementGridZOffset, gridZ);
        _view.Write(BridgeProtocol.PlacementWorldXOffset, worldX);
        _view.Write(BridgeProtocol.PlacementWorldYOffset, worldY);
        _view.Write(BridgeProtocol.PlacementWorldZOffset, worldZ);
        _view.Write(BridgeProtocol.PlacementRotationXOffset, rotationX);
        _view.Write(BridgeProtocol.PlacementRotationYOffset, rotationY);
        _view.Write(BridgeProtocol.PlacementRotationZOffset, rotationZ);
        _view.Write(BridgeProtocol.PlacementRotationWOffset, rotationW);
        _view.Write(BridgeProtocol.PlacementNormalXOffset, normalX);
        _view.Write(BridgeProtocol.PlacementNormalYOffset, normalY);
        _view.Write(BridgeProtocol.PlacementNormalZOffset, normalZ);
        _view.Write(BridgeProtocol.PlacementMaterialIdOffset, (int)materialId);
        _view.Write(BridgeProtocol.PlacementActionOffset, (int)action);
        _view.Write(BridgeProtocol.PlacementBlockSizeOffset, blockSize);
        Thread.MemoryBarrier();
        _view.Write(BridgeProtocol.PlacementSequenceOffset, completedSequence);
        return completedSequence;
    }

    public bool TryReadConsumer(out int processId, out int frameCount, out DateTime utc, out ushort selectedBlockId)
    {
        processId = _view.ReadInt32(BridgeProtocol.ConsumerProcessIdOffset);
        frameCount = _view.ReadInt32(BridgeProtocol.ConsumerFrameCountOffset);
        selectedBlockId = (ushort)Math.Clamp(
            _view.ReadInt32(BridgeProtocol.ConsumerSelectedBlockIdOffset),
            ushort.MinValue,
            ushort.MaxValue);
        long ticks = _view.ReadInt64(BridgeProtocol.ConsumerUtcTicksOffset);
        if (processId <= 0 || ticks <= 0)
        {
            utc = default;
            return false;
        }

        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    public long ReadAcknowledgedPlacementSequence() =>
        _view.ReadInt64(BridgeProtocol.ConsumerPlacementSequenceOffset);

    public void ReadConsumerHotbar(ushort[] destination, out int selectedSlot)
    {
        if (destination is null || destination.Length < BridgeProtocol.HotbarSlotCount)
        {
            throw new ArgumentException(
                $"A {BridgeProtocol.HotbarSlotCount}-slot destination is required.",
                nameof(destination));
        }

        selectedSlot = Math.Clamp(
            _view.ReadInt32(BridgeProtocol.ConsumerSelectedHotbarSlotOffset),
            0,
            BridgeProtocol.HotbarSlotCount - 1);
        for (int index = 0; index < BridgeProtocol.HotbarSlotCount; index++)
        {
            destination[index] = (ushort)Math.Clamp(
                _view.ReadInt32(BridgeProtocol.ConsumerHotbarStartOffset + index * sizeof(int)),
                ushort.MinValue,
                ushort.MaxValue);
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}

public sealed class BridgeReader : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;

    private BridgeReader(MemoryMappedFile mapping)
    {
        _mapping = mapping;
        _view = mapping.CreateViewAccessor(0, BridgeProtocol.Capacity, MemoryMappedFileAccess.ReadWrite);
    }

    public static bool TryOpen(out BridgeReader? reader)
    {
        try
        {
            reader = new BridgeReader(MemoryMappedFile.OpenExisting(
                BridgeProtocol.MapName,
                MemoryMappedFileRights.ReadWrite));
            return true;
        }
        catch (System.IO.FileNotFoundException)
        {
            reader = null;
            return false;
        }
    }

    public bool TryRead(out BridgeSnapshot snapshot)
    {
        snapshot = default;
        if (_view.ReadInt32(BridgeProtocol.MagicOffset) != BridgeProtocol.Magic ||
            _view.ReadInt32(BridgeProtocol.VersionOffset) != BridgeProtocol.Version ||
            _view.ReadInt32(BridgeProtocol.SizeOffset) < BridgeProtocol.PayloadSize)
        {
            return false;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            long start = _view.ReadInt64(BridgeProtocol.SequenceOffset);
            if ((start & 1) != 0)
            {
                Thread.Yield();
                continue;
            }

            int flags = _view.ReadInt32(BridgeProtocol.FlagsOffset);
            int processId = _view.ReadInt32(BridgeProtocol.ProcessIdOffset);
            long utcTicks = _view.ReadInt64(BridgeProtocol.UtcTicksOffset);
            int frameCount = _view.ReadInt32(BridgeProtocol.FrameCountOffset);
            float positionX = _view.ReadSingle(BridgeProtocol.PositionXOffset);
            float positionY = _view.ReadSingle(BridgeProtocol.PositionYOffset);
            float positionZ = _view.ReadSingle(BridgeProtocol.PositionZOffset);
            float rotationX = _view.ReadSingle(BridgeProtocol.RotationXOffset);
            float rotationY = _view.ReadSingle(BridgeProtocol.RotationYOffset);
            float rotationZ = _view.ReadSingle(BridgeProtocol.RotationZOffset);
            float rotationW = _view.ReadSingle(BridgeProtocol.RotationWOffset);
            float fieldOfView = _view.ReadSingle(BridgeProtocol.FieldOfViewOffset);
            int screenWidth = _view.ReadInt32(BridgeProtocol.ScreenWidthOffset);
            int screenHeight = _view.ReadInt32(BridgeProtocol.ScreenHeightOffset);

            Thread.MemoryBarrier();
            long end = _view.ReadInt64(BridgeProtocol.SequenceOffset);
            if (start == end && (end & 1) == 0)
            {
                snapshot = new BridgeSnapshot(
                    processId,
                    utcTicks,
                    frameCount,
                    (flags & BridgeProtocol.HasCameraFlag) != 0,
                    positionX,
                    positionY,
                    positionZ,
                    rotationX,
                    rotationY,
                    rotationZ,
                    rotationW,
                    fieldOfView,
                    screenWidth,
                    screenHeight,
                    end);
                return true;
            }
        }

        return false;
    }

    public bool TryReadPlacement(out BridgePlacementSnapshot placement)
    {
        placement = default;
        long sequence = _view.ReadInt64(BridgeProtocol.PlacementSequenceOffset);
        if (sequence <= 0 || (sequence & 1) != 0)
        {
            return false;
        }

        int gridX = _view.ReadInt32(BridgeProtocol.PlacementGridXOffset);
        int gridY = _view.ReadInt32(BridgeProtocol.PlacementGridYOffset);
        int gridZ = _view.ReadInt32(BridgeProtocol.PlacementGridZOffset);
        float worldX = _view.ReadSingle(BridgeProtocol.PlacementWorldXOffset);
        float worldY = _view.ReadSingle(BridgeProtocol.PlacementWorldYOffset);
        float worldZ = _view.ReadSingle(BridgeProtocol.PlacementWorldZOffset);
        float rotationX = _view.ReadSingle(BridgeProtocol.PlacementRotationXOffset);
        float rotationY = _view.ReadSingle(BridgeProtocol.PlacementRotationYOffset);
        float rotationZ = _view.ReadSingle(BridgeProtocol.PlacementRotationZOffset);
        float rotationW = _view.ReadSingle(BridgeProtocol.PlacementRotationWOffset);
        float normalX = _view.ReadSingle(BridgeProtocol.PlacementNormalXOffset);
        float normalY = _view.ReadSingle(BridgeProtocol.PlacementNormalYOffset);
        float normalZ = _view.ReadSingle(BridgeProtocol.PlacementNormalZOffset);
        ushort materialId = (ushort)Math.Clamp(
            _view.ReadInt32(BridgeProtocol.PlacementMaterialIdOffset),
            ushort.MinValue,
            ushort.MaxValue);
        BridgeBlockAction action = (BridgeBlockAction)_view.ReadInt32(BridgeProtocol.PlacementActionOffset);
        float blockSize = _view.ReadSingle(BridgeProtocol.PlacementBlockSizeOffset);
        Thread.MemoryBarrier();
        if (_view.ReadInt64(BridgeProtocol.PlacementSequenceOffset) != sequence)
        {
            return false;
        }

        placement = new BridgePlacementSnapshot(
            sequence,
            gridX,
            gridY,
            gridZ,
            worldX,
            worldY,
            worldZ,
            rotationX,
            rotationY,
            rotationZ,
            rotationW,
            normalX,
            normalY,
            normalZ,
            materialId,
            action,
            blockSize);
        return true;
    }

    public void Acknowledge(int processId, int frameCount, ushort selectedBlockId)
    {
        Acknowledge(processId, frameCount, selectedBlockId, selectedHotbarSlot: 0, ReadOnlySpan<ushort>.Empty);
    }

    public void Acknowledge(
        int processId,
        int frameCount,
        ushort selectedBlockId,
        int selectedHotbarSlot,
        ReadOnlySpan<ushort> hotbarIds)
    {
        _view.Write(BridgeProtocol.ConsumerProcessIdOffset, processId);
        _view.Write(BridgeProtocol.ConsumerFrameCountOffset, frameCount);
        for (int index = 0; index < BridgeProtocol.HotbarSlotCount; index++)
        {
            ushort blockId = index < hotbarIds.Length ? hotbarIds[index] : (ushort)0;
            _view.Write(BridgeProtocol.ConsumerHotbarStartOffset + index * sizeof(int), (int)blockId);
        }
        _view.Write(
            BridgeProtocol.ConsumerSelectedHotbarSlotOffset,
            Math.Clamp(selectedHotbarSlot, 0, BridgeProtocol.HotbarSlotCount - 1));
        _view.Write(BridgeProtocol.ConsumerSelectedBlockIdOffset, (int)selectedBlockId);
        Thread.MemoryBarrier();
        _view.Write(BridgeProtocol.ConsumerUtcTicksOffset, DateTime.UtcNow.Ticks);
    }

    public void AcknowledgePlacement(long sequence)
    {
        Thread.MemoryBarrier();
        _view.Write(BridgeProtocol.ConsumerPlacementSequenceOffset, sequence);
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}

public static class BridgeFrameProtocol
{
    public const string MapName = "Local\\RTXSandbox.ManifoldGarden.Frame.v1";
    public const int Magic = 0x4D474631;
    public const int Version = 1;
    public const int HeaderSize = 64;
    public const int MaximumWidth = 3840;
    public const int MaximumHeight = 2160;
    public const int MaximumPixelBytes = MaximumWidth * MaximumHeight * 4;
    public const int Capacity = HeaderSize + MaximumPixelBytes;

    internal const int MagicOffset = 0;
    internal const int VersionOffset = 4;
    internal const int WidthOffset = 8;
    internal const int HeightOffset = 12;
    internal const int PixelLengthOffset = 16;
    internal const int ProducerProcessIdOffset = 20;
    internal const int SequenceOffset = 24;
    internal const int UtcTicksOffset = 32;
    internal const int FrameNumberOffset = 40;
}

public sealed class BridgeFrameWriter : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private long _sequence;
    private long _frameNumber;

    public BridgeFrameWriter()
    {
        _mapping = MemoryMappedFile.CreateOrOpen(
            BridgeFrameProtocol.MapName,
            BridgeFrameProtocol.Capacity,
            MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, BridgeFrameProtocol.Capacity, MemoryMappedFileAccess.ReadWrite);
        _view.Write(BridgeFrameProtocol.MagicOffset, BridgeFrameProtocol.Magic);
        _view.Write(BridgeFrameProtocol.VersionOffset, BridgeFrameProtocol.Version);
    }

    public void Write(int width, int height, byte[] rgbaPixels)
    {
        int length = checked(width * height * 4);
        if (width <= 0 || height <= 0 || width > BridgeFrameProtocol.MaximumWidth ||
            height > BridgeFrameProtocol.MaximumHeight || rgbaPixels.Length < length)
            throw new ArgumentOutOfRangeException(nameof(rgbaPixels), "Bridge frame dimensions or buffer are invalid.");

        long completed = Interlocked.Add(ref _sequence, 2);
        _view.Write(BridgeFrameProtocol.SequenceOffset, completed - 1);
        Thread.MemoryBarrier();
        _view.WriteArray(BridgeFrameProtocol.HeaderSize, rgbaPixels, 0, length);
        _view.Write(BridgeFrameProtocol.WidthOffset, width);
        _view.Write(BridgeFrameProtocol.HeightOffset, height);
        _view.Write(BridgeFrameProtocol.PixelLengthOffset, length);
        _view.Write(BridgeFrameProtocol.ProducerProcessIdOffset, Process.GetCurrentProcess().Id);
        _view.Write(BridgeFrameProtocol.UtcTicksOffset, DateTime.UtcNow.Ticks);
        _view.Write(BridgeFrameProtocol.FrameNumberOffset, ++_frameNumber);
        Thread.MemoryBarrier();
        _view.Write(BridgeFrameProtocol.SequenceOffset, completed);
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}

public sealed class BridgeFrameReader : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;

    private BridgeFrameReader(MemoryMappedFile mapping)
    {
        _mapping = mapping;
        _view = mapping.CreateViewAccessor(0, BridgeFrameProtocol.Capacity, MemoryMappedFileAccess.Read);
    }

    public static bool TryOpen(out BridgeFrameReader? reader)
    {
        try
        {
            reader = new BridgeFrameReader(MemoryMappedFile.OpenExisting(
                BridgeFrameProtocol.MapName,
                MemoryMappedFileRights.Read));
            return true;
        }
        catch (System.IO.FileNotFoundException)
        {
            reader = null;
            return false;
        }
    }

    public bool TryRead(ref byte[] pixels, out int width, out int height, out long frameNumber)
    {
        width = 0;
        height = 0;
        frameNumber = 0;
        if (_view.ReadInt32(BridgeFrameProtocol.MagicOffset) != BridgeFrameProtocol.Magic ||
            _view.ReadInt32(BridgeFrameProtocol.VersionOffset) != BridgeFrameProtocol.Version)
            return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            long start = _view.ReadInt64(BridgeFrameProtocol.SequenceOffset);
            if (start <= 0 || (start & 1) != 0)
            {
                Thread.Yield();
                continue;
            }
            int readWidth = _view.ReadInt32(BridgeFrameProtocol.WidthOffset);
            int readHeight = _view.ReadInt32(BridgeFrameProtocol.HeightOffset);
            int length = _view.ReadInt32(BridgeFrameProtocol.PixelLengthOffset);
            if (readWidth <= 0 || readHeight <= 0 || length != readWidth * readHeight * 4 ||
                length > BridgeFrameProtocol.MaximumPixelBytes)
                return false;
            if (pixels.Length != length) pixels = new byte[length];
            _view.ReadArray(BridgeFrameProtocol.HeaderSize, pixels, 0, length);
            long readFrameNumber = _view.ReadInt64(BridgeFrameProtocol.FrameNumberOffset);
            Thread.MemoryBarrier();
            long end = _view.ReadInt64(BridgeFrameProtocol.SequenceOffset);
            if (start != end || (end & 1) != 0) continue;
            width = readWidth;
            height = readHeight;
            frameNumber = readFrameNumber;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}
#pragma warning restore CA1416
