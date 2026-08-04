namespace RTXSandbox.Gameplay;

public static class PlacementRules
{
    public const float MinimumEyeDistance = 1.1f;

    public static bool IsFarEnough(float rayHitDistance) =>
        float.IsFinite(rayHitDistance) && rayHitDistance + 0.00001f >= MinimumEyeDistance;
}
