using System.IO;
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
    /// <c>{ModelRoot}/{category}/{ModelPrefix}/</c>.</summary>
    public static string WeightsDirectory(AudioProviderDefinition provider)
        => Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), CategorySubfolder(provider.Category), provider.ModelPrefix);

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

    /// <summary>Ensures every engine-runnable checkpoint for a provider is on disk, downloading the missing
    /// ones. Returns true if the provider has at least one registered checkpoint and all resolved; false if
    /// nothing is registered (caller should surface a "place the file manually" message).</summary>
    public static async Task<bool> EnsureProviderWeightsAsync(AudioProviderDefinition provider, Action<string> onProgress, CancellationToken cancel = default)
    {
        IReadOnlyCollection<AudioWeightsRegistry.DownloadSpec> specs = AudioWeightsRegistry.DistinctFor(provider.Id);
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

    /// <summary>Downloads one checkpoint to <paramref name="dir"/> if not already present. Atomic
    /// (.tmp stage + move) so an interrupted download never masquerades as complete.</summary>
    public static async Task EnsureWeightAsync(AudioWeightsRegistry.DownloadSpec spec, string dir, Action<string> onProgress, CancellationToken cancel = default)
    {
        string targetPath = Path.Combine(dir, spec.FileName);
        if (File.Exists(targetPath))
        {
            onProgress?.Invoke($"{spec.FileName} already present.");
            return;
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
            Logs.Error($"[AudioLab] Failed to download audio checkpoint '{spec.FileName}': {ex.Message}");
            throw;
        }
    }
}
