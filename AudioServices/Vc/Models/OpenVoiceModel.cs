using System;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.OpenVoice;
using HartsyInference.Audio.Models.Vits;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>OpenVoice V2 tone-color converter (myshell-ai/OpenVoiceV2) — a VITS posterior + flow + HiFi-GAN run
/// as a voice converter: it re-voices a source clip into a target speaker's tone color. Provider id
/// <c>openvoice_clone</c>. The converter checkpoint auto-downloads.
///
/// <para>Config = <see cref="VitsConfig.PiperHigh"/> (resblock "1", upsample ∏[8,8,2,2]=256 hop, 22.05 kHz) with
/// <c>GinChannels=256</c> for speaker conditioning; 513 linear-spec bins (n_fft 1024). The pipeline carries its
/// own <c>ref_enc</c> (Conv2d+GRU over the linear spec), so <see cref="OpenVoicePipeline.ConvertWithReferences"/>
/// extracts the source/target speaker vectors internally from the two spectrograms.</para></summary>
public static class OpenVoiceModel
{
    private const string Repo = "myshell-ai/OpenVoiceV2";
    private const string CheckpointFile = "converter/checkpoint.pth";
    private const int NFft = 1024, Hop = 256, SpecChannels = 513;

    public static readonly VcModelDescriptor Descriptor = new()
    {
        ManagesOwnWeights = true,
        InputSampleRate = 22_050,
        CacheKey = (providerId, _) => $"{providerId}:openvoice-v2",
        LoadAsync = async (_, _, ct) =>
        {
            string ckpt = await AudioModelCache.GetAsync(Repo, CheckpointFile, ct: ct).ConfigureAwait(false);
            PytorchPickleLoader loader = new();
            loader.Load(ckpt);

            VitsConfig cfg = VitsConfig.PiperHigh with { GinChannels = 256 };
            OpenVoicePipeline pipeline = new(cfg, SpecChannels, posteriorLayers: 16);
            pipeline.LoadWeights(loader.GetAllTensors());
            Logs.Info("[AudioLab][OpenVoice] Loaded myshell-ai/OpenVoiceV2 tone-color converter (22.05 kHz).");

            return new VcRunner(pipeline.SampleRate, (backend, src, tgt) =>
            {
                if (tgt is null || tgt.Length == 0)
                {
                    throw new InvalidOperationException(
                        "OpenVoice needs a target voice — upload a reference clip in the target voice field.");
                }
                Tensor srcSpec = LinearSpectrogram.Extract(src, NFft, Hop);
                Tensor tgtSpec = LinearSpectrogram.Extract(tgt, NFft, Hop);
                try
                {
                    return pipeline.ConvertWithReferences(backend, srcSpec, (int)srcSpec.Shape[2], tgtSpec, (int)tgtSpec.Shape[2]);
                }
                finally
                {
                    srcSpec.Dispose();
                    tgtSpec.Dispose();
                }
            }, pipeline, loader);
        },
    };
}
