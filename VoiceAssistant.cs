using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Hartsy.Extensions.VoiceAssistant.WebAPI;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.VoiceAssistant;

public class VoiceAssistant : Extension
{
    public override void OnPreInit()
    {
        Logs.Info("VoiceAssistant Version 0.1 has started.");
        //ScriptFiles.Add("Assets/");
        //StyleSheetFiles.Add("Assets/");
    }

    public override void OnInit()
    {
        // Register API endpoints so they can be used in the frontend
        VoiceAssistantAPI.Register();
    }

    public override void OnShutdown()
    {
        Logs.Info("VoiceAssistant is shutting down.");
    }
}

