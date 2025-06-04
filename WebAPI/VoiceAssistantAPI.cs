using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using SwarmUI.Accounts;
using System.Text.Json;
using Hartsy.Extensions.MagicPromptExtension.WebAPI.Models;
using System.Net.Http;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Html;
using System.Net;
using System.Text.RegularExpressions;
using System.Linq;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

public static class VoiceAssistantPermissions
{
    //public static readonly PermInfoGroup MagicPromptPermGroup = new("MagicPrompt", "Permissions related to MagicPrompt functionality for API calls and settings.");
    //public static readonly PermInfo PermPhoneHome = Permissions.Register(new("magicprompt_phone_home", "Phone Home", "Allows the extension to make outbound calls to retrieve external data.", PermissionDefault.POWERUSERS, MagicPromptPermGroup));
}

[API.APIClass("API routes related to MagicPromptExtension extension")]
public class VoiceAssistantAPI
{
    /// <summary>Registers the API calls for the extension, enabling methods to be called from JavaScript with appropriate permissions.</summary>
    public static void Register()
    {
        //API.RegisterAPICall(LLMAPICalls.PhoneHomeAsync, true, MagicPromptPermissions.PermPhoneHome);
        // All key types must be added to the accepted list first
        string[] keyTypes = ["openai_api", "anthropic_api", "openrouter_api", "openaiapi_local"];
        foreach (string keyType in keyTypes)
        {
            BasicAPIFeatures.AcceptedAPIKeyTypes.Add(keyType);
        }
        // Register API Key tables for each backend
        RegisterApiKeyIfNeeded("openai_api", "openai", "OpenAI (ChatGPT)", "https://platform.openai.com/api-keys",
            new HtmlString("To use OpenAI models in SwarmUI (via Hartsy extensions), you must set your OpenAI API key."));
        RegisterApiKeyIfNeeded("anthropic_api", "anthropic", "Anthropic (Claude)", "https://console.anthropic.com/settings/keys",
            new HtmlString("To use Anthropic models like Claude in SwarmUI (via Hartsy extensions), you must set your Anthropic API key."));
        RegisterApiKeyIfNeeded("openrouter_api", "openrouter", "OpenRouter", "https://openrouter.ai/keys",
            new HtmlString("To use OpenRouter models in SwarmUI (via Hartsy extensions), you must set your OpenRouter API key. OpenRouter gives you access to many different models through a single API."));
        RegisterApiKeyIfNeeded("openaiapi_local", "openaiapi", "OpenAI API (Local)", "#",
            new HtmlString("For connecting to local servers that implement the OpenAI API schema (like LM Studio, text-generation-webui, or LocalAI). You may need to provide API keys or connection details depending on your local setup."));
    }

    /// <summary>Safely registers an API key if it's not already registered</summary>
    private static void RegisterApiKeyIfNeeded(string keyType, string jsPrefix, string title, string createLink, HtmlString infoHtml)
    {
        try
        {
            if (!UserUpstreamApiKeys.KeysByType.ContainsKey(keyType))
            {
                UserUpstreamApiKeys.Register(new(keyType, jsPrefix, title, createLink, infoHtml));
                Logs.Debug($"Registered API key type: {keyType}");
            }
            else
            {
                Logs.Debug($"API key type '{keyType}' already registered, skipping registration");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to register API key type '{keyType}': {ex.Message}");
        }
    }

    /// <summary>Creates a JSON object for a success, includes models and config data.</summary>
    /// <returns>The success bool, models, and config data to the JavaScript function that called it.</returns>
    public static JObject CreateSuccessResponse(string response, List<ModelData> models = null, JObject settings = null)
    {
        return new JObject
        {
            ["success"] = true,
            ["response"] = response,
            ["models"] = models != null ? JArray.FromObject(models) : null,
            ["settings"] = settings,
            ["error"] = null
        };
    }

    /// <summary>Creates a JSON object for a failure, includes the error message.</summary>
    /// <returns>The success bool and the error to the JavaScript function that called it.</returns>
    public static JObject CreateErrorResponse(string errorMessage)
    {
        return new JObject
        {
            { "success", false },
            { "error", errorMessage }
        };
    }
}
