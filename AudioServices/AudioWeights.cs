using System.IO;
using System.Linq;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// Resolves and downloads the local <c>.safetensors</c> checkpoints the in-process C# engine needs — the
/// C# replacement for AudioLab's Python install (venv + pip + HF pull). Sits between "AudioLab knows what
/// the user wants" and "the engine loads a file".
///
/// <para>Doubles as the routing gate: <see cref="AudioServerManager"/> only hands a request to the engine
/// when <see cref="ResolveCheckpoint"/> returns a real path, so a provider the engine *can* service still
/// falls back to Python until its weights are actually present — no mid-migration regression.</para>
/// </summary>
public static class AudioWeights
{
    /// <summary>Per-category subfolder under the audio model root (mirrors the dirs AudioLab creates).</summary>
    private static string CategorySubfolder(AudioCategory category) => category switch
    {
        AudioCategory.TTS => "tts",
        AudioCategory.STT => "stt",
        AudioCategory.AudioGeneration => "music",
        AudioCategory.VoiceConversion => "clone",
        AudioCategory.AudioProcessing => "fx",
        _ => "misc",
    };

    /// <summary>The conventional directory a provider's local weights live in:
    /// <c>{ModelRoot}/{category}/{ModelPrefix}/</c>. Tolerates a differently-cased directory already on disk
    /// (older installs/manual placement used lowercase provider ids, e.g. <c>fx/demucs</c> vs the canonical
    /// <c>fx/Demucs</c>) — Linux is case-sensitive, so an exact-case miss would otherwise report weights as
    /// absent even though the files are right there. Falls back to the canonical (as-yet-nonexistent) path so
    /// fresh downloads still land in the ModelPrefix-cased directory.</summary>
    public static string WeightsDirectory(AudioProviderDefinition provider)
    {
        string category = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), CategorySubfolder(provider.Category));
        string canonical = Path.Combine(category, provider.ModelPrefix);
        if (Directory.Exists(canonical))
        {
            return canonical;
        }
        if (Directory.Exists(category))
        {
            string existing = Directory.EnumerateDirectories(category)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), provider.ModelPrefix, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }
        return canonical;
    }

    /// <summary>Returns the local checkpoint path for the request, or null if none is usable.
    /// When the request carries a model id (<c>__model_id</c>, injected by <c>BuildEngineArgs</c>) we
    /// resolve the exact variant via <see cref="AudioWeightsRegistry"/> and refuse variants the engine
    /// can't run (returns null → routes to Python). With no model id, falls back to any checkpoint a power
    /// user has dropped in the directory.</summary>
    public static string ResolveCheckpoint(AudioProviderDefinition provider, IReadOnlyDictionary<string, object> args)
    {
        try
        {
            string dir = WeightsDirectory(provider);
            string modelId = args is not null && args.TryGetValue("__model_id", out object idObj) ? idObj?.ToString() : null;

            if (!string.IsNullOrEmpty(modelId))
            {
                AudioWeightsRegistry.DownloadSpec spec = AudioWeightsRegistry.Resolve(provider.Id, modelId);
                if (spec is null)
                {
                    // Engine has no entry for this exact variant — don't guess; let Python handle it.
                    return null;
                }
                string variantPath = Path.Combine(dir, spec.FileName);
                return File.Exists(variantPath) ? variantPath : null;
            }

            // No model-id hint: accept any checkpoint dropped in the provider's weights directory.
            if (!Directory.Exists(dir))
            {
                return null;
            }
            return Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ensures the engine-runnable checkpoint files are on disk, downloading the missing ones.
    /// When <paramref name="modelId"/> is given, only THAT model's file set downloads (variants are distinct
    /// multi-GB checkpoints now — installing a whole provider would pull tens of GB); without it, every
    /// registered file for the provider is ensured (legacy behavior). Returns true if anything was
    /// registered and all files resolved; false if nothing is registered (caller should surface a
    /// "place the file manually" message).</summary>
    public static async Task<bool> EnsureProviderWeightsAsync(AudioProviderDefinition provider, Action<string> onProgress, CancellationToken cancel = default, string modelId = null)
    {
        IReadOnlyCollection<AudioWeightsRegistry.DownloadSpec> specs = string.IsNullOrEmpty(modelId)
            ? AudioWeightsRegistry.DistinctFor(provider.Id)
            : AudioWeightsRegistry.SpecsFor(provider.Id, modelId);
        if (specs.Count == 0)
        {
            return false;
        }
        string dir = WeightsDirectory(provider);
        Directory.CreateDirectory(dir);
        foreach (AudioWeightsRegistry.DownloadSpec spec in specs)
        {
            cancel.ThrowIfCancellationRequested();
            await EnsureWeightAsync(spec, dir, onProgress, cancel);
        }
        return true;
    }

    /// <summary>A .safetensors/checkpoint smaller than this is treated as truncated/corrupt when there is no
    /// canonical hash to verify against. Catches the common "interrupted download left a stub" failure.</summary>
    private const long MinPlausibleCheckpointBytes = 1_000_000;

    /// <summary>Downloads one checkpoint to <paramref name="dir"/> if not already present. Atomic
    /// (.tmp stage + move) so an interrupted download never masquerades as complete. An already-present file
    /// is integrity-checked (hash when the spec has one, else a size floor); a bad file is deleted and
    /// re-downloaded rather than silently accepted.</summary>
    public static async Task EnsureWeightAsync(AudioWeightsRegistry.DownloadSpec spec, string dir, Action<string> onProgress, CancellationToken cancel = default)
    {
        string targetPath = Path.Combine(dir, spec.FileName);
        if (File.Exists(targetPath))
        {
            bool ok;
            if (spec.HasHash)
            {
                ok = await FileMatchesHashAsync(targetPath, spec.Sha256, cancel);
                if (!ok)
                {
                    Logs.Warning($"[AudioLab] '{spec.FileName}' failed SHA-256 verification — re-downloading.");
                }
            }
            else
            {
                // No canonical hash published — at least reject a clearly-truncated file.
                long len = new FileInfo(targetPath).Length;
                ok = len >= MinPlausibleCheckpointBytes;
                if (!ok)
                {
                    Logs.Warning($"[AudioLab] '{spec.FileName}' is implausibly small ({len} bytes) — re-downloading.");
                }
            }
            if (ok)
            {
                onProgress?.Invoke($"{spec.FileName} already present.");
                return;
            }
            onProgress?.Invoke($"{spec.FileName} failed integrity check — re-downloading.");
            try { File.Delete(targetPath); } catch (Exception ex) { Logs.Warning($"[AudioLab] Could not delete bad '{targetPath}': {ex.Message}"); }
        }
        string tmpPath = targetPath + ".tmp";
        if (File.Exists(tmpPath))
        {
            try { File.Delete(tmpPath); } catch { }
        }

        onProgress?.Invoke($"Downloading {spec.FileName}...");
        Logs.Info($"[AudioLab] Downloading audio checkpoint '{spec.FileName}' from {spec.Url}");
        try
        {
            double nextLoggedPct = 0.05;
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            await Utilities.DownloadFile(spec.Url, tmpPath, (bytes, total, perSec) =>
            {
                if (total <= 0) return;
                double pct = bytes / (double)total;
                if (pct >= nextLoggedPct)
                {
                    onProgress?.Invoke($"{spec.FileName}: {pct * 100:0.0}% ({bytes / (1024.0 * 1024.0):F0}/{total / (1024.0 * 1024.0):F0} MB, {perSec / (1024.0 * 1024.0):F1} MB/s)");
                    nextLoggedPct = Math.Round(pct / 0.05) * 0.05 + 0.05;
                }
            }, cancel: cts, verifyHash: spec.HasHash ? spec.Sha256 : null);
            File.Move(tmpPath, targetPath);
            onProgress?.Invoke($"{spec.FileName} download complete.");
            Logs.Info($"[AudioLab] Audio checkpoint '{spec.FileName}' downloaded to {targetPath}");
        }
        catch (Exception ex)
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
            // A genuine download failure (not a user cancel) falls back to the spec's alternate source when it
            // has one — e.g. prefer a pre-converted repack, but still install off the canonical full checkpoint
            // if the repack host is down / not yet published.
            if (spec.Fallback is not null && !cancel.IsCancellationRequested)
            {
                Logs.Warning($"[AudioLab] Primary source for '{spec.FileName}' failed ({ex.Message}); falling back to '{spec.Fallback.FileName}'.");
                onProgress?.Invoke($"{spec.FileName} unavailable from the preferred source — using fallback...");
                await EnsureWeightAsync(spec.Fallback, dir, onProgress, cancel);
                return;
            }
            Logs.Error($"[AudioLab] Failed to download audio checkpoint '{spec.FileName}': {ex.Message}");
            throw;
        }
    }

    /// <summary>Streams the file through SHA-256 and compares (case-insensitive hex) to the expected digest.
    /// On any read error returns true (don't delete a file we merely failed to read — let load surface it).</summary>
    private static async Task<bool> FileMatchesHashAsync(string path, string expectedSha256, CancellationToken cancel)
    {
        try
        {
            await using FileStream fs = File.OpenRead(path);
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, cancel).ConfigureAwait(false);
            string hex = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(hex, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Hash check of '{path}' failed to run: {ex.Message}");
            return true;
        }
    }
}
