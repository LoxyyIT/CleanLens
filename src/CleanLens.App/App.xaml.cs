using System.Windows;
using System.IO;
using CleanLens.Core.Safety;
using CleanLens.Core.Services;
using CleanLens.Data;
using CleanLens.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace CleanLens.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localDataPath = Path.Combine(appData, "CleanLens");
#if DEBUG
        var debugDataPath = Environment.GetEnvironmentVariable("CLEANLENS_DEBUG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(debugDataPath))
        {
            localDataPath = Path.GetFullPath(debugDataPath);
        }
#endif
        Directory.CreateDirectory(localDataPath);
        var allowedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            appData,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path));
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        }.Where(path => !string.IsNullOrWhiteSpace(path));

        var services = new ServiceCollection();
        services.AddSingleton<IApplicationInventory, RegistryApplicationInventory>();
        services.AddSingleton<LeftoverScanner>();
        services.AddSingleton(new CleanLensDatabase(Path.Combine(localDataPath, "cleanlens.db")));
        services.AddSingleton(new DeletionPathPolicy(allowedRoots, protectedRoots));
        services.AddSingleton(provider => new QuarantineService(
            Path.Combine(localDataPath, "Quarantine"),
            provider.GetRequiredService<CleanLensDatabase>(),
            provider.GetRequiredService<DeletionPathPolicy>()));
        services.AddSingleton(provider => new MainViewModel(
            provider.GetRequiredService<IApplicationInventory>(),
            provider.GetRequiredService<LeftoverScanner>(),
            provider.GetRequiredService<CleanLensDatabase>(),
            provider.GetRequiredService<QuarantineService>(),
            localDataPath));
        Services = services.BuildServiceProvider();

        base.OnStartup(e);
        var window = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }
}
