using CleanLens.Core.Models;

namespace CleanLens.Core.Services;

public interface IApplicationInventory
{
    Task<IReadOnlyList<InstalledApplication>> ScanAsync(CancellationToken cancellationToken = default);
}
