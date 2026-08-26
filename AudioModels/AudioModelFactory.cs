using System.IO;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>Factory for creating T2IModel instances from AudioProviderDefinition objects.
/// Mirrors the pattern from SwarmUI-API-Backends/Models/ModelFactory.cs.</summary>
public static class AudioModelFactory
{
    private static readonly Dictionary<string, T2IModelClass> _modelClasses = [];

    /// <summary>Creates a T2IModel from an AudioModelDefinition and AudioProviderDefinition.</summary>
    public static T2IModel Create(AudioModelDefinition model, AudioProviderDefinition provider)
    {
        string fullName = provider.GetFullModelName(model.Id);
        string previewImage = LoadPreviewImage(provider.Id);
        // Use model-level class override if present, otherwise fall back to provider class
        string classId = model.ModelClassId ?? provider.ModelClassId;
        string className = model.ModelClassName ?? provider.ModelClassName;
        T2IModelClass modelClass = GetOrCreateModelClass(classId, className, provider.Category);
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
                ModelClassType = classId,
                Tags = [.. allTags],
                TimeCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TimeModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
    }

    /// <summary>Builds the selector entries for a file-backed provider from artifacts actually on disk.
    ///
    /// <para>Unlike <see cref="CreateAllModels"/> these are not invented: a catalog row appears only when
    /// <see cref="AudioArtifactIndex"/> admitted a validated artifact for it, so an uninstalled or
    /// half-downloaded model has no entry at all. Displayed metadata comes from the scanned file, which is
    /// what makes title/author/license/hash reflect the artifact rather than a hand-written table.</para></summary>
    public static Dictionary<string, T2IModel> ProjectScannedModels(AudioProviderDefinition provider)
    {
        Dictionary<string, T2IModel> models = [];
        foreach (AudioArtifact artifact in AudioArtifactIndex.Admitted.Values)
        {
            if (artifact.ProviderId != provider.Id)
            {
                continue;
            }
            T2IModel scanned = artifact.ScannedModel;
            AudioModelDefinition row = null;
            foreach (AudioModelDefinition candidate in provider.Models)
            {
                if (candidate.Id == artifact.ModelId)
                {
                    row = candidate;
                    break;
                }
            }
            // Keep the scanned object's identity (path, metadata, hash) and only rename it into the
            // "Audio Models/..." namespace the selector and every saved workflow already use.
            // A scanned model with no metadata gets its filename as a title, which is not a name worth showing —
            // fall back to the catalog's until the artifact carries a real one.
            string scannedStem = Path.GetFileNameWithoutExtension(scanned.RawFilePath ?? "");
            bool titleIsFilename = string.IsNullOrEmpty(scanned.Title) || scanned.Title == scannedStem;
            T2IModel projected = new(scanned.Handler, scanned.OriginatingFolderPath, scanned.RawFilePath, artifact.DisplayName)
            {
                Title = titleIsFilename ? row?.Name ?? artifact.ModelId : scanned.Title,
                Description = string.IsNullOrEmpty(scanned.Description) ? row?.Description ?? "" : scanned.Description,
                ModelClass = scanned.ModelClass ?? GetOrCreateModelClass(row?.ModelClassId ?? provider.ModelClassId,
                    row?.ModelClassName ?? provider.ModelClassName, provider.Category),
                StandardWidth = 0,
                StandardHeight = 0,
                IsSupportedModelType = true,
                PreviewImage = string.IsNullOrEmpty(scanned.PreviewImage) || scanned.PreviewImage == PlaceholderImage
                    ? LoadPreviewImage(provider.Id) : scanned.PreviewImage,
                Metadata = scanned.Metadata,
            };
            models[artifact.DisplayName] = projected;
            Logs.Debug($"[AudioModelFactory] Projected file-backed model: {artifact.DisplayName} <- {artifact.ArtifactPath}");
        }
        return models;
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
        => GetOrCreateModelClass(provider.ModelClassId, provider.ModelClassName, provider.Category);

    /// <summary>Gets or creates a T2IModelClass by explicit ID and name. Registers compat class with IsAudioModel = true.</summary>
    public static T2IModelClass GetOrCreateModelClass(string id, string name, AudioCategory category)
    {
        if (!_modelClasses.TryGetValue(id, out T2IModelClass modelClass))
        {
            // Both sorter registries are Dictionary.Add and throw on a duplicate id. Core owns audio classes
            // of its own (ace-step-1_5, minimax-music-3), so adopt an existing class instead of colliding.
            string key = id.ToLowerInvariant();
            if (T2IModelClassSorter.ModelClasses.TryGetValue(key, out T2IModelClass existing))
            {
                _modelClasses[id] = existing;
                Logs.Debug($"[AudioModelFactory] Reusing already-registered model class: {id}");
                return existing;
            }
            if (!T2IModelClassSorter.CompatClasses.TryGetValue(key, out T2IModelCompatClass compat))
            {
                compat = T2IModelClassSorter.RegisterCompat(new()
                {
                    ID = id,
                    ShortCode = GetShortCode(category),
                    IsAudioModel = true
                });
            }
            modelClass = new T2IModelClass
            {
                ID = id,
                Name = name,
                CompatClass = compat,
                StandardWidth = 0,
                StandardHeight = 0,
                // Deliberately never matches by heuristic: a scanned audio artifact is classified from its
                // embedded modelspec.architecture, which the sorter checks before it consults any predicate.
                // A predicate here would only let one audio class steal another's files.
                IsThisModelOfClass = (model, header) => false
            };
            _modelClasses[id] = modelClass;
            T2IModelClassSorter.Register(modelClass);
            Logs.Debug($"[AudioModelFactory] Registered model class: {id} ({name})");
        }
        return modelClass;
    }

    private static string GetShortCode(AudioCategory cat) => cat switch
    {
        AudioCategory.TTS => "TTS",
        AudioCategory.STT => "STT",
        AudioCategory.AudioGeneration => "Gen",
        AudioCategory.VoiceConversion => "Clone",
        AudioCategory.AudioProcessing => "Proc",
        _ => "Audio"
    };

    /// <summary>SwarmUI's standard placeholder image path.</summary>
    private const string PlaceholderImage = "imgs/model_placeholder.jpg";

    /// <summary>Loads a preview image from Assets/previews/{providerId}.png or falls back to placeholder.</summary>
    private static string LoadPreviewImage(string providerId)
    {
        // AudioConfiguration.ExtensionDirectory is absolute (resolved in OnPreInit); the old relative
        // "src/Extensions/SwarmUI-AudioLab" only resolved when the process CWD happened to be the Swarm root.
        string fullPath = Path.Combine(AudioConfiguration.ExtensionDirectory, "Assets", "previews", $"{providerId}.png");
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
