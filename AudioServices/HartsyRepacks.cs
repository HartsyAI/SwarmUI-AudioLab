namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>The Hartsy card each local model installs from, keyed by provider id then model id.
///
/// <para>Each card holds a converted repack of the upstream weights plus its companion files (shards, codecs,
/// tokenizers, configs). Every converted file names, in its <c>hartsy.stands_in_for</c> metadata, the upstream path
/// it replaces, so the engine finds it where its loaders already look. <see cref="HartsyInstaller"/> does the
/// download.</para>
///
/// <para>Rows that share one download (a quantization made on-device from the base weights, or a streaming row
/// riding on another family's checkpoint) name the card of the weights they load.</para></summary>
public static class HartsyRepacks
{
    /// <summary>Hartsy's site root.</summary>
    public const string SiteUrl = "https://hartsy.ai";

    /// <summary>Provider id, then model id, to the Hartsy card id.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> _cards = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acestep_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = "96c439b9-f47d-46f6-81a8-495937953dba", // ACE-Step 1.5 Base
            ["sft"] = "d2c6986f-682b-47f4-b551-0079decf39b9", // ACE-Step 1.5 SFT
            ["turbo"] = "9e18f584-5100-4fbf-9bfd-404e33c623ab", // ACE-Step 1.5 Turbo
            ["turbo-continuous"] = "8bcbe703-af3b-4196-ab36-d2a94561fe5d", // ACE-Step 1.5 Turbo Continuous
            ["turbo-shift1"] = "a5f4bcbd-92b8-4101-9f3a-b013fd636c8c", // ACE-Step 1.5 Turbo Shift 1
            ["turbo-shift3"] = "98190d1e-f65e-44d0-a2db-e54ea12ab3d6", // ACE-Step 1.5 Turbo Shift 3
            ["xl-base"] = "b40fa58b-a2f1-42eb-94d3-69670de1aa95", // ACE-Step 1.5 XL Base
            ["xl-sft"] = "dc1e074b-53f3-4dc0-ae4a-75510f19da9d", // ACE-Step 1.5 XL SFT
            ["xl-turbo"] = "5d1bf2c5-ea32-4578-b660-e6bce081cd1f", // ACE-Step 1.5 XL Turbo
        },
        ["audiogen_sfx"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["medium"] = "015876a5-3218-41e1-9b54-26a9077f1870", // AudioGen Medium
        },
        ["auk_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = "cc7d5cc0-66a2-46e3-b651-ede3f74a0b95", // AuK
            ["flash"] = "0a0b69f6-faa6-494b-96ec-868167605ece", // AuK Flash
        },
        ["bark_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "2654d7dd-c285-4229-9b44-045f66693728", // Bark
        },
        ["breeze_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["breeze-tts-2"] = "9ada5af5-85b0-4654-b5b6-02f94050697b", // Breeze TTS 2
        },
        ["chatterbox_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "09cbaa61-47a9-45ef-b760-eb087b891459", // Chatterbox TTS
        },
        ["controlfoley_sfx"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["large-44k"] = "f80d2ca1-0217-4ec8-89ec-d5cface9732a", // ControlFoley
        },
        ["cosyvoice_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["2-0.5b"] = "59756559-fe4c-40b1-87ce-43f1554a42dc", // CosyVoice 2
        },
        ["csm_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1b"] = "3a20447a-358e-4c7c-a5b7-a1c96966d5fd", // CSM 1B
        },
        ["demucs_fx"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["htdemucs"] = "3f8afef7-3cb8-44ae-9525-b64dcc8640c0", // Demucs HTDemucs
            ["htdemucs_6s"] = "9e52e33b-2b81-4969-b20d-6adef56766d3", // Demucs HTDemucs 6-Stem
        },
        ["dia_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1.6b"] = "1d0a4876-387b-4a8f-a2ca-33db0f9a40a4", // Dia 1.6B
        },
        ["distilwhisper_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["large-v3"] = "ab0eced5-4105-462e-b290-72856deae9df", // Distil-Whisper Large v3
            ["large-v3.5"] = "12de1d30-d060-4711-838e-1bba2bcf39b1", // Distil-Whisper Large v3.5
        },
        ["f5_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["v1-base"] = "304888f4-94f2-4826-b16a-870dc5a6f7ef", // F5-TTS
        },
        ["fishaudio_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["s2-pro"] = "753dfce5-3a61-467f-945b-60adb1607d82", // Fish Audio S2 Pro
        },
        ["fishspeech_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fish-speech-1.4"] = "b5539bc0-672c-478e-84c3-3b0a939a4d9a", // Fish Speech 1.4 (a version of the Fish Speech card)
            ["fish-speech-1.5"] = "d35bf127-febd-44a7-863d-548c64f1484d", // Fish Speech 1.5
        },
        ["gptsovits_clone"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "9a5d0d0e-4afb-4fe1-9976-9a5ad6a8ac85", // GPT-SoVITS
        },
        ["heartlib_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Q8 and Q4 are converted on-device from the base weights, so they install the base card.
            ["3b-base"] = "275e03bb-1e73-425e-8481-c53c82ca5384", // HeartMuLa 3B
            ["3b-base-q4"] = "275e03bb-1e73-425e-8481-c53c82ca5384",
            ["3b-base-q8"] = "275e03bb-1e73-425e-8481-c53c82ca5384",
            ["3b-hny"] = "cc752ffd-7d73-4611-b047-cac510f4e1b7", // HeartMuLa 3B Happy New Year
            ["3b-hny-q4"] = "cc752ffd-7d73-4611-b047-cac510f4e1b7",
            ["3b-hny-q8"] = "cc752ffd-7d73-4611-b047-cac510f4e1b7",
            ["3b-rl"] = "3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00", // HeartMuLa 3B RL
            ["3b-rl-q4"] = "3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00",
            ["3b-rl-q8"] = "3f51bcef-5b5b-4319-8ed6-78eeb9aa2f00",
        },
        ["indextts2_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["v2_0"] = "06498fe8-0dd3-4d21-83a5-9d41fdb951d8", // IndexTTS 2.0
            ["v2_5"] = "4f66d8cf-8493-48c6-b86d-5d735bd35be7", // IndexTTS 2.5
        },
        ["indextts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["v1_5"] = "56ad3c7b-6ae1-4881-afe6-2934c2522cdc", // IndexTTS 1.5
        },
        ["kokoro_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "886ff167-e93d-44fb-9bf4-0f4ec4393891", // Kokoro TTS
        },
        ["kyutaistt_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1b-en-fr"] = "3809b06f-980f-4b99-a717-372c52192946", // Kyutai STT 1B English/French
            ["2.6b-en"] = "0d6d85d0-82c9-4f02-9adc-d463a735479a", // Kyutai STT 2.6B English
        },
        ["kyutaitts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1.6b-en-fr"] = "e81cc622-e0f9-4ee9-8cb9-daa3465de29f", // Kyutai TTS 1.6B
        },
        ["melotts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english-v3"] = "6d5891e7-92f5-49e1-b1e2-3eb4c7b2b6f6", // MeloTTS English
        },
        ["minimax_music3"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Q8 and Q4 are converted on-device from the base weights, so they install the base card.
            ["base"] = "c97879c7-648d-4b1c-8ab0-1e1ca159ded1", // MiniMax Music 3
            ["q4"] = "c97879c7-648d-4b1c-8ab0-1e1ca159ded1",
            ["q8"] = "c97879c7-648d-4b1c-8ab0-1e1ca159ded1",
        },
        ["moonshine_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = "215fe70b-8e1b-4be3-804a-25d46ef85a6f", // Moonshine Base
            ["tiny"] = "732e2a60-ad35-4aee-9639-e81196face4d", // Moonshine Tiny
        },
        ["moonshinestreaming_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["medium"] = "1c2a2195-9c0a-42d3-8c6a-1956baf6df20", // Moonshine Streaming Medium
            ["small"] = "eab61907-9561-4279-9fbd-ab9691c8d953", // Moonshine Streaming Small
            ["tiny"] = "560df088-9a4f-4b3c-90d2-43a3b57c7100", // Moonshine Streaming Tiny
        },
        ["musicgen_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["large"] = "57e56727-ab46-49ef-aeec-7dec2c484070", // MusicGen Large
            ["medium"] = "6e26c1da-1098-406d-aa5e-9e131b217723", // MusicGen Medium
            ["small"] = "87c05a55-058a-4d54-bbfc-faf2e18ff41f", // MusicGen Small
        },
        ["neutts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["air"] = "b308a4ff-9e9a-4b27-a32a-0ea220c6b0e3", // NeuTTS Air
        },
        ["openvoice_clone"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["v2"] = "4208508c-bd10-4119-afbd-368afa546921", // OpenVoice V2
        },
        ["orpheus_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["3b"] = "7ebbef68-6a4e-4b62-a49a-d92210d69016", // Orpheus 3B
        },
        ["hurston_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "f8987685-ca30-4926-9d20-0fac8a306b9e", // Hurston-1
        },
        ["piper_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // One voice (en_US lessac medium) as a bare .onnx with no stand-in path, so the installer cannot place
            // it and the engine keeps fetching voices itself.
            ["default"] = "da49c57a-7d01-4f5a-9a11-ec60e2b3ba5d", // Piper TTS
        },
        ["pockettts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "dd7cfcb2-4d63-4d9f-b07c-62562dbbc285", // PocketTTS
        },
        ["qwen3_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0.6B-Base"] = "a22bc965-45e2-489f-8713-6956f7af0ae9", // Qwen3-TTS 0.6B Base
            ["0.6B-CustomVoice"] = "1373e35a-cf15-471d-a704-a28be15f96de", // Qwen3-TTS 0.6B CustomVoice
            ["1.7B-Base"] = "5266aa28-2c49-4be0-8212-35c0296370da", // Qwen3-TTS 1.7B Base
            ["1.7B-CustomVoice"] = "50f7bb96-ce77-4c8c-af93-3ed352baab4d", // Qwen3-TTS 1.7B CustomVoice
            ["1.7B-VoiceDesign"] = "661d0c97-86b7-44e1-8f6f-5972bab87f99", // Qwen3-TTS 1.7B VoiceDesign
        },
        ["resemble_enhance_fx"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // One checkpoint carries both the enhancer and the denoiser.
            ["denoise"] = "63475ba9-c3cf-4e32-9501-3fa7e4221390", // Resemble Enhance
            ["enhance"] = "63475ba9-c3cf-4e32-9501-3fa7e4221390",
        },
        ["sheetsage2_transcribe"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bf16"] = "ead42a7b-c5b5-43b9-91e7-77a50d3edb3e", // SheetSage2
        },
        ["sparktts_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0.5B"] = "4c302041-730f-4285-8462-d28b58bffb52", // Spark-TTS 0.5B
        },
        ["stableaudio_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["open-small"] = "ee956408-1719-4251-bad9-6fde866ae4b1", // Stable Audio Open Small
        },
        ["styletts2_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["libritts"] = "3c32cabe-1815-4a67-8d5b-2adb1b7df82c", // StyleTTS 2
        },
        ["vibevoice_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1.5b"] = "c65f409f-a1a8-4ced-9a13-43f093e40e0e", // VibeVoice 1.5B
        },
        ["whisper_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = "c9618d12-d411-4fb0-acce-9d475b02a8e3", // Whisper Base
            ["large-v2"] = "163b99cd-b7fa-4b57-8382-3fe4cbe39abc", // Whisper Large v2
            ["medium"] = "bc615007-6b9b-42bf-8c41-f158c664a0de", // Whisper Medium
            ["small"] = "7323b95b-dd12-4b2e-9525-33eb26015be5", // Whisper Small
            ["tiny"] = "949944e6-4477-4e32-85b4-985ee5271b47", // Whisper Tiny
        },
        ["whisperstreaming_stt"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Streams the Whisper base checkpoint.
            ["base"] = "c9618d12-d411-4fb0-acce-9d475b02a8e3", // Whisper Base
        },
        ["yue2_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bf16"] = "e3ccd2ac-577e-4693-acd2-11ef4ead4ce9", // YuE2
        },
        ["yue_music"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en-cot"] = "3b0241fa-65fb-4d03-945a-3a67fa4004c5", // YuE 7B English CoT
            ["en-icl"] = "d35a6e57-e680-4e2e-9090-f32102d206d8", // YuE 7B English ICL
            ["zh-cot"] = "b939b731-6294-42b7-bf11-d8035bea2ffe", // YuE 7B Chinese CoT
            ["zh-icl"] = "c1936296-e2dc-4a6c-ac76-0a4965875adb", // YuE 7B Chinese ICL
        },
        ["zipvoice_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = "9533c7bf-774c-48e6-9387-b0308fc6b606", // ZipVoice
        },
        ["zonos_tts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["transformer"] = "d0e9a4b9-aff4-42a6-9af3-5fe2a383ab0a", // Zonos v0.1
        },
    };

    /// <summary>Local models ("provider/model") with no usable Hartsy card: none yet, or a card withheld until its sidecars are correct. They install from upstream until then.</summary>
    internal static readonly HashSet<string> PendingUpload = new(StringComparer.OrdinalIgnoreCase)
    {
        "fishaudio_drama3_tts/native",
        "fishaudio_drama3_tts/int4",
        "rvc_clone/v2",
        // Withheld: these cards still link Whisper Base's config.json, vocab.json, added_tokens.json and generation_config.json.
        "whisper_stt/large-v3",
        "whisper_stt/turbo",
    };

    /// <summary>Providers whose rows ride on another family's card, mapped to that family, so the card's files are
    /// stored once in its folder.</summary>
    private static readonly Dictionary<string, string> _folderOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whisperstreaming_stt"] = "whisper_stt",
    };

    /// <summary>The provider whose weights folder holds this provider's Hartsy files.</summary>
    public static string FolderOwner(string providerId)
        => providerId is not null && _folderOwners.TryGetValue(providerId, out string owner) ? owner : providerId;

    /// <summary>The Hartsy card id for a model, or null when it has none.</summary>
    public static string CardFor(string providerId, string modelId)
    {
        if (providerId is null || modelId is null || !_cards.TryGetValue(providerId, out Dictionary<string, string> models))
        {
            return null;
        }
        return models.TryGetValue(modelId, out string card) ? card : null;
    }

    /// <summary>Every card id in the table, for tests.</summary>
    internal static IEnumerable<(string ProviderId, string ModelId, string CardId)> All()
    {
        foreach ((string providerId, Dictionary<string, string> models) in _cards)
        {
            foreach ((string modelId, string card) in models)
            {
                yield return (providerId, modelId, card);
            }
        }
    }

    /// <summary>The card's page on Hartsy, for people.</summary>
    public static string PageUrl(string cardId) => $"{SiteUrl}/Home?type=models&id={cardId}";

    /// <summary>The card's main file. Redirects to a short-lived signed storage URL.</summary>
    public static string DownloadUrl(string cardId) => $"{SiteUrl}/download/{cardId}?type=model";

    /// <summary>The card's download manifest: its main file and every companion, each with the path it takes inside
    /// the model folder, its size, its hash and its download route.</summary>
    public static string ManifestUrl(string cardId) => $"{SiteUrl}/download/{cardId}/manifest";

    /// <summary>The page link for a model, or null when it has no card.</summary>
    public static string PageUrlFor(string providerId, string modelId)
    {
        string card = CardFor(providerId, modelId);
        return card is null ? null : PageUrl(card);
    }
}
