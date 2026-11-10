import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const run = promisify(execFile);
const repository = fileURLToPath(new URL('../../', import.meta.url));

// Exercise the production model and preset methods without starting Relay, GPU workers,
// fixture generation or timers. Only the owning service's logging/locking shell is stubbed.
test('modern Warp settings, migration, invalidation, presets and wildcard input checks', { timeout: 120000 }, async t => {
    const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'relay-warp-settings-'));
    t.after(() => fs.rm(temp, { recursive: true, force: true }));
    const source = await fs.readFile(path.join(repository, 'ComponentDev/Services/WarpSessionPreview.cs'), 'utf8');
    const primary = source.match(/public sealed partial record PreviewSettings\([\s\S]*?\);/)?.[0];
    const stages = source.match(/\[Flags\]\s*public enum PreviewStage\s*\{[^}]+\}/)?.[0];
    assert.ok(primary && stages, 'production settings constructor and stages must be found');
    await fs.writeFile(path.join(temp, 'Models.cs'), `namespace ComponentDev.Services;\n${stages}\n${primary}\n`);
    const linked = ['PreviewSettingsDetails.cs', 'PreviewPickingSettings.cs', 'WarpPreviewPresets.cs'].map(name => `<Compile Include="${path.join(repository, 'ComponentDev/Services', name).replaceAll('&', '&amp;')}" Link="${name}" />`).join('');
    await fs.writeFile(path.join(temp, 'SettingsCheck.csproj'), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />${linked}</ItemGroup></Project>`);
    await fs.writeFile(path.join(temp, 'Host.cs'), `global using Microsoft.Extensions.Logging;
namespace ComponentDev.Services;
public sealed partial class WarpSessionPreview {
    private readonly object _sync = new();
    private readonly ILogger<WarpSessionPreview> _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<WarpSessionPreview>.Instance;
    public string Root { get; }
    public PreviewSettings Settings { get; } = NormalizeSettings(new());
    public WarpSessionPreview(string root) { Root = root; Directory.CreateDirectory(root); }
}`);
    await fs.writeFile(path.join(temp, 'Program.cs'), String.raw`
using ComponentDev.Services;
using System.Text.Json;
using static ComponentDev.Services.WarpSessionPreview;
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var fresh = NormalizeSettings(new());
Check(fresh.SettingsSchemaVersion == CurrentSettingsSchemaVersion, "fresh settings are versioned");
Check(fresh.InputPattern == "*.eer" && fresh.TotalDose == 40 && fresh.TargetPixelSize == fresh.Pixel, "modern defaults");
Check(fresh.MotionGridAuto && fresh.CtfGridAuto && fresh.MotionGridArgument == null && fresh.CtfGridArgument == null, "new grids use worker auto");
Check(fresh.WorkerDosePerAngstromFrame == -40, "total dose uses Warp's negative internal convention");
Check(ValidateSettings(fresh) == null, "normalized defaults validate");
var retained = NormalizeSettings(fresh with { MotionGrid = 7, MotionGridY = 6, MotionGridZ = 31, CtfGridX = 8, CtfGridY = 7 });
Check(retained.MotionGrid == 7 && retained.MotionGridY == 6 && retained.MotionGridZ == 31 && retained.CtfGridX == 8 && retained.CtfGridY == 7, "auto retains manual grid values");
Check((retained with { MotionGridAuto = false }).MotionGridArgument == "7x6x31" && (retained with { CtfGridAuto = false }).CtfGridArgument == "8x7", "manual grid arguments restore stored values");
var legacy = NormalizeSettings(new PreviewSettings(Pixel: 1.06m, Dose: 1.7m, Acquisition: "TIFF", MotionGrid: 7) { BinTimes = 1.5m, MotionGridY = 6, MotionGridZ = 31 }, legacy: true);
Check(legacy.InputPattern == "*.tif*", "legacy TIFF pattern preserves both TIFF extensions");
Check(Math.Abs((double)legacy.TargetPixelSize - 1.06 * Math.Pow(2, 1.5)) < 1e-10, "legacy bin exponent preserves physical target");
Check(legacy.TotalDose == 40 && legacy.Dose == 1.7m, "legacy per-frame dose is preserved but not pretended to be a total");
Check(!legacy.MotionGridAuto && legacy.MotionGridArgument == "7x6x31" && !legacy.CtfGridAuto, "legacy explicit grids stay manual");
var legacyAuto = NormalizeSettings(new PreviewSettings(MotionGrid: 0, Acquisition: "DAT") { CtfGridX = 0 }, legacy: true);
Check(legacyAuto.MotionGridAuto && legacyAuto.CtfGridAuto && legacyAuto.MotionGrid >= 1 && legacyAuto.CtfGridX >= 1, "legacy zero sentinel becomes auto with usable manual values");
Check(legacyAuto.InputPattern == "*.dat", "legacy filename pattern retained");
var roundtrip = NormalizeSettings(JsonSerializer.Deserialize<PreviewSettings>(JsonSerializer.Serialize(retained with { TotalDose = 52, TargetPixelSize = 2.1m, InputPattern = "FoilHole*_Data_*.eer" })));
Check(roundtrip.TotalDose == 52 && roundtrip.TargetPixelSize == 2.1m && roundtrip.InputPattern == "FoilHole*_Data_*.eer" && roundtrip.MotionGridAuto, "modern settings roundtrip");
Check(NormalizeSettings(roundtrip) == roundtrip, "normalization is idempotent");
Check(ValidateSettings(fresh with { OutputPath = "" }) == null, "legacy output setting is not required");
Check(ValidateSettings(fresh with { InputPattern = "../*.eer" }) != null, "pattern cannot escape input folder");
Check(ValidateSettings(fresh with { InputPattern = "" }) != null, "pattern is required");
Check(ValidateSettings(fresh with { TotalDose = 0 }) != null, "total dose must be positive");
Check(ValidateSettings(fresh with { TargetPixelSize = .1m }) != null, "explicit target cannot upsample raw pixels");
Check(NormalizeSettings(fresh with { TargetPixelSize = .1m }).TargetPixelSize == .1m, "normalization does not silently correct an invalid explicit target");
Check(ValidateSettings(fresh with { MotionGrid = 0, CtfGridX = 0 }) == null, "auto ignores inactive manual grid values");
Check(ValidateSettings(fresh with { MotionGridAuto = false, MotionGrid = 0 }) != null, "manual motion grid requires positive dimensions");
Check(ValidateSettings(fresh with { CtfGridAuto = false, CtfGridX = 0 }) != null, "manual CTF grid requires positive dimensions");
Check(ValidateSettings(fresh with { MinimumFileSizeMb = -1 }) != null, "negative minimum file size rejected");
Check(DetermineRedo(fresh, fresh with { MinimumFileSizeMb = 100 }) == PreviewStage.None, "minimum file size changes discovery without invalidating processing");
Check(NormalizeSettings(fresh with { TrainCount = 150, TrainAfter = 20 }).TrainAfter == 150, "training trigger follows sample count");
Check(ValidateSettings(fresh with { TrainCount = 150, TrainAfter = 20 }) == null, "legacy trigger cannot reject a larger sample");
Check(DetermineRedo(fresh, fresh with { FitPhase = true }) == (PreviewStage.Ctf | PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract), "phase plate enables phase fitting and retains motion");
Check(DetermineRedo(fresh, fresh with { TotalDose = 50 }) == PreviewStage.All, "total exposure affects motion auto, CTF auto and downstream products");
Check(DetermineRedo(fresh, fresh with { TargetPixelSize = 2 }) == PreviewStage.All, "target pixel affects all stages");
Check(DetermineRedo(fresh, fresh with { MotionGridAuto = false }) == PreviewStage.All, "motion grid mode invalidates all downstream stages");
Check(DetermineRedo(fresh, fresh with { CtfGridAuto = false }) == (PreviewStage.Ctf | PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract), "CTF grid mode retains motion");
Check(DetermineRedo(fresh, fresh with { MotionGrid = 9, CtfGridX = 9 }) == PreviewStage.None, "inactive manual grids do not invalidate auto results");
Check(DetermineRedo(fresh, fresh with { InputPattern = "FoilHole*.eer", Recursive = true, OutputPath = "/ignored" }) == PreviewStage.None, "discovery and legacy output changes keep existing processing");
Check(DetermineRedo(fresh, fresh with { Diameter = 200 }) == (PreviewStage.Pick | PreviewStage.Extract), "picking changes retain earlier processing");
var sandbox = Path.Combine(Path.GetTempPath(), "warp-settings-data-" + Guid.NewGuid().ToString("N"));
try {
    var input = Path.Combine(sandbox, "input"); var root = Path.Combine(sandbox, "job");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "unrelated.eer"), "raw");
    await File.WriteAllTextAsync(Path.Combine(input, "FoilHole42_Data_007.eer"), "raw");
    Directory.CreateDirectory(Path.Combine(input, "nested"));
    await File.WriteAllTextAsync(Path.Combine(input, "nested", "nested-only.eer"), "raw");
    var service = new WarpSessionPreview(root);
    var current = fresh with { InputPath = input, OutputPath = "/intentionally-not-a-user-output", TotalDose = 51, CorrectGain = false, InputPattern = "FoilHole*_Data_*.eer" };
    var inputChecks = await service.CheckInputAsync(current);
    Check(inputChecks.Any(c => c.Item == "Example movie" && c.Passed == true && c.Detail.Contains("FoilHole42_Data_007.eer")), "input check uses precise wildcard");
    var tooSmall = await service.CheckInputAsync(current with { MinimumFileSizeMb = .000004m });
    Check(tooSmall.Any(c => c.Item == "Example movie" && c.Passed == null), "input check waits for files below minimum size");
    var exactSize = await service.CheckInputAsync(current with { MinimumFileSizeMb = .000003m });
    Check(exactSize.Any(c => c.Item == "Example movie" && c.Passed == true), "minimum size includes exact byte boundary");
    Check(inputChecks.Any(c => c.Item == "Job folder" && c.Passed == true), "output check uses managed Root, not legacy OutputPath");
    Check(!Directory.EnumerateFiles(root, ".warp-access-*").Any(), "write-access probe is removed");
    var topOnly = await service.CheckInputAsync(current with { InputPattern = "nested-only.eer" });
    Check(topOnly.Any(c => c.Item == "Example movie" && c.Passed == null), "nonrecursive search excludes nested files");
    var recursive = await service.CheckInputAsync(current with { InputPattern = "nested-only.eer", Recursive = true });
    Check(recursive.Any(c => c.Item == "Example movie" && c.Passed == true), "recursive wildcard search finds nested files");
    var preset = service.LoadCalibrationPreset("Krios · K3", current);
    Check(preset.InputPattern == "*.tif" && preset.Pixel == 1.06m && preset.TargetPixelSize == 1.06m, "preset raises too-small target to new raw pixel");
    Check(preset.TotalDose == 51 && preset.InputPath == input, "calibration preset retains collection exposure and folder");
    Check(service.LoadCalibrationPreset("Krios · K3", current with { TargetPixelSize = 2.5m }).TargetPixelSize == 2.5m, "valid physical target survives preset load");
    service.SaveCalibrationPreset("Custom", current with { Pixel = 1.2m, InputPattern = "Sample*.mrc" });
    var reloaded = new WarpSessionPreview(root).LoadCalibrationPreset("Custom", current);
    Check(reloaded.InputPattern == "Sample*.mrc" && reloaded.Pixel == 1.2m && reloaded.TargetPixelSize == 1.2m, "saved modern calibration pattern roundtrips");
    var oldPreset = new WarpCalibrationPreset("Legacy", new() { ["Acquisition"] = JsonSerializer.SerializeToElement("TIFF"), ["Pixel"] = JsonSerializer.SerializeToElement(1.3m) });
    await File.WriteAllTextAsync(Path.Combine(root, "calibration-presets.json"), JsonSerializer.Serialize(new[] { oldPreset }));
    var migratedPreset = new WarpSessionPreview(root).LoadCalibrationPreset("Legacy", current);
    Check(migratedPreset.InputPattern == "*.tif*" && migratedPreset.Pixel == 1.3m, "legacy format-only presets migrate to a wildcard");
} finally { Directory.Delete(sandbox, true); }
Console.WriteLine($"PASS {checks} modern settings assertions");
`);
    const result = await run('dotnet', ['run', '--project', path.join(temp, 'SettingsCheck.csproj'), '--verbosity', 'quiet'], { cwd: temp, timeout: 110000, maxBuffer: 1024 * 1024 });
    assert.match(result.stdout, /PASS \d+ modern settings assertions/);
    console.log(result.stdout.trim());
});
