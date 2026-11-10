using System.Diagnostics;
using RTXSandbox.ManifoldGardenBridge;

namespace RTXSandbox.Infrastructure;

public sealed class ManifoldGardenBridgeMonitor : IDisposable
{
    // Camera pose is presentation state, not background telemetry. Throttling this to
    // 10 Hz made a correctly anchored RTX block visibly follow the view while Manifold
    // rendered its own world at 60 Hz. Shared-memory reads are allocation-free, so sample
    // once per RTX update and keep both cameras within one producer/consumer frame.
    private static readonly TimeSpan PollInterval = TimeSpan.Zero;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleInterval = TimeSpan.FromSeconds(2);

    private BridgeReader? _reader;
    private DateTime _nextPollUtc;
    private DateTime _nextOpenAttemptUtc;
    private int _acknowledgementFrame;
    private int _connectedProducerProcessId;
    private long _lastPlacementSequence;
    private BridgePlacementSnapshot? _pendingPlacement;

    public bool Connected { get; private set; }
    public BridgeSnapshot Snapshot { get; private set; }
    public double AgeMilliseconds { get; private set; }

    public void Update(ushort selectedBlockId, int selectedHotbarSlot, ReadOnlySpan<ushort> hotbarIds)
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextPollUtc)
        {
            return;
        }
        _nextPollUtc = now + PollInterval;

        if (_reader is null)
        {
            if (now < _nextOpenAttemptUtc)
            {
                return;
            }
            _nextOpenAttemptUtc = now + RetryInterval;
            try
            {
                BridgeReader.TryOpen(out _reader);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Manifold Garden bridge open failed: {exception.Message}");
            }
        }

        if (_reader is null)
        {
            SetDisconnected();
            return;
        }

        try
        {
            _reader.Acknowledge(
                Environment.ProcessId,
                ++_acknowledgementFrame,
                selectedBlockId,
                selectedHotbarSlot,
                hotbarIds);
            if (!_reader.TryRead(out BridgeSnapshot snapshot))
            {
                return;
            }

            AgeMilliseconds = Math.Max(0, (now - snapshot.ProducerUtc).TotalMilliseconds);
            if (AgeMilliseconds > StaleInterval.TotalMilliseconds)
            {
                CloseReader();
                SetDisconnected();
                return;
            }

            Snapshot = snapshot;
            if (_reader.TryReadPlacement(out BridgePlacementSnapshot placement) &&
                placement.Sequence > _lastPlacementSequence)
            {
                _lastPlacementSequence = placement.Sequence;
                _pendingPlacement = placement;
            }
            Connected = true;
            if (_connectedProducerProcessId != snapshot.ProducerProcessId)
            {
                _connectedProducerProcessId = snapshot.ProducerProcessId;
                Console.WriteLine(
                    $"Manifold Garden bridge connected: pid={snapshot.ProducerProcessId}, " +
                    $"frame={snapshot.FrameCount}, camera={snapshot.HasCamera}");
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Manifold Garden bridge read failed: {exception.Message}");
            CloseReader();
            SetDisconnected();
        }
    }

    public bool TryDequeuePlacement(out BridgePlacementSnapshot placement)
    {
        if (_pendingPlacement is BridgePlacementSnapshot value)
        {
            placement = value;
            _pendingPlacement = null;
            return true;
        }

        placement = default;
        return false;
    }

    public void AcknowledgePlacement(long sequence)
    {
        try
        {
            _reader?.AcknowledgePlacement(sequence);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Manifold Garden bridge placement acknowledgement failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        CloseReader();
        SetDisconnected();
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    private void SetDisconnected()
    {
        if (_connectedProducerProcessId != 0)
        {
            Console.WriteLine($"Manifold Garden bridge disconnected: pid={_connectedProducerProcessId}");
        }
        _connectedProducerProcessId = 0;
        Connected = false;
        Snapshot = default;
        AgeMilliseconds = 0;
    }
}
