using System.IO;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>Factory for creating T2IModel instances from AudioProviderDefinition objects.
/// Mirrors the pattern from SwarmUI-API-Backends/Models/ModelFactory.cs.</summary>
public static class AudioModelFactory
{
    private static readonly Dictionary<string, T2IModelClass> _modelClasses = [];
    private const string ExtensionRoot = "src/Extensions/SwarmUI-AudioLab";

    /// <summary>Creates a T2IModel from an AudioModelDefinition and AudioProviderDefinition.</summary>
    public static T2IModel Create(AudioModelDefinition model, AudioProviderDefinition provider)
    {
        string fullName = provider.GetFullModelName(model.Id);
        string previewImage = LoadPreviewImage(provider.Id);
        T2IModelClass modelClass = GetOrCreateModelClass(provider);
        List<string> allTags = ["audiolab", provider.Category.ToString().ToLowerInvariant(), provider.EngineGroup];
        return new T2IModel(null, null, null, fullName)
        {
            Title = model.Name,
            Description = model.Description,
            ModelClass = modelClass,
            StandardWidth = 0,
            StandardHeight = 0,
            IsSupportedModelType = true,
            PreviewImage = previewImage,
            Metadata = new T2IModelHandler.ModelMetadataStore
            {
                ModelName = fullName,
                Title = model.Name,
                Author = provider.Name,
                Description = model.Description,
                PreviewImage = previewImage,
                StandardWidth = 0,
                StandardHeight = 0,
                License = string.IsNullOrEmpty(model.License) ? "Open Source" : model.License,
                UsageHint = $"Audio processing via {provider.Name}",
                ModelClassType = provider.ModelClassId,
                Tags = [.. allTags],
                TimeCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TimeModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
    }

    /// <summary>Creates all T2IModel instances for a provider.</summary>
    public static Dictionary<string, T2IModel> CreateAllModels(AudioProviderDefinition provider)
    {
        Dictionary<string, T2IModel> models = [];
        foreach (AudioModelDefinition modelDef in provider.Models)
        {
            string fullName = provider.GetFullModelName(modelDef.Id);
            models[fullName] = Create(modelDef, provider);
            Logs.Debug($"[AudioModelFactory] Created model: {fullName}");
        }
        return models;
    }

    /// <summary>Gets or creates a T2IModelClass for the provider. Registers compat class with IsAudioModel = true.</summary>
    public static T2IModelClass GetOrCreateModelClass(AudioProviderDefinition provider)
    {
        string id = provider.ModelClassId;
        if (!_modelClasses.TryGetValue(id, out T2IModelClass modelClass))
        {
            T2IModelCompatClass compat = T2IModelClassSorter.RegisterCompat(new()
            {
                ID = id,
                ShortCode = GetShortCode(provider.Category),
                IsAudioModel = true
            });
            modelClass = new T2IModelClass
            {
                ID = id,
                Name = provider.ModelClassName,
                CompatClass = compat,
                StandardWidth = 0,
                StandardHeight = 0,
                IsThisModelOfClass = (model, header) => true
            };
            _modelClasses[id] = modelClass;
            T2IModelClassSorter.Register(modelClass);
            Logs.Debug($"[AudioModelFactory] Registered model class: {id} ({provider.ModelClassName})");
        }
        return modelClass;
    }

    private static string GetShortCode(AudioCategory cat) => cat switch
    {
        AudioCategory.TTS => "TTS",
        AudioCategory.STT => "STT",
        AudioCategory.MusicGen => "Music",
        AudioCategory.VoiceClone => "Clone",
        AudioCategory.AudioFX => "AudFX",
        AudioCategory.SoundFX => "SndFX",
        _ => "Audio"
    };

    /// <summary>SwarmUI's standard placeholder image path.</summary>
    private const string PlaceholderImage = "imgs/model_placeholder.jpg";

    /// <summary>Loads a preview image from Assets/previews/{providerId}.png or falls back to placeholder.</summary>
    private static string LoadPreviewImage(string providerId)
    {
        string fullPath = Path.Combine(ExtensionRoot, "Assets", "previews", $"{providerId}.png");
        if (!File.Exists(fullPath))
        {
            return PlaceholderImage;
        }
        try
        {
            byte[] imageBytes = File.ReadAllBytes(fullPath);
            return $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}";
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioModelFactory] Failed to load preview image {fullPath}: {ex.Message}");
            return PlaceholderImage;
        }
    }
}
