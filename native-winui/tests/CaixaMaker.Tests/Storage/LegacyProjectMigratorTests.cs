using System.Text.Json;
using CaixaMaker.Core.Storage;
using CaixaMaker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Storage;

public sealed class LegacyProjectMigratorTests
{
    [Fact]
    public void MigrateLegacyProjects_IsAtomicIdempotentAndPreservesOriginals()
    {
        using TempDirectoryScope temp = new();
        string legacyDir = temp.GetPath("legacy_projects");
        string targetDir = temp.GetPath("appdata_projects");
        string markerFile = temp.GetPath("config", "legacy_migration.json");
        Directory.CreateDirectory(legacyDir);

        string validPayload = """
            {
              "schema_version": 1,
              "id": "proj_legacy01",
              "name": "Caixa IFMaker",
              "model_id": "box_with_lid_and_handles",
              "box_parameters": {
                "box_thickness": 3.0,
                "box_width": 200.0,
                "box_depth": 150.0,
                "box_height": 100.0,
                "folga": 0.0
              }
            }
            """;
        string legacyValidPath = Path.Combine(legacyDir, "proj_legacy01.json");
        File.WriteAllText(legacyValidPath, validPayload);

        string legacyCorruptPath = Path.Combine(legacyDir, "corrupt.json");
        File.WriteAllText(legacyCorruptPath, "{not-json");

        string legacyIncompletePath = Path.Combine(legacyDir, "incomplete.json");
        File.WriteAllText(legacyIncompletePath, """{"id":"proj_bad","name":""}""");

        MigrationReport report1 = LegacyProjectMigrator.MigrateLegacyProjects(
            targetDir,
            [legacyDir],
            markerFile);

        report1.Performed.Should().BeTrue();
        report1.MigratedCount.Should().Be(1);
        report1.SkippedExistingCount.Should().Be(0);
        report1.MigratedIds.Should().Equal("proj_legacy01");
        report1.Errors.Should().HaveCount(2);
        report1.SourcesChecked.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(legacyDir));

        string migratedDest = Path.Combine(targetDir, "proj_legacy01.json");
        File.Exists(migratedDest).Should().BeTrue();
        File.Exists($"{migratedDest}.migrating").Should().BeFalse();
        Directory.GetFiles(targetDir, "*.migrating").Should().BeEmpty();

        // Originais permanecem intactos na pasta legada
        File.Exists(legacyValidPath).Should().BeTrue();
        File.ReadAllText(legacyValidPath).Should().Be(validPayload);
        File.Exists(legacyCorruptPath).Should().BeTrue();

        // Manifesto legacy_migration.json criado com chaves compatíveis com o backend Python
        File.Exists(markerFile).Should().BeTrue();
        using JsonDocument markerDoc = JsonDocument.Parse(File.ReadAllText(markerFile));
        markerDoc.RootElement.GetProperty("performed").GetBoolean().Should().BeTrue();
        markerDoc.RootElement.GetProperty("migrated_count").GetInt32().Should().Be(1);
        markerDoc.RootElement.GetProperty("skipped_existing_count").GetInt32().Should().Be(0);
        markerDoc.RootElement.GetProperty("migrated_ids")[0].GetString().Should().Be("proj_legacy01");

        // Modifica destino para comprovar que a segunda execução não sobrescreve nem duplica
        const string editedDestContent = """{"id":"proj_legacy01","name":"Editado no AppData","model_id":"open_box","box_parameters":{}}""";
        File.WriteAllText(migratedDest, editedDestContent);

        MigrationReport report2 = LegacyProjectMigrator.MigrateLegacyProjects(
            targetDir,
            [legacyDir],
            markerFile);

        report2.Performed.Should().BeTrue();
        report2.MigratedCount.Should().Be(0);
        report2.SkippedExistingCount.Should().Be(1);
        File.ReadAllText(migratedDest).Should().Be(editedDestContent);
    }
}
