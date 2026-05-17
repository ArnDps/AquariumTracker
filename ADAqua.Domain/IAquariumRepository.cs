namespace ADAqua.Domain;

public interface IAquariumRepository
{
    Task<IReadOnlyList<Aquarium>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(Aquarium aquarium, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid aquariumId, CancellationToken cancellationToken = default);
}
