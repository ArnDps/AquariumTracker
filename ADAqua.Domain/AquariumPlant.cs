namespace ADAqua.Domain;

public sealed class AquariumPlant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CommonName { get; set; } = string.Empty;
    public string ScientificName { get; set; } = string.Empty;
    public PlantGrowthSpeed GrowthSpeed { get; set; } = PlantGrowthSpeed.Medium;
    public string LightNeed { get; set; } = "Moyen";
    public string Notes { get; set; } = string.Empty;
}

public enum PlantGrowthSpeed
{
    Slow,
    Medium,
    Fast
}
