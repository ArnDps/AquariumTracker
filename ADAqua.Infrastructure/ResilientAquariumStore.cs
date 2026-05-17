using ADAqua.Domain;

namespace ADAqua.Infrastructure;

public sealed class ResilientAquariumStore(IAquariumRepository repository)
{
    public async Task<(bool IsPersisted, string Message)> SaveAsync(Aquarium aquarium, CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.SaveAsync(aquarium, cancellationToken);
            return (true, "Donnees sauvegardees dans MySQL.");
        }
        catch (Exception exception)
        {
            return (false, $"Sauvegarde MySQL indisponible: {exception.Message}");
        }
    }
}
