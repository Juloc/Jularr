using Jularr.Web.Features.Acquisition.Backup;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;

namespace Jularr.Tests;

/// <summary>P1 item 8: backup/restore of acquisition settings, with a dry-run preview and secret handling.</summary>
[TestClass]
public sealed class AcquisitionBackupTests
{
    [TestMethod]
    public async Task ExportThenRestoreRoundTripsCanonicalStores()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Copy });

        var bundle = await environment.ExportBackupAsync();
        Assert.IsTrue(bundle.Files.ContainsKey("monitoring.json"));
        Assert.IsTrue(bundle.Files.ContainsKey("import-settings.json"));
        Assert.IsTrue(bundle.Files.ContainsKey("indexers.json"));
        Assert.IsTrue(bundle.Files.ContainsKey("download-clients.json"));
        Assert.IsFalse(bundle.Files.ContainsKey("health.json"), "Runtime health state is not a setting and is never backed up.");

        // Change settings after the export.
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Move });

        var restored = await environment.RestoreBackupAsync(bundle);

        Assert.IsTrue(restored.Success, string.Join(" ", restored.Errors));
        Assert.AreEqual(bundle.Files.Count, restored.FilesWritten);
        Assert.AreEqual(ImportMode.Copy, (await environment.ImportSettings.LoadAsync()).DefaultImportMode);
    }

    [TestMethod]
    public async Task PreviewNeverWritesAnything()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Copy });
        var bundle = await environment.ExportBackupAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Move });

        var preview = await environment.PreviewRestoreAsync(bundle);

        Assert.IsTrue(preview.CanRestore);
        var importFile = preview.Files.Single(file => file.FileName == "import-settings.json");
        Assert.IsTrue(importFile.WouldChange, "The preview reports what would change...");
        Assert.AreEqual(ImportMode.Move, (await environment.ImportSettings.LoadAsync()).DefaultImportMode, "...without applying it.");
    }

    [TestMethod]
    public async Task AnUnsupportedBundleVersionIsRejectedWithoutWritingAnything()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Copy });

        var badBundle = new AcquisitionBackupBundle(99, DateTimeOffset.UtcNow, new Dictionary<string, string>
        {
            ["import-settings.json"] = """{"Version":1,"DefaultImportMode":"Move","RootImportModes":{},"RemotePathMappings":[]}"""
        });

        var result = await environment.RestoreBackupAsync(badBundle);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, result.FilesWritten);
        Assert.AreEqual(ImportMode.Copy, (await environment.ImportSettings.LoadAsync()).DefaultImportMode, "Nothing was written.");
    }

    [TestMethod]
    public async Task InvalidJsonInTheBundleIsRejectedWithoutWritingAnything()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await environment.ImportSettings.UpdateAsync(state => state with { DefaultImportMode = ImportMode.Copy });

        var badBundle = new AcquisitionBackupBundle(AcquisitionBackupService.CurrentVersion, DateTimeOffset.UtcNow, new Dictionary<string, string>
        {
            ["import-settings.json"] = "not json"
        });

        var preview = await environment.PreviewRestoreAsync(badBundle);
        Assert.IsFalse(preview.CanRestore);

        var result = await environment.RestoreBackupAsync(badBundle);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(ImportMode.Copy, (await environment.ImportSettings.LoadAsync()).DefaultImportMode);
    }

    [TestMethod]
    public async Task IndexerAndDownloadClientBackupsAreFlaggedAsCarryingEncryptedSecrets()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        var bundle = await environment.ExportBackupAsync();
        var preview = await environment.PreviewRestoreAsync(bundle);

        Assert.IsTrue(preview.Files.Single(f => f.FileName == "indexers.json").ContainsEncryptedSecret);
        Assert.IsTrue(preview.Files.Single(f => f.FileName == "download-clients.json").ContainsEncryptedSecret);
        Assert.IsFalse(preview.Files.Single(f => f.FileName == "monitoring.json").ContainsEncryptedSecret);
        // The secret itself is never in plain text in the bundle.
        StringAssert.Contains(bundle.Files["indexers.json"], "protectedApiKey");
        StringAssert.DoesNotMatch(bundle.Files["indexers.json"], new System.Text.RegularExpressions.Regex("prowlarr-key"));
    }

    [TestMethod]
    public async Task AMonitoringFileThatStillCarriesTagsLoadsAndIsSavedWithoutThem()
    {
        var root = Directory.CreateTempSubdirectory("jularr-tags-gone-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "acquisition"));
            var path = Path.Combine(directory.FullName, "monitoring.json");
            await File.WriteAllTextAsync(path, """{"version":1,"anime":{"frieren":{"animeKey":"frieren","monitored":true,"searchOnAdd":true,"seasonOverrides":{},"episodeOverrides":{},"tagIds":["slow"]}},"wanted":{},"attempts":{},"history":[]}""");
            var store = new MonitoringStore(root.FullName);

            var state = await store.LoadAsync();
            await store.SaveAsync(state);

            Assert.IsTrue(state.Anime["frieren"].SearchOnAdd);
            Assert.DoesNotContain("tagIds", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
