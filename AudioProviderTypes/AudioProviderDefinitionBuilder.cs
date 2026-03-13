using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Fluent builder for creating <see cref="AudioProviderDefinition"/> instances.</summary>
public sealed class AudioProviderDefinitionBuilder
{
    #region Fields

    private string _id = "";
    private string _name = "";
    private AudioCategory _category;
    private string _pythonModule = "";
    private string _pythonEngineClass = "";
    private string _modelPrefix = "";
    private string _modelClassId = "";
    private string _modelClassName = "";
    private readonly List<string> _featureFlags = [];
    private readonly List<PackageDefinition> _dependencies = [];
    private readonly List<AudioModelDefinition> _models = [];
    private string _engineGroup = "default";
    private bool _requiresDocker = false;

    #endregion

    #region Builder Methods

    /// <summary>Sets the unique provider identifier.</summary>
    public AudioProviderDefinitionBuilder WithId(string id) { _id = id; return this; }

    /// <summary>Sets the display name of the provider.</summary>
    public AudioProviderDefinitionBuilder WithName(string name) { _name = name; return this; }

    /// <summary>Sets the audio processing category.</summary>
    public AudioProviderDefinitionBuilder WithCategory(AudioCategory category) { _category = category; return this; }

    /// <summary>Sets the Python engine module and class name.</summary>
    public AudioProviderDefinitionBuilder WithPythonEngine(string module, string className)
    {
        _pythonModule = module;
        _pythonEngineClass = className;
        return this;
    }

    /// <summary>Sets the model name prefix used for routing.</summary>
    public AudioProviderDefinitionBuilder WithModelPrefix(string prefix) { _modelPrefix = prefix; return this; }

    /// <summary>Sets the model class ID and display name for SwarmUI categorization.</summary>
    public AudioProviderDefinitionBuilder WithModelClass(string id, string name)
    {
        _modelClassId = id;
        _modelClassName = name;
        return this;
    }

    /// <summary>Adds a feature flag for parameter visibility control.</summary>
    public AudioProviderDefinitionBuilder AddFeatureFlag(string flag) { _featureFlags.Add(flag); return this; }

    /// <summary>Adds a single package dependency.</summary>
    public AudioProviderDefinitionBuilder AddDependency(PackageDefinition dep) { _dependencies.Add(dep); return this; }

    /// <summary>Adds multiple package dependencies.</summary>
    public AudioProviderDefinitionBuilder AddDependencies(IEnumerable<PackageDefinition> deps) { _dependencies.AddRange(deps); return this; }

    /// <summary>Adds a single model definition.</summary>
    public AudioProviderDefinitionBuilder AddModel(AudioModelDefinition model) { _models.Add(model); return this; }

    /// <summary>Adds multiple model definitions.</summary>
    public AudioProviderDefinitionBuilder AddModels(IEnumerable<AudioModelDefinition> models) { _models.AddRange(models); return this; }

    /// <summary>Sets the engine group for venv/Docker isolation.</summary>
    public AudioProviderDefinitionBuilder WithEngineGroup(string group) { _engineGroup = group; return this; }

    /// <summary>Marks this provider as requiring Docker to run.</summary>
    public AudioProviderDefinitionBuilder WithRequiresDocker() { _requiresDocker = true; return this; }

    #endregion

    #region Build

    /// <summary>Validates required fields and constructs the provider definition.</summary>
    public AudioProviderDefinition Build()
    {
        if (string.IsNullOrEmpty(_id)) throw new InvalidOperationException("Provider ID is required");
        if (string.IsNullOrEmpty(_name)) throw new InvalidOperationException("Provider name is required");
        if (string.IsNullOrEmpty(_pythonModule)) throw new InvalidOperationException("Python module is required");
        if (string.IsNullOrEmpty(_pythonEngineClass)) throw new InvalidOperationException("Python engine class is required");
        if (string.IsNullOrEmpty(_modelPrefix)) throw new InvalidOperationException("Model prefix is required");

        return new AudioProviderDefinition
        {
            Id = _id,
            Name = _name,
            Category = _category,
            PythonModule = _pythonModule,
            PythonEngineClass = _pythonEngineClass,
            ModelPrefix = _modelPrefix,
            ModelClassId = _modelClassId,
            ModelClassName = _modelClassName,
            FeatureFlags = _featureFlags.AsReadOnly(),
            Dependencies = _dependencies.AsReadOnly(),
            Models = _models.AsReadOnly(),
            EngineGroup = _engineGroup,
            RequiresDocker = _requiresDocker
        };
    }

    /// <summary>Creates a new builder instance.</summary>
    public static AudioProviderDefinitionBuilder Create() => new();

    #endregion
}
