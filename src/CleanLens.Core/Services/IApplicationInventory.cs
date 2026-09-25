using CleanLens.Core.Models;

namespace CleanLens.Core.Services;

public interface IApplicationInventory
{
    string? LastScanWarning => null;
    Task<IReadOnlyList<InstalledApplication>> ScanAsync(CancellationToken cancellationToken = default);
}
