namespace ADAqua.Domain;

public sealed class AquariumIntervention
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; set; } = DateTime.Now;
    public DateTime OccurredOnDate
    {
        get => OccurredAt.Date;
        set => OccurredAt = value.Date.Add(OccurredAt.TimeOfDay);
    }

    public InterventionType Type { get; set; } = InterventionType.WaterChange;
    public string ProductName { get; set; } = string.Empty;
    public string ProductQuantity { get; set; } = string.Empty;
    public decimal? WaterVolumeLiters { get; set; }
    public decimal? WaterPercentage { get; set; }
    public string PopulationChangeReason { get; set; } = string.Empty;
    public int? PopulationChangeCount { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public enum InterventionType
{
    WaterChange,
    Fertilization,
    FilterCleaning,
    PopulationAdded,
    PopulationRemoved,
    MedicalTreatment,
    Other
}
