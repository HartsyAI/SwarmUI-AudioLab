using Hartsy.Extensions.AudioLab.WebAPI.Models;

namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Fluent builder for creating <see cref="AudioProviderDefinition"/> instances.</summary>
public sealed class AudioProviderDefinitionBuilder
{
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

    public AudioProviderDefinitionBuilder WithId(string id) { _id = id; return this; }
    public AudioProviderDefinitionBuilder WithName(string name) { _name = name; return this; }
    public AudioProviderDefinitionBuilder WithCategory(AudioCategory category) { _category = category; return this; }
    public AudioProviderDefinitionBuilder WithPythonEngine(string module, string className)
    {
        _pythonModule = module;
        _pythonEngineClass = className;
        return this;
    }
    public AudioProviderDefinitionBuilder WithModelPrefix(string prefix) { _modelPrefix = prefix; return this; }
    public AudioProviderDefinitionBuilder WithModelClass(string id, string name)
    {
        _modelClassId = id;
        _modelClassName = name;
        return this;
    }
    public AudioProviderDefinitionBuilder AddFeatureFlag(string flag) { _featureFlags.Add(flag); return this; }
    public AudioProviderDefinitionBuilder AddDependency(PackageDefinition dep) { _dependencies.Add(dep); return this; }
    public AudioProviderDefinitionBuilder AddDependencies(IEnumerable<PackageDefinition> deps) { _dependencies.AddRange(deps); return this; }
    public AudioProviderDefinitionBuilder AddModel(AudioModelDefinition model) { _models.Add(model); return this; }
    public AudioProviderDefinitionBuilder AddModels(IEnumerable<AudioModelDefinition> models) { _models.AddRange(models); return this; }

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
            Models = _models.AsReadOnly()
        };
    }

    /// <summary>Creates a new builder instance.</summary>
    public static AudioProviderDefinitionBuilder Create() => new();
}
