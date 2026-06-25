using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>GPT-SoVITS v2 (lj1995/GPT-SoVITS) — zero-shot TTS: HuBERT SSL + Text2Semantic (s1) + SoVITS (s2),
/// 32 kHz. Provider id <c>gptsovits_tts</c>. The engine's <c>GptSoVitsPipeline</c> is complete and the phoneme
/// front-end ships (<c>GptSoVitsFrontend.CleanText</c>, <c>GptSoVitsSymbols</c>, zh/en G2P). The gap is the BERT
/// feature stream <c>Generate</c> requires ([1024, P]) — i.e. a chinese-roberta-wwm-ext-large encoder — plus the
/// reference-audio preprocessing (16 kHz HuBERT input + 1025-bin linear spectrogram) and a reference transcript.
/// Gated until the BERT encoder + reference preprocessing are wired (engine-work list).</summary>
public static class GptSoVitsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "lj1995/GPT-SoVITS",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][GPT-SoVITS] Not runnable yet: GptSoVitsPipeline.Generate needs a BERT feature stream "
            + "([1024,P], chinese-roberta-wwm-ext-large), reference-audio preprocessing (16 kHz + 1025-bin linear spec), "
            + "and a reference transcript. The pipeline + phoneme front-end are ready; the BERT/reference path is the gap. "
            + "See engine-work list."),
    };
}
