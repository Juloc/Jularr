using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Workers
{
    // Hosts opt in to background work; workers only run versioned System services.
    public static class WorkerRegistration
    {
        public static IServiceCollection AddJularrWorkers(this IServiceCollection services)
        {
            services.AddHostedService<OperationWorker>();
            services.AddHostedService<AcquisitionWorker>();
            services.AddHostedService<BrowserModuleWorker>();
            services.AddHostedService<LiveWorker>();
            services.AddHostedService<DownloadWorker>();
            services.AddHostedService<ProviderSyncWorker>();
            services.AddHostedService<UsenetWorker>();
            services.AddHostedService<MediaWorker>();
            services.AddHostedService<StorageWorker>();
            services.AddHostedService<BackupWorker>();
            services.AddHostedService<MaintenanceWorker>();
            services.AddHostedService<TrickplayWorker>();
            services.AddHostedService<SubtitleWorker>();
            services.AddHostedService<RecognitionWorker>();
            services.AddHostedService<DerivationWorker>();
            services.AddHostedService<TranslationWorker>();
            services.AddHostedService<CatalogWorker>();
            services.AddHostedService<HealthWorker>();
            return services;
        }
    }
}
