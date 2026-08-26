using System.IO;
using Hartsy.Extensions.AudioLab.AudioServices;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>Registers an "Audio" model type so SwarmUI natively scans <c>Models/audio</c> and reads each
/// artifact's SAI ModelSpec metadata, the same way it does for Stable-Diffusion or LoRA.
///
/// <para>This set is the file-truth inventory: every audio artifact on disk lands here, including shards,
/// codecs and files whose architecture nothing has registered. It is deliberately NOT what the generation
/// selector shows — <see cref="AudioArtifactIndex"/> decides which of these are admitted as selectable
/// models. Browsing the raw inventory is <c>ListModels</c> with <c>subtype=Audio</c>.</para>
///
/// <para>Follows the same contract as SwarmUI-LLMAssistant's "LLM" type: core clears
/// <see cref="Program.T2IModelSets"/> on any model-path settings save and fires
/// <see cref="Program.ModelPathsChangedEvent"/> AFTER its own refresh pass, so the re-registered handler has
/// to refresh itself or the type silently disappears until the next manual refresh.</para></summary>
public static class AudioModelTypeRegistration
{
    /// <summary>The <see cref="Program.T2IModelSets"/> key and <see cref="T2IModelHandler.ModelType"/> value.</summary>
    public const string ModelType = "Audio";

    /// <summary>Registers the handler and keeps it registered across model-path settings changes. Call from
    /// <c>OnInit</c>, which runs before core's first <c>RefreshAllModelSets</c>, so the first scan includes it.</summary>
    public static void Register()
    {
        RegisterHandler();
        Program.ModelPathsChangedEvent += OnModelPathsChanged;
    }

    /// <summary>Drops the settings-change hook; the handler itself is owned by core's registry.</summary>
    public static void Unregister()
    {
        Program.ModelPathsChangedEvent -= OnModelPathsChanged;
    }

    /// <summary>Returns the live Audio handler, or null when registration has not run.</summary>
    public static T2IModelHandler Handler
        => Program.T2IModelSets.TryGetValue(ModelType, out T2IModelHandler handler) ? handler : null;

    private static void OnModelPathsChanged()
    {
        RegisterHandler();
        Handler?.Refresh();
    }

    private static void RegisterHandler()
    {
        if (Program.T2IModelSets.ContainsKey(ModelType))
        {
            return;
        }
        List<string> paths = [];
        foreach (string root in Program.ServerSettings.Paths.ActualModelRoots)
        {
            string audioPath = Path.Combine(root, AudioConfiguration.ModelRootFolderName);
            try
            {
                Directory.CreateDirectory(audioPath);
                paths.Add(audioPath);
            }
            catch (Exception ex)
            {
                Logs.Error($"[AudioLab] Could not prepare audio model folder '{audioPath}': {ex.Message}");
            }
        }
        if (paths.Count == 0)
        {
            Logs.Warning("[AudioLab] No usable audio model folder; audio models will not be scanned from disk.");
            return;
        }
        T2IModelHandler handler = new() { ModelType = ModelType, FolderPaths = [.. paths], DownloadFolderPath = paths[0] };
        Program.T2IModelSets[ModelType] = handler;
        Logs.Info($"[AudioLab] Registered '{ModelType}' model type over {paths.Count} folder(s): {string.Join(", ", paths)}");
    }
}
