using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>Generic voice-conversion handler (source audio → re-voiced audio), driven by a per-model
/// <see cref="VcModelDescriptor"/>. Runners cached per resolved model.</summary>
public sealed class VcHandler(string providerId, VcModelDescriptor descriptor) : IAudioHandler
{
    private readonly ConcurrentDictionary<string, IVcRunner> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    public string Category => "voiceconv";

    public bool ManagesOwnWeights => descriptor.ManagesOwnWeights;

    public async Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        onProgress("Loading voice-conversion model...");
        await GetOrLoadAsync(modelId, cancel).ConfigureAwait(false);
        onProgress("Ready.");
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string sourceB64 = AudioIo.Str(args, "source_audio");
        if (string.IsNullOrEmpty(sourceB64))
        {
            return AudioIo.Error("No source audio supplied to re-voice.");
        }
        float[] source = AudioIo.DecodeBase64ToMono(sourceB64, 16_000, cancel);
        if (source.Length == 0)
        {
            return AudioIo.Error("The source audio decoded to no samples.");
        }
        cancel.ThrowIfCancellationRequested();

        IVcRunner runner = await GetOrLoadAsync(AudioIo.Str(args, "__model_id"), cancel).ConfigureAwait(false);
        long start = Environment.TickCount64;
        float[] audio = runner.Convert(backend, source);
        if (audio is null || audio.Length == 0)
        {
            return AudioIo.Error("The voice-conversion model produced no audio.");
        }
        double duration = audio.Length / (double)runner.SampleRate;
        Logs.Verbose($"[AudioLab][VC] Converted {duration:0.0}s @ {runner.SampleRate} Hz in {Environment.TickCount64 - start}ms.");
        return AudioIo.AudioResult(AudioIo.EncodeWavBase64(audio, null, runner.SampleRate), "wav", duration);
    }

    public void Unload(string modelId)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryRemove(key, out IVcRunner runner))
        {
            runner.Dispose();
        }
    }

    private async Task<IVcRunner> GetOrLoadAsync(string modelId, CancellationToken cancel)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryGetValue(key, out IVcRunner existing))
        {
            return existing;
        }
        IVcRunner loaded = await descriptor.LoadAsync(providerId, modelId, cancel).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_cache.TryGetValue(key, out IVcRunner raced))
            {
                loaded.Dispose();
                return raced;
            }
            _cache[key] = loaded;
            return loaded;
        }
    }
}
