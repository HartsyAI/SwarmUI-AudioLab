using Microsoft.AspNetCore.Html;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Registers the per-user API key types used by AudioLab's cloud providers.
/// <para>Without this, no input field exists in Server &gt; User Settings &gt; API Keys for these
/// services — the key-entry table in <c>UserTab.cshtml</c> is generated entirely from
/// <see cref="UserUpstreamApiKeys.KeysByType"/>, so an unregistered type is unreachable and every
/// provider depending on it fails with an empty key.</para>
/// <para>Key type strings must match the <c>WithApiProvider(...)</c> argument in the matching
/// <c>AudioProviders/*.cs</c> file exactly.</para></summary>
public static class AudioApiKeys
{
    /// <summary>Whether <see cref="RegisterAll"/> has already run, so repeat calls are a no-op.</summary>
    private static bool Registered = false;

    /// <summary>Registers every API key type used by AudioLab's cloud providers. Idempotent.</summary>
    public static void RegisterAll()
    {
        if (Registered)
        {
            return;
        }
        Registered = true;
        RegisterApiKey(new("elevenlabs_api", "elevenlabs", "ElevenLabs", "https://elevenlabs.io/app/settings/api-keys",
            new("Used by AudioLab for ElevenLabs text-to-speech, sound effects, voice changer, and voice isolator.")));
        RegisterApiKey(new("openai_api", "openai", "OpenAI", "https://platform.openai.com/api-keys",
            new("Used by AudioLab for OpenAI text-to-speech and Whisper/GPT-4o transcription, and by the LLM Assistant extension for GPT text generation.")));
        RegisterApiKey(new("azure_speech_api", "azurespeech", "Azure Speech", "https://portal.azure.com/",
            new("Used by AudioLab for Azure Cognitive Services text-to-speech and speech-to-text.<br>Enter the Speech resource key; the region is configured per-provider.")));
        RegisterApiKey(new("aws_api", "aws", "AWS", "https://console.aws.amazon.com/iam/",
            new("Used by AudioLab for Amazon Polly text-to-speech.")));
        RegisterApiKey(new("google_cloud_api", "googlecloud", "Google Cloud", "https://console.cloud.google.com/apis/credentials",
            new("Used by AudioLab for Google Cloud text-to-speech and speech-to-text.")));
        RegisterApiKey(new("deepgram_api", "deepgram", "Deepgram", "https://console.deepgram.com/",
            new("Used by AudioLab for Deepgram Aura text-to-speech and Nova transcription.")));
        RegisterApiKey(new("cartesia_api", "cartesia", "Cartesia", "https://play.cartesia.ai/keys",
            new("Used by AudioLab for Cartesia Sonic low-latency text-to-speech.")));
        RegisterApiKey(new("playht_api", "playht", "PlayHT", "https://play.ht/studio/api-access",
            new("Used by AudioLab for PlayHT text-to-speech.")));
        RegisterApiKey(new("assemblyai_api", "assemblyai", "AssemblyAI", "https://www.assemblyai.com/app/api-keys",
            new("Used by AudioLab for AssemblyAI transcription with speaker diarization and sentiment analysis.")));
        RegisterApiKey(new("suno_api", "suno", "Suno", "https://suno.com/",
            new("Used by AudioLab for Suno AI music generation.")));
        RegisterApiKey(new("udio_api", "udio", "Udio", "https://www.udio.com/",
            new("Used by AudioLab for Udio AI music generation.")));
        RegisterApiKey(new("dolby_api", "dolby", "Dolby.io", "https://dashboard.dolby.io/",
            new("Used by AudioLab for Dolby.io Media Enhance audio processing.")));
        Logs.Info("[AudioLab] Registered cloud provider API key types.");
    }

    /// <summary>Registers one key type, tolerating another extension having already registered it
    /// (core's <see cref="UserUpstreamApiKeys.Register"/> throws on a duplicate, and SwarmUI-LLMAssistant
    /// registers <c>openai_api</c> when present).</summary>
    private static void RegisterApiKey(UserUpstreamApiKeys.ApiKeyInfo info)
    {
        if (!UserUpstreamApiKeys.KeysByType.ContainsKey(info.KeyType))
        {
            UserUpstreamApiKeys.Register(info);
        }
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add(info.KeyType);
    }
}
