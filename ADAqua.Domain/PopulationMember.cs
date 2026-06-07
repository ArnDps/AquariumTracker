namespace ADAqua.Domain;

public sealed class PopulationMember
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime AddedOn { get; set; } = DateTime.Today;
    public string SpeciesName { get; set; } = string.Empty;
    public string CommonName { get; set; } = string.Empty;
    public PopulationType Type { get; set; } = PopulationType.Fish;
    public int Quantity { get; set; } = 1;
    public string Notes { get; set; } = string.Empty;
}

public enum PopulationType
{
    Fish,
    Shrimp,
    Snail,
    Other
}
