using RTXSandbox.ManifoldGardenBridge;

int samples = 8;
int intervalMilliseconds = 250;
bool selfTest = false;
bool frameOnly = false;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--samples" && index + 1 < args.Length)
    {
        samples = int.Parse(args[++index]);
    }
    else if (args[index] == "--interval" && index + 1 < args.Length)
    {
        intervalMilliseconds = int.Parse(args[++index]);
    }
    else if (args[index] == "--self-test")
    {
        selfTest = true;
    }
    else if (args[index] == "--frame-only")
    {
        frameOnly = true;
    }
}

if (selfTest)
{
    if (BridgeReader.TryOpen(out BridgeReader? liveReader) && liveReader is not null)
    {
        using (liveReader)
        {
            if (liveReader.TryRead(out BridgeSnapshot liveState) &&
                DateTime.UtcNow - liveState.ProducerUtc < TimeSpan.FromSeconds(2) &&
                liveState.ProducerProcessId > 0)
            {
                try
                {
                    using System.Diagnostics.Process producer =
                        System.Diagnostics.Process.GetProcessById(liveState.ProducerProcessId);
                    if (!producer.HasExited)
                    {
                        Console.Error.WriteLine(
                            $"Protocol self-test refused to overwrite the active Manifold bridge " +
                            $"owned by PID {liveState.ProducerProcessId}.");
                        return 13;
                    }
                }
                catch (ArgumentException)
                {
                    // The producer disappeared between the snapshot and process lookup.
                }
            }
        }
    }

    using var writer = new BridgeWriter();
    if (!BridgeReader.TryOpen(out BridgeReader? testReader) || testReader is null)
    {
        Console.Error.WriteLine("Protocol self-test could not reopen its named mapping.");
        return 4;
    }
    using (testReader)
    {
        ushort[] expectedHotbar = [1, 26, 31, 136, 157, 177, 198, 201, 206];
        testReader.Acknowledge(7654, 9, 177, 5, expectedHotbar);
        if (!writer.TryReadConsumer(out int consumerPid, out int consumerFrame, out _, out ushort selected) ||
            consumerPid != 7654 || consumerFrame != 9 || selected != 177)
        {
            Console.Error.WriteLine("Protocol self-test failed the RTX-to-Manifold selection acknowledgement.");
            return 5;
        }
        ushort[] actualHotbar = new ushort[BridgeProtocol.HotbarSlotCount];
        writer.ReadConsumerHotbar(actualHotbar, out int selectedSlot);
        if (selectedSlot != 5 || !actualHotbar.SequenceEqual(expectedHotbar))
        {
            Console.Error.WriteLine("Protocol self-test failed the RTX hotbar HUD payload.");
            return 8;
        }
        writer.WritePlacement(3, -2, 7, 10.5f, 20.5f, 30.5f, 0f, 0f, 0f, 1f, 0f, 1f, 0f,
            177, BridgeBlockAction.Remove, 1.25f);
        if (!testReader.TryReadPlacement(out BridgePlacementSnapshot placement) ||
            placement.GridX != 3 || placement.GridY != -2 || placement.GridZ != 7 ||
            placement.MaterialId != 177 || Math.Abs(placement.WorldY - 20.5f) > 0.0001f ||
            Math.Abs(placement.NormalY - 1f) > 0.0001f ||
            placement.Action != BridgeBlockAction.Remove || Math.Abs(placement.BlockSize - 1.25f) > 0.0001f)
        {
            Console.Error.WriteLine("Protocol self-test failed the Manifold-to-RTX placement payload.");
            return 6;
        }
        testReader.AcknowledgePlacement(placement.Sequence);
        if (writer.ReadAcknowledgedPlacementSequence() != placement.Sequence)
        {
            Console.Error.WriteLine("Protocol self-test failed the committed-placement acknowledgement.");
            return 7;
        }
    }
    using (var frameWriter = new BridgeFrameWriter())
    {
        if (!BridgeFrameReader.TryOpen(out BridgeFrameReader? frameReader) || frameReader is null)
        {
            Console.Error.WriteLine("Protocol self-test could not reopen the framebuffer mapping.");
            return 9;
        }
        using (frameReader)
        {
            byte[] expectedPixels =
            [
                255, 0, 0, 255, 0, 255, 0, 128,
                0, 0, 255, 64, 0, 0, 0, 0,
            ];
            frameWriter.Write(2, 2, expectedPixels);
            byte[] actualPixels = Array.Empty<byte>();
            if (!frameReader.TryRead(ref actualPixels, out int width, out int height, out long frameNumber) ||
                width != 2 || height != 2 || frameNumber <= 0 || !actualPixels.SequenceEqual(expectedPixels))
            {
                Console.Error.WriteLine("Protocol self-test failed the real framebuffer round trip.");
                return 10;
            }
        }
    }
    Console.WriteLine("Bridge protocol self-test passed: hotbar, block actions, commit acknowledgement and RGBA framebuffer round-tripped.");
    return 0;
}

if (frameOnly)
{
    BridgeFrameReader? frameReader = null;
    DateTime frameDeadline = DateTime.UtcNow.AddSeconds(10);
    while (frameReader is null && DateTime.UtcNow < frameDeadline)
    {
        BridgeFrameReader.TryOpen(out frameReader);
        if (frameReader is null) Thread.Sleep(100);
    }
    if (frameReader is null)
    {
        Console.Error.WriteLine($"Framebuffer bridge not found: {BridgeFrameProtocol.MapName}");
        return 11;
    }
    using (frameReader)
    {
        byte[] pixels = Array.Empty<byte>();
        long previousFrame = -1;
        int advancingFrames = 0;
        for (int sample = 0; sample < samples; sample++)
        {
            if (frameReader.TryRead(ref pixels, out int width, out int height, out long frameNumber))
            {
                int transparentPixels = 0;
                int visiblePixels = 0;
                for (int alpha = 3; alpha < pixels.Length; alpha += 4)
                {
                    if (pixels[alpha] == 0) transparentPixels++;
                    else visiblePixels++;
                }
                if (previousFrame >= 0 && frameNumber > previousFrame) advancingFrames++;
                previousFrame = frameNumber;
                Console.WriteLine($"frame={frameNumber} size={width}x{height} rgba={pixels.Length} " +
                                  $"transparent={transparentPixels} visible={visiblePixels}");
            }
            Thread.Sleep(intervalMilliseconds);
        }
        if (samples > 1 && advancingFrames == 0)
        {
            Console.Error.WriteLine("Framebuffer mapping exists, but its frame number did not advance.");
            return 12;
        }
    }
    return 0;
}

BridgeReader? reader = null;
DateTime deadline = DateTime.UtcNow.AddSeconds(10);
while (reader is null && DateTime.UtcNow < deadline)
{
    BridgeReader.TryOpen(out reader);
    if (reader is null)
    {
        Thread.Sleep(100);
    }
}

if (reader is null)
{
    Console.Error.WriteLine($"Bridge not found: {BridgeProtocol.MapName}");
    return 2;
}

using (reader)
{
    long previousSequence = -1;
    int changingSnapshots = 0;
    for (int sample = 0; sample < samples; sample++)
    {
        reader.Acknowledge(Environment.ProcessId, sample, selectedBlockId: 1);
        if (reader.TryRead(out BridgeSnapshot state))
        {
            bool advanced = previousSequence >= 0 && state.Sequence > previousSequence;
            if (advanced)
            {
                changingSnapshots++;
            }

            previousSequence = state.Sequence;
            double ageMilliseconds = (DateTime.UtcNow - state.ProducerUtc).TotalMilliseconds;
            Console.WriteLine(
                $"pid={state.ProducerProcessId} frame={state.FrameCount} seq={state.Sequence} " +
                $"age={ageMilliseconds:F0}ms camera={state.HasCamera} " +
                $"pos=({state.PositionX:F3},{state.PositionY:F3},{state.PositionZ:F3}) " +
                $"rot=({state.RotationX:F3},{state.RotationY:F3},{state.RotationZ:F3},{state.RotationW:F3}) " +
                $"fov={state.FieldOfView:F1} screen={state.ScreenWidth}x{state.ScreenHeight}");
        }
        else
        {
            Console.WriteLine("Snapshot was being updated; retrying.");
        }

        if (reader.TryReadPlacement(out BridgePlacementSnapshot placement))
        {
            Console.WriteLine(
                $"placement seq={placement.Sequence} material={placement.MaterialId} " +
                $"grid=({placement.GridX},{placement.GridY},{placement.GridZ}) " +
                $"world=({placement.WorldX:F3},{placement.WorldY:F3},{placement.WorldZ:F3}) " +
                $"normal=({placement.NormalX:F3},{placement.NormalY:F3},{placement.NormalZ:F3})");
        }

        Thread.Sleep(intervalMilliseconds);
    }

    if (samples > 1 && changingSnapshots == 0)
    {
        Console.Error.WriteLine("Bridge mapping exists, but its sequence did not advance.");
        return 3;
    }
}

return 0;
