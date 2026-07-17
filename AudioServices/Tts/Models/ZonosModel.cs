using System;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Io;
using HartsyInference.Audio.Models.Zonos;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Phonemizer.Espeak;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Zonos v0.1 (Zyphra/Zonos-v0.1-transformer) — transformer backbone over 9 DAC codebooks → 44.1 kHz
/// voice-cloning TTS. Provider id <c>zonos_tts</c>. The engine <see cref="ZonosTts"/> wires the ResNet293 speaker
/// encoder (ref clip → 128-d speaker, verified corr 1.0), the espeak → 189-symbol phoneme tokenizer, the prefix
/// conditioner (cond + CFG-uncond, corr 1.0), and the delayed-AR generator (backbone bit-parity vs upstream).
/// A voice-reference clip is required (Zonos is a cloning model).</summary>
public static class ZonosModel
{
    private const string ModelRepo = "Zyphra/Zonos-v0.1-transformer";
    private const string SpeakerRepo = "Zyphra/Zonos-v0.1-speaker-embedding";
    private const string EspeakLanguage = "en-us";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => ModelRepo,
        LoadAsync = async (_, ct) =>
        {
            string modelPath = await AudioModelCache.GetAsync(ModelRepo, "model.safetensors", ct: ct).ConfigureAwait(false);
            // The engine DAC consumes the canonical descript .pth layout (the HF safetensors mirrors are reshaped).
            string dacPath = await AudioModelCache.GetAsync("descript/descript-audio-codec", "weights.pth", ct: ct).ConfigureAwait(false);
            string spkPath = await AudioModelCache.GetAsync(SpeakerRepo, "ResNet293_SimAM_ASP_base.pt", ct: ct).ConfigureAwait(false);
            string ldaPath = await AudioModelCache.GetAsync(SpeakerRepo, "ResNet293_SimAM_ASP_base_LDA-128.pt", ct: ct).ConfigureAwait(false);

            SafeTensorsLoader modelLoader = new();
            modelLoader.Load(modelPath);
            PytorchPickleLoader dacLoader = new();
            dacLoader.Load(dacPath);
            PytorchPickleLoader spkLoader = new();
            spkLoader.Load(spkPath);
            PytorchPickleLoader ldaLoader = new();
            ldaLoader.Load(ldaPath);

            EspeakPhonemizer phonemizer = EspeakPhonemizer.FromCache(EspeakLanguage);
            ZonosTts tts = new(ZonosConfig.V0_1Transformer, phonemizer, EspeakLanguage);
            tts.LoadWeights(modelLoader.GetAllTensors(), dacLoader.GetAllTensors(),
                spkLoader.GetAllTensors(), ldaLoader.GetAllTensors());
            Logs.Info("[AudioLab][Zonos] Loaded Zyphra/Zonos-v0.1-transformer (ResNet293 clone + DAC, 44.1 kHz).");

            // Speaker encoder wants 16 kHz; the handler hands us a mono 24 kHz reference.
            Resampler to16k = Resampler.Create(24_000, tts.SpeakerSampleRate);

            return new TtsRunner(tts.SampleRate, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new NotSupportedException(
                        "[AudioLab][Zonos] Supply a voice-reference clip — Zonos clones its speaker (no random voice).");
                }
                float[] refWav16k = to16k.Resample(req.ReferenceMono24k);
                ZonosControls controls = new()
                {
                    LanguageId = ZonosLanguages.Resolve(EspeakLanguage),
                    CfgScale = req.CfgScale is > 0 ? (float)req.CfgScale.Value : 2.0f,
                };
                return tts.Synthesize(backend, req.Text, refWav16k, controls, req.Seed);
            }, tts, modelLoader, dacLoader, spkLoader, ldaLoader);
        },
    };
}
