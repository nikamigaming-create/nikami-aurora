namespace OpenDAO.Rendering;

public static class WorldRenderLayers
{
    public const uint Gameplay = 1u << 0;
    public const uint Minimap = 1u << 1;
    public const uint GameplayAndMinimap = Gameplay | Minimap;
}
