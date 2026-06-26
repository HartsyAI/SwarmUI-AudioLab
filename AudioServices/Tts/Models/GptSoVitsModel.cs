using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Frontends;
using HartsyInference.Audio.Models.GptSoVits;
using HartsyInference.Audio.Models.Hubert;
using HartsyInference.Audio.Models.OpenVoice;
using HartsyInference.Audio.Models.Vits;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>GPT-SoVITS v2 (lj1995/GPT-SoVITS) — zero-shot ENGLISH TTS: HuBERT SSL + Text2Semantic (s1, AR) +
/// SoVITS (s2), 32 kHz, in the reference speaker's voice. Provider id <c>gptsovits_clone</c>. Needs a reference
/// clip (decoded to 16 kHz for HuBERT and 32 kHz for the 1025-bin linear spectrogram) and its transcript; the
/// English G2P front-end (<see cref="GptSoVitsFrontend"/>/<see cref="GptSoVitsSymbols"/>) is self-contained and the
/// English path uses zero BERT (no chinese-roberta). Auto-downloads the v2-final pretrained s1/s2 + chinese-hubert.
///
/// <para>Caveat: the s1 AR loop has no KV cache (O(n²)) in the engine today, so long sentences are slow — keep test
/// clips short until the engine adds the per-layer K/V cache.</para></summary>
public static class GptSoVitsModel
{
    private const string Repo = "lj1995/GPT-SoVITS";
    private const string S2File = "gsv-v2final-pretrained/s2G2333k.pth";
    private const string S1File = "gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt";
    private const string HubertFile = "chinese-hubert-base/pytorch_model.bin";

    /// <summary>GPT-SoVITS v2 SoVITS (VITS) config — resblock "1", ∏[10,8,2,2,2]=640 hop, 32 kHz output.</summary>
    private static VitsConfig V2Config() => new()
    {
        InterChannels = 192, HiddenChannels = 192, FilterChannels = 768, NumHeads = 2, NumEncoderLayers = 6,
        EncoderKernelSize = 3, WindowSize = 4, GinChannels = 512,
        FlowLayers = 4, FlowFlows = 4, FlowKernelSize = 5, FlowDilationRate = 1,
        ResBlock = "1", ResBlockKernelSizes = [3, 7, 11],
        ResBlockDilations = [[1, 3, 5], [1, 3, 5], [1, 3, 5]],
        UpsampleRates = [10, 8, 2, 2, 2], UpsampleInitialChannel = 512, UpsampleKernelSizes = [16, 16, 8, 2, 2],
        SampleRate = 32_000,
    };

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string s2Path = await AudioModelCache.GetAsync(Repo, S2File, ct: ct).ConfigureAwait(false);
            string s1Path = await AudioModelCache.GetAsync(Repo, S1File, ct: ct).ConfigureAwait(false);
            string hubPath = await AudioModelCache.GetAsync(Repo, HubertFile, ct: ct).ConfigureAwait(false);

            PytorchPickleLoader s2l = new(); s2l.Load(s2Path);
            IReadOnlyDictionary<string, Tensor> s2w = s2l.GetAllTensors();
            SoVitsSynthesizer s2 = new(V2Config(), sslDim: 768, sslLayers: 3, textLayers: 6, enc2Layers: 3, mrteHidden: 512, mrteHeads: 4);
            s2.LoadWeights(s2w);
            SoVitsRefEnc refEnc = new(); refEnc.LoadWeights(s2w, "ref_enc");

            PytorchPickleLoader s1l = new(); s1l.Load(s1Path);
            Text2Semantic s1 = new(new Text2SemanticConfig());
            s1.LoadWeights(s1l.GetAllTensors(), "model");

            PytorchPickleLoader hl = new(); hl.Load(hubPath);
            Hubert hubert = new(new HubertConfig());
            hubert.LoadWeights(hl.GetAllTensors());

            GptSoVitsPipeline pipeline = new(hubert, s1, s2, refEnc);
            Logs.Info("[AudioLab][GPT-SoVITS] Loaded lj1995/GPT-SoVITS v2 (HuBERT + s1 + s2, 32 kHz, English zero-shot).");

            // Components are shared resources the pipeline doesn't own → dispose them (+ the loaders) ourselves.
            IDisposable[] keep = [pipeline, hubert, s1, s2, s2l, s1l, hl];
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                if (string.IsNullOrEmpty(req.ReferenceB64))
                {
                    throw new InvalidOperationException(
                        "GPT-SoVITS needs a reference voice clip — upload a short WAV (~3–10s) in the voice reference field.");
                }
                if (string.IsNullOrWhiteSpace(req.RefText))
                {
                    throw new InvalidOperationException(
                        "GPT-SoVITS needs the reference transcript — enter the exact words spoken in the reference clip "
                        + "in the reference text field.");
                }

                // Reference clip → 16 kHz (HuBERT) + 32 kHz (1025-bin linear spectrogram for the speaker embedding).
                float[] ref16 = AudioIo.DecodeBase64ToMono(req.ReferenceB64, 16_000, CancellationToken.None);
                float[] ref32 = AudioIo.DecodeBase64ToMono(req.ReferenceB64, 32_000, CancellationToken.None);
                Tensor refPcm = new(new TensorShape(1, 1, ref16.Length), DType.F32);
                new Span<float>(ref16).CopyTo(refPcm.AsSpan<float>());
                Tensor refSpec = LinearSpectrogram.Extract(ref32, 2048, 640);   // [1, 1025, tSpec]
                int tSpec = (int)refSpec.Shape[2];

                // English G2P → phoneme ids; s1 text span = ref + target, BERT is zero for the English path.
                int[] refIds = GptSoVitsSymbols.ToSequence(GptSoVitsFrontend.CleanText(req.RefText, "en").Phones);
                int[] tgtIds = GptSoVitsSymbols.ToSequence(GptSoVitsFrontend.CleanText(req.Text, "en").Phones);
                int[] allIds = refIds.Concat(tgtIds).ToArray();
                Tensor zeroBert = new(new TensorShape(1024, allIds.Length), DType.F32);
                try
                {
                    return pipeline.Generate(backend, refPcm, ref16.Length, refSpec, tSpec, allIds, zeroBert, tgtIds, seed: req.Seed);
                }
                finally
                {
                    refPcm.Dispose();
                    refSpec.Dispose();
                    zeroBert.Dispose();
                }
            }, keep);
        },
    };
}
