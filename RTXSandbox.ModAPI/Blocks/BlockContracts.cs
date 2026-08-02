namespace RTXSandbox.ModAPI;

public interface IBlockRegistry
{
    IReadOnlyList<BlockDefinition> Definitions { get; }
    ushort Register(BlockDefinition definition, bool force = false);
    bool Remove(string key);
    void NotifyChanged();
    BlockDefinition Get(ushort id);
    bool TryGet(string key, out BlockDefinition? definition);
}
