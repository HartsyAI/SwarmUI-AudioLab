using SwarmUI.WebAPI;
using SwarmUI.Utils;
using System.Threading.Tasks;

namespace Hartsy.Extensions.VoiceAssistant.WebAPI;

public static class VoiceAssistantPermissions
{
    // Define any permissions needed for voice assistant
}

[API.APIClass("API routes for Voice Assistant extension")]
public class VoiceAssistantAPI
{
    /// <summary>Registers the API calls for the Voice Assistant extension</summary>
    public static void Register()
    {
        // Register voice processing endpoints
        API.RegisterAPICall(ProcessVoiceInput, "process_voice_input");
        API.RegisterAPICall(StartVoiceService, "start_voice_service");
        API.RegisterAPICall(StopVoiceService, "stop_voice_service");
        API.RegisterAPICall(GetVoiceStatus, "voice_status");
        API.RegisterAPICall(ProcessTextCommand, "process_text_command");
    }

    private static async Task<object> ProcessVoiceInput(dynamic data)
    {
        // Implementation will be moved here from VoiceAssistant.cs
        await Task.CompletedTask;
        return new { success = true };
    }

    private static async Task<object> StartVoiceService()
    {
        await Task.CompletedTask;
        return new { success = true };
    }

    private static async Task<object> StopVoiceService()
    {
        await Task.CompletedTask;
        return new { success = true };
    }

    private static object GetVoiceStatus()
    {
        return new { running = false };
    }

    private static async Task<object> ProcessTextCommand(dynamic data)
    {
        await Task.CompletedTask;
        return new { success = true };
    }
}
