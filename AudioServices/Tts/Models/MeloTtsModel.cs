using System;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>MeloTTS (VITS + multi-stream text encoder) — 44.1 kHz. Provider id <c>melotts_tts</c>. The engine's
/// <c>MeloTtsPipeline</c> (MeloTtsConfig.EnglishV3) is implemented, but its <c>Synthesize</c> needs phoneme + tone
/// + language id streams AND two BERT feature tensors ([1024,T] + [768,T]) the caller must produce — i.e. an
/// espeak phonemizer, a tone predictor, and a (language-specific) BERT encoder, none shipped. Wired as a
/// dispatchable model that fails with a clear message until those land (see engine-work list).</summary>
public static class MeloTtsModel
{
    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "myshell-ai/MeloTTS-English-v3",
        LoadAsync = (_, _) => throw new NotSupportedException(
            "[AudioLab][MeloTTS] Not runnable yet: needs an espeak phonemizer + tone/language id streams + a BERT "
            + "feature encoder ([1024,T] and [768,T]) to feed MeloTtsPipeline.Synthesize. The engine pipeline is ready; "
            + "the text/BERT front-end is the gap. See engine-work list."),
    };
}
