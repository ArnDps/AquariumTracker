using System.Collections.ObjectModel;

namespace ADAqua.Domain;

public sealed class Aquarium
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public decimal VolumeLiters { get; set; }
    public string ContainerType { get; set; } = "Aquarium";
    public string WaterType { get; set; } = "FreshwaterTropical";
    public DateOnly StartedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Notes { get; set; } = string.Empty;
    public ObservableCollection<WaterParameters> Measurements { get; } = [];
    public ObservableCollection<AquariumPlant> Plants { get; } = [];
    public ObservableCollection<PopulationMember> Population { get; } = [];
    public ObservableCollection<AquariumIntervention> Interventions { get; } = [];
}
