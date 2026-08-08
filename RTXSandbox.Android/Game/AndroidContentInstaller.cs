using Android.Content;

namespace RTXSandbox.Android.Game;

internal static class AndroidContentInstaller
{
    public static (string BaseContent, string ContentPacks) Install(Context context)
    {
        string root = Path.Combine(context.FilesDir?.AbsolutePath ?? AppContext.BaseDirectory, "GameContent");
        string baseRoot = Path.Combine(root, "Base");
        CopyAssetTree(context, "Content/Base", baseRoot);
        string packs = Path.Combine(root, "ContentPacks");
        Directory.CreateDirectory(packs);
        return (Path.Combine(baseRoot, "base.blocks.json"), packs);
    }

    private static void CopyAssetTree(Context context, string assetPath, string destination)
    {
        string[] children = context.Assets?.List(assetPath) ?? Array.Empty<string>();
        if (children.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = context.Assets!.Open(assetPath);
            using FileStream target = File.Create(destination);
            source.CopyTo(target);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (string child in children)
            CopyAssetTree(context, $"{assetPath}/{child}", Path.Combine(destination, child));
    }
}
