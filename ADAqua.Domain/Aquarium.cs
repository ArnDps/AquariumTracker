namespace ADAqua.Domain;

public sealed class Aquarium
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public decimal VolumeLiters { get; set; }
    public string WaterType { get; set; } = "Eau douce";
    public DateOnly StartedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Notes { get; set; } = string.Empty;
    public List<WaterParameters> Measurements { get; } = [];
    public List<AquariumPlant> Plants { get; } = [];
    public List<PopulationMember> Population { get; } = [];
}
