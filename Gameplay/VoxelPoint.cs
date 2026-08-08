namespace RTXSandbox.Gameplay;

public readonly record struct VoxelPoint(int X, int Y, int Z)
{
    public override string ToString() => $"{X}, {Y}, {Z}";
}
