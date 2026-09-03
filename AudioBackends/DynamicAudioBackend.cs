using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.AudioLab.AudioModels;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices;
using Hartsy.Extensions.AudioLab.WebAPI.Models;
using HartsyInference.Audio.Streaming;
using HartsyInference.Cuda;
using HartsyInference.Engine;
using HartsyInference.Vulkan;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.AudioLab.AudioBackends;

/// <summary>A single routing backend for all AudioLab engines.
/// Engines are installed on-demand via the backend UI — only installed engines
/// get their models registered into the model browser. Model prefix matching
/// routes generation requests to the correct provider's Python engine.</summary>
public class DynamicAudioBackend : AbstractT2IBackend
{
    #region Static Initialization

    /// <summary>Static constructor to register our model provider with ModelsAPI.
    /// Mirrors DynamicAPIBackend static constructor pattern.</summary>
    static DynamicAudioBackend()
    {
        ModelsAPI.ExtraModelProviders["audiolab"] = GetAudioModels;
    }

    /// <summary>Static method to provide audio models from all DynamicAudioBackend instances.</summary>
    private static Dictionary<string, JObject> GetAudioModels(string subtype)
    {
        IEnumerable<DynamicAudioBackend> audioBackends = Program.Backends.RunningBackendsOfType<DynamicAudioBackend>()
            .Where(b => b.RemoteModels != null);
        if (subtype is "Stable-Diffusion" || string.IsNullOrEmpty(subtype))
        {
            Dictionary<string, JObject> result = [];
            foreach (DynamicAudioBackend backend in audioBackends)
            {
                if (backend.RemoteModels.TryGetValue("Stable-Diffusion", out Dictionary<string, JObject> models))
                {
                    foreach (KeyValuePair<string, JObject> kvp in models)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                }
            }
            Logs.Verbose($"[AudioLab] Returned {result.Count} audio models for subtype: {subtype}");
            return result;
        }
        return [];
    }

    #endregion

    #region Settings and Fields

    /// <summary>Settings for the dynamic audio backend.</summary>
    public class DynamicAudioSettings : AutoConfiguration
    {
        [ConfigComment("Which compute device audio models run on.\nThe list comes from the engine itself, so every compute backend it supports (CPU, CUDA, Vulkan, and whatever it gains later) shows up here, with one entry per GPU where the devices can be enumerated.\n'Auto' picks the best available, and is the right answer unless you are deliberately steering audio off a card another backend is using.\nGPU numbering is the engine's own enumeration (CUDA is fastest-first), which need not match nvidia-smi's order.\nCUDA entries only appear when a driver is present; Vulkan cannot be probed ahead of time, so a missing Vulkan driver only shows up when this backend starts.\nAudio shares one engine instance process-wide, so this is not really a per-backend choice: whichever audio backend initializes last before the first audio generation picks the device. Once audio has run, changing this needs a SwarmUI restart.\nRun one audio backend unless you know why you want two.")]
        [SettingsOptions(Impl = typeof(AudioDeviceOptions))]
        public string Device = "auto";

        [ConfigComment("How hard the engine should work to fit audio models in VRAM.\n\n'Auto' (default) reads the card's size for a starting posture, then measures free VRAM before each stage. Right for almost everyone.\n\n'Performance' never frees between stages — fastest back-to-back, but a large model (YuE's 7B, MiniMax Music) can run the card out of memory.\n\n'Balanced' releases each stage's weights at its boundary, which is what lets a multi-stage model (YuE Stage-1 → Stage-2 → vocoder) fit a smaller card.\n\n'Aggressive' also halves cross-step cache precision and shrinks decode chunks — the lever that matters for vocoders and codec decoders, where the peak is activations rather than weights.\n\n'Maximum' adds quantized compute and frees after every generation. Slowest, and quantized compute changes the output.\n\nLike the Device setting, audio shares ONE engine process-wide: whichever audio backend initializes last before the first audio generation wins, and changing this afterwards needs a SwarmUI restart. AUDIOLAB_VRAM_MODE overrides it for headless runs.")]
        [ManualSettingsOptions(Impl = null, Vals = ["Auto", "Performance", "Balanced", "Aggressive", "Maximum"],
            ManualNames = ["Auto (recommended)", "Performance (never free between stages)", "Balanced (free between stages)",
                "Aggressive (smaller chunks, half-precision caches)", "Maximum (every lever, changes output)"])]
        public string VramMode = "Auto";
    }

    /// <summary>Builds the Device dropdown from whatever compute backends the engine reports
    /// (<see cref="BackendFactory.ValidSelectors"/>) rather than a hardcoded list, so a backend the engine gains
    /// later shows up here with no change to this file. Kinds that take a device ordinal are expanded per
    /// device where they can be enumerated. Evaluated once, when the backend type is registered at startup.</summary>
    public class AudioDeviceOptions : SettingsOptionsAttribute.AbstractImpl
    {
        // Cached: GetOptions and Names are queried separately, and two probes could disagree and misalign
        // the value/label arrays. Also keeps startup to one driver query.
        private static readonly Lazy<(string[] Vals, string[] Names)> _options = new(Build);

        /// <summary>Friendly labels for the kinds we know about today. Anything the engine adds later falls
        /// back to its own token, so an unknown kind is still selectable, just plainly named.</summary>
        private static readonly Dictionary<string, string> KindLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = "Auto (best available)",
            ["cpu"] = "CPU only (very slow)",
            ["cuda"] = "CUDA (NVIDIA GPU)",
            ["vulkan"] = "Vulkan (any GPU)",
        };

        private static (string[] Vals, string[] Names) Build()
        {
            List<string> vals = [];
            List<string> names = [];
            foreach (string kind in Guard(() => BackendFactory.ValidSelectors.ToList(), "backend list") ?? [])
            {
                // 'auto' accepts an ordinal too, but offering a device for a backend that hasn't been chosen
                // yet is not a useful thing to put in front of a user.
                bool perDevice = !kind.Equals("auto", StringComparison.OrdinalIgnoreCase) && TakesDeviceOrdinal(kind);
                List<(int Ordinal, string Label)> devices = perDevice ? DevicesFor(kind) : [];
                if (devices.Count == 0)
                {
                    AddIfUsable(vals, names, kind, KindLabels.TryGetValue(kind, out string label) ? label : kind);
                    continue;
                }
                foreach ((int ordinal, string deviceLabel) in devices)
                {
                    AddIfUsable(vals, names, $"{kind}:{ordinal}", deviceLabel);
                }
            }
            if (vals.Count == 0)
            {
                // Nothing validated (no engine, no driver). 'auto' always resolves, so never ship an empty list.
                vals.Add("auto");
                names.Add(KindLabels["auto"]);
            }
            return ([.. vals], [.. names]);
        }

        /// <summary>Adds a selector only if the engine agrees it could be built here, which is how a CUDA entry
        /// disappears on a machine with no NVIDIA driver. Vulkan has no cheap availability probe engine-side, so
        /// it always passes and a missing driver surfaces when the backend starts.</summary>
        private static void AddIfUsable(List<string> vals, List<string> names, string selector, string label)
        {
            if (Guard(() => { BackendFactory.Validate(selector); return true; }, $"validate '{selector}'"))
            {
                vals.Add(selector);
                names.Add(label);
            }
        }

        /// <summary>Whether this backend kind selects a device, asked of the engine rather than assumed, so a
        /// future device backend is expanded per device automatically. Its own quiet try/catch: a throw here is
        /// the answer "no" (as CPU gives every startup), not a fault worth logging.</summary>
        private static bool TakesDeviceOrdinal(string kind)
        {
            try
            {
                BackendFactory.WithOrdinal(kind, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Enumerates the devices of one backend kind, as (ordinal, display label). Empty when the kind
        /// has no enumeration path, in which case the caller offers the bare kind and lets the engine pick.</summary>
        private static List<(int, string)> DevicesFor(string kind) => kind.ToLowerInvariant() switch
        {
            "cuda" => Guard(ProbeCuda, "CUDA device probe") ?? [],
            "vulkan" => Guard(ProbeVulkan, "Vulkan device probe") ?? [],
            _ => [],
        };

        /// <summary>CUDA ordinals come from the engine's own enumeration, so they match what the selector means
        /// (fastest-first), not nvidia-smi's PCI order.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<(int, string)> ProbeCuda()
            => [.. CudaTopology.Probe().Select(g => (g.Ordinal,
                g.TotalMemoryBytes > 0
                    ? $"GPU {g.Ordinal}: {g.Name} ({g.TotalMemoryBytes / 1073741824.0:0.#} GB)"
                    : $"GPU {g.Ordinal}: {g.Name}"))];

        /// <summary>Vulkan has no cheap topology query, so each physical device is opened briefly to read its
        /// name. Guarded per device: one that refuses to open costs its label, not the whole list.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<(int, string)> ProbeVulkan()
        {
            using VulkanInstance instance = new();
            int count = instance.EnumeratePhysicalDevices().Length;
            List<(int, string)> devices = [];
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                int index = ordinal;
                devices.Add((index, Guard(() => DescribeVulkanDevice(instance, index), $"Vulkan device {index}")
                    ?? $"Vulkan device {index}"));
            }
            return devices;
        }

        /// <summary>Label for one Vulkan physical device. Software rasterizers (llvmpipe and friends) enumerate
        /// as ordinary Vulkan devices, so they are named as such: picking one silently makes audio glacial.</summary>
        private static string DescribeVulkanDevice(VulkanInstance instance, int ordinal)
        {
            using VulkanDevice device = VulkanDevice.Create(instance, ordinal);
            VulkanCapabilities caps = device.Capabilities;
            string software = caps.DeviceType == VkPhysicalDeviceType.Cpu ? ", software" : "";
            return $"Vulkan {ordinal}: {caps.DeviceName} ({caps.TotalVramBytes / 1073741824.0:0.#} GB{software})";
        }

        /// <summary>Runs an engine query that must never take the whole backend registration down with it, and
        /// which may touch an assembly or driver that isn't present. Returns default on failure.</summary>
        private static T Guard<T>(Func<T> probe, string what)
        {
            try
            {
                return probe();
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab] Device dropdown: {what} failed ({ex.Message}).");
                return default;
            }
        }

        public override string[] GetOptions => _options.Value.Vals;

        public override string[] Names => _options.Value.Names;
    }

    /// <summary>Maps AudioCategory enum to category-level feature flag names.</summary>
    private static readonly Dictionary<AudioCategory, string> CategoryFlags = new()
    {
        [AudioCategory.TTS] = "audiolab_tts",
        [AudioCategory.STT] = "audiolab_stt",
        [AudioCategory.AudioGeneration] = "audiolab_audiogen",
        [AudioCategory.VoiceConversion] = "audiolab_clone",
        [AudioCategory.AudioProcessing] = "audiolab_audioproc",
    };

    /// <summary>Runtime state for initialized providers, keyed by provider ID. Concurrent: reached from Init,
    /// install/uninstall, reconcile, the background redownloads and every generation — the same unsynchronized
    /// access that previously corrupted RegisteredAudioModels and aborted the process.</summary>
    private readonly ConcurrentDictionary<string, AudioProviderMetadata> _providers = new(StringComparer.Ordinal);

    /// <summary>Supported feature flags, populated in Init(). Concurrent for the same reason as
    /// <see cref="_providers"/>: SupportedFeatures is enumerated by the API thread while installs rebuild it.</summary>
    private readonly ConcurrentDictionary<string, byte> _supportedFeatureSet = new(StringComparer.Ordinal);

    /// <summary>Current settings accessor.</summary>
    public DynamicAudioSettings Settings => SettingsRaw as DynamicAudioSettings;

    /// <summary>Feature flags from all enabled providers.</summary>
    public override IEnumerable<string> SupportedFeatures => _supportedFeatureSet.Keys;

    /// <summary>Dictionary of remote models this backend provides, by type.</summary>
    public Dictionary<string, Dictionary<string, JObject>> RemoteModels { get; set; } = [];

    /// <summary>Collection of all registered models, keyed by model name.</summary>
    private Dictionary<string, T2IModel> RegisteredAudioModels { get; set; } = [];

    /// <summary>Guards <see cref="RemoteModels"/> and <see cref="RegisteredAudioModels"/>. Both are plain
    /// Dictionaries reached concurrently from engine install, restart-all, and the background redownloads;
    /// unsynchronized writes corrupted the Dictionary and aborted the process.</summary>
    private readonly object _modelsLock = new();

    /// <summary>Set of installed engine provider IDs, persisted to JSON config. Mutated by install/uninstall
    /// and read by reconcile, delete and the API thread, so every access goes through <see cref="_modelsLock"/>
    /// and every enumeration takes a snapshot — iterating it live while an install added to it threw.</summary>
    private HashSet<string> InstalledEngines { get; set; } = [];

    /// <summary>Provider IDs with an auto-redownload currently in flight — guards the startup (reconcile) and
    /// on-demand (generation) redownload paths from racing/duplicating a fetch for the same engine.</summary>
    private readonly ConcurrentDictionary<string, byte> _redownloading = new(StringComparer.Ordinal);

    /// <summary>Path to the installed engines config file.</summary>
    private static string InstalledEnginesConfigPath => Path.Combine(Program.DataDir, "AudioLabInstalledEngines.json");

    /// <summary>VRAM headroom kept free on top of the incoming model's own estimate, covering activations,
    /// the codec/vocoder stage and pool fragmentation. Measured: a 90s ACE-Step XL run peaks roughly 2GB above
    /// the checkpoint itself.</summary>
    private const long VramSafetyMarginBytes = 2L * 1024 * 1024 * 1024;

    #endregion

    #region Initialization

    /// <summary>Initializes the backend with default settings and LOADING status.</summary>
    public DynamicAudioBackend()
    {
        SettingsRaw = new DynamicAudioSettings();
        Status = BackendStatus.LOADING;
    }

    /// <summary>Pushes the configured compute device to the shared engine, failing the backend loudly on a bad
    /// selector instead of letting it die mid-generation. Returns false when the backend was set to ERRORED.</summary>
    private bool ApplyDeviceSetting()
    {
        string device = string.IsNullOrWhiteSpace(Settings?.Device) ? "auto" : Settings.Device.Trim();
        try
        {
            BackendFactory.Validate(device);
        }
        catch (Exception ex)
        {
            Status = BackendStatus.ERRORED;
            AddLoadStatus($"Device '{device}' cannot be used: {ex.Message}");
            Logs.Error($"[AudioLab] Audio backend device '{device}' is unusable: {ex.Message}");
            return false;
        }
        string inUse = AudioEngineBridge.RequestDevice(device);
        if (inUse is not null)
        {
            // The engine is a process-wide singleton, already built by an earlier generation, so it cannot be
            // retargeted now. Say so rather than silently running on the wrong card.
            AddLoadStatus($"Audio is already running on '{inUse}', so '{device}' will not take effect until SwarmUI restarts.");
            Logs.Warning($"[AudioLab] Audio engine already built for '{inUse}', ignoring requested device '{device}'. Restart SwarmUI to change it.");
        }
        string vramMode = Settings?.VramMode;
        string vramInUse = AudioEngineBridge.RequestVramMode(vramMode);
        if (vramInUse is not null)
        {
            AddLoadStatus($"Audio is already running in VRAM mode '{vramInUse}', so '{vramMode}' will not take effect until SwarmUI restarts.");
            Logs.Warning($"[AudioLab] Audio engine already built with VRAM mode '{vramInUse}', ignoring '{vramMode}'. Restart SwarmUI to change it.");
        }
        return true;
    }

    /// <summary>Initializes the backend: syncs the model root, applies the device setting, loads the installed
    /// engines config and registers their models. Nothing is launched, since generation runs in-process.</summary>
    public override async Task Init()
    {
        Status = BackendStatus.LOADING;
        Models = new ConcurrentDictionary<string, List<string>>();
        _supportedFeatureSet.Clear();
        _providers.Clear();
        lock (_modelsLock)
        {
            RegisteredAudioModels.Clear();
            RemoteModels.Clear();
        }
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;
        Program.ModelPathsChangedEvent -= ReRegisterModelsAfterPathChange;

        // Re-read on every init so restarting this backend picks up a changed server ModelRoot.
        AudioConfiguration.SyncModelRootFromServer();

        if (!ApplyDeviceSetting())
        {
            return;
        }

        LoadInstalledEnginesConfig();

        // Before the provider loop: a file-backed provider projects from this index, so an empty one would
        // register zero models and leave the backend's own model list empty until some later refresh.
        AudioArtifactIndex.Rebuild();

        try
        {
            foreach (string providerId in InstalledEnginesSnapshot())
            {
                AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
                if (definition == null)
                {
                    Logs.Warning($"[AudioLab] Installed provider '{providerId}' not found in registry, skipping.");
                    continue;
                }

                // Engines with no C# implementation, and every cloud API engine (all untested, so all held
                // back), can't run in this build. Skip a stale saved install rather than register a provider
                // that would fail on use.
                if (definition.NotImplemented || definition.IsApiProvider)
                {
                    string why = definition.NotImplemented ? "not built in the C# engine yet" : "a cloud API engine, currently disabled";
                    Logs.Warning($"[AudioLab] Skipping installed provider {definition.Name}: {why}.");
                    continue;
                }

                AudioProviderMetadata meta = new()
                {
                    Definition = definition,
                    IsEnabled = true
                };
                _providers[providerId] = meta;

                RegisterModelsForProvider(definition);

                if (CategoryFlags.TryGetValue(definition.Category, out string categoryFlag))
                {
                    _supportedFeatureSet.TryAdd(categoryFlag, 0);
                }
                if (definition.Category != AudioCategory.STT)
                {
                    _supportedFeatureSet.TryAdd("audiolab_output", 0);
                }
                foreach (string flag in definition.FeatureFlags)
                {
                    _supportedFeatureSet.TryAdd(flag, 0);
                }

                Logs.Debug($"[AudioLab] Loaded installed provider: {definition.Name} ({providerId})");
            }

            if (_providers.Count > 0)
            {
                UpdateRemoteModels();
            }
            ReconcileWeights();
            Program.ModelRefreshEvent += ReRegisterModelsAfterRefresh;
            Program.ModelPathsChangedEvent += ReRegisterModelsAfterPathChange;

            // Nothing to start — local inference is delegated to HartsyInference.Engine (via
            // AudioEngineBridge) and cloud providers to their API handlers. The backend is ready as soon
            // as installed engines' models are registered.

            Status = BackendStatus.RUNNING;
            if (_providers.Count > 0)
            {
                Logs.Info($"[AudioLab] Audio backend initialized with {_providers.Count} installed engine(s), " +
                          $"{RegisteredAudioModels.Count} model(s): {string.Join(", ", _providers.Keys)}");
            }
            else
            {
                Logs.Info("[AudioLab] Audio backend initialized. No engines installed yet. Use the backend settings to install engines.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Audio backend initialization failed: {ex}");
            Status = BackendStatus.ERRORED;
        }
    }

    #endregion

    #region Model Registration

    /// <summary>Registers models for a specific provider into MainSDModels.
    /// Mirrors DynamicAPIBackend.RegisterModelsForProvider().</summary>
    private void RegisterModelsForProvider(AudioProviderDefinition provider)
    {
        Dictionary<string, T2IModel> models = provider.FileBacked
            ? AudioModelFactory.ProjectScannedModels(provider)
            : AudioModelFactory.CreateAllModels(provider);
        List<string> modelNames = [];
        foreach (KeyValuePair<string, T2IModel> kvp in models)
        {
            string name = kvp.Key;
            T2IModel model = kvp.Value;
            model.Handler = Program.MainSDModels;
            modelNames.Add(name);
            if (!Program.MainSDModels.Models.ContainsKey(name))
            {
                Program.MainSDModels.Models[name] = model;
                Logs.Debug($"[AudioLab] Added model to MainSDModels: {name}");
            }
            lock (_modelsLock)
            {
                RegisteredAudioModels[name] = model;
            }
        }
        if (Models.TryGetValue("Stable-Diffusion", out List<string> existingModels))
        {
            existingModels.AddRange(modelNames);
        }
        else
        {
            Models.TryAdd("Stable-Diffusion", modelNames);
        }
    }

    /// <summary>Re-projects after a model-path settings save.
    ///
    /// <para>That path does NOT fire <see cref="Program.ModelRefreshEvent"/>: core calls BuildModelLists (which
    /// replaces MainSDModels with a brand-new handler) then RefreshAllModelSets directly, and only then raises
    /// this event. Without this hook every audio model silently disappears until the next manual refresh, since
    /// the entries were re-injected into a handler that has since been thrown away.</para></summary>
    private void ReRegisterModelsAfterPathChange()
    {
        AudioConfiguration.SyncModelRootFromServer();
        ReRegisterModelsAfterRefresh();
    }

    /// <summary>Re-scans disk and rebuilds the entries of every file-backed provider, so a model that was just
    /// installed appears and one whose files were deleted goes away.
    ///
    /// <para>The "Audio" handler subscribes its own Refresh to <see cref="Program.ModelRefreshEvent"/> when it
    /// is registered in OnInit, which is before this backend exists — so by the time this runs on that event,
    /// the disk scan it reads has already completed.</para></summary>
    private void RefreshFileBackedModels()
    {
        List<AudioProviderDefinition> fileBacked = [];
        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            if (meta.Definition.FileBacked)
            {
                fileBacked.Add(meta.Definition);
            }
        }
        if (fileBacked.Count == 0)
        {
            return;
        }
        AudioArtifactIndex.Rebuild();
        foreach (AudioProviderDefinition provider in fileBacked)
        {
            Dictionary<string, T2IModel> projected = AudioModelFactory.ProjectScannedModels(provider);
            lock (_modelsLock)
            {
                string prefix = $"Audio Models/{provider.ModelPrefix}/";
                foreach (string stale in RegisteredAudioModels.Keys.Where(k => k.StartsWith(prefix)).ToList())
                {
                    if (!projected.ContainsKey(stale))
                    {
                        RegisteredAudioModels.Remove(stale);
                        Program.MainSDModels.Models.Remove(stale, out _);
                    }
                }
                foreach (KeyValuePair<string, T2IModel> kvp in projected)
                {
                    RegisteredAudioModels[kvp.Key] = kvp.Value;
                    kvp.Value.Handler = Program.MainSDModels;
                }
                // The backend's own list is what Swarm's backend matching consults — a model that reaches
                // MainSDModels but not here is visible in the selector and unroutable.
                List<string> names = Models.GetOrCreate("Stable-Diffusion", () => []);
                names.RemoveAll(n => n.StartsWith(prefix) && !projected.ContainsKey(n));
                foreach (string name in projected.Keys)
                {
                    if (!names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
    }

    /// <summary>Publishes registered models to RemoteModels for ExtraModelProviders.
    /// Mirrors DynamicAPIBackend.UpdateRemoteModels().</summary>
    private void UpdateRemoteModels()
    {
        // Re-project first: publishing from the pre-projection snapshot would leave every file-backed model
        // out of RemoteModels until the next install or uninstall happened to rebuild it.
        ReRegisterModelsAfterRefresh();
        int published;
        lock (_modelsLock)
        {
            if (!RegisteredAudioModels.Any())
            {
                Logs.Warning("[AudioLab] No registered audio models to publish");
                return;
            }
            Dictionary<string, JObject> remoteSD = RemoteModels.GetOrCreate("Stable-Diffusion", () => []);
            remoteSD.Clear();
            foreach (KeyValuePair<string, T2IModel> kvp in RegisteredAudioModels)
            {
                remoteSD[kvp.Key] = CreateModelMetadata(kvp.Value, kvp.Key);
            }
            published = remoteSD.Count;
        }
        Logs.Verbose($"[AudioLab] Published {published} audio models to RemoteModels");
    }

    /// <summary>Re-registers audio models into MainSDModels.Models after a filesystem refresh wipes them.
    /// Mirrors DynamicAPIBackend.ReRegisterModelsAfterRefresh().</summary>
    private void ReRegisterModelsAfterRefresh()
    {
        if (Status is not BackendStatus.RUNNING and not BackendStatus.LOADING)
        {
            return;
        }
        RefreshFileBackedModels();
        int added = 0;
        // Also reached directly via Program.ModelRefreshEvent, not just from UpdateRemoteModels.
        lock (_modelsLock)
        {
            foreach (KeyValuePair<string, T2IModel> kvp in RegisteredAudioModels)
            {
                if (!Program.MainSDModels.Models.ContainsKey(kvp.Key))
                {
                    Program.MainSDModels.Models[kvp.Key] = kvp.Value;
                    added++;
                }
            }
        }
        if (added > 0)
        {
            Logs.Verbose($"[AudioLab] Re-registered {added} audio models into MainSDModels after refresh");
        }
        ReconcileWeights();
    }

    /// <summary>Creates metadata JObject for a model, for RemoteModels publishing.</summary>
    private JObject CreateModelMetadata(T2IModel model, string modelName)
    {
        return new JObject
        {
            ["name"] = modelName,
            ["title"] = model.Title ?? modelName,
            ["description"] = model.Description ?? "AudioLab model",
            ["preview_image"] = model.PreviewImage ?? "",
            ["loaded"] = true,
            ["architecture"] = model.ModelClass?.ID ?? "audiolab",
            ["class"] = model.ModelClass?.Name ?? "AudioLab",
            ["compat_class"] = model.ModelClass?.CompatClass.ID ?? "audiolab",
            ["standard_width"] = 0,
            ["standard_height"] = 0,
            ["is_supported_model_format"] = true,
            ["is_audio_model"] = true,
            ["local"] = false,
            ["api_source"] = "audiolab",
            ["license"] = model.Metadata?.License ?? ""
        };
    }

    #endregion

    #region Generation

    /// <summary>Generate with live output. Yields AudioFile objects via takeOutput.
    /// Routes to streaming or normal path based on StreamAudio param.
    /// Mirrors DynamicAPIBackend.GenerateLive().</summary>
    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        string modelName = user_input.Get(T2IParamTypes.Model)?.Name ?? "";
        string providerId = GetProviderIdFromModel(modelName);

        if (providerId == null || !_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            Logs.Error($"[AudioLab] No provider found for model: {modelName}");
            return;
        }

        AudioProviderDefinition provider = meta.Definition;
        AudioModelDefinition modelDef = GetModelDefinition(modelName, provider);

        // No weights means not installed — reset it and say so. Engine-managed providers fetch lazily inside
        // their own loader, so their on-disk state before a first generation says nothing.
        if (!provider.IsApiProvider && !AudioEngineBridge.ProviderManagesOwnWeights(provider.Id)
            && !AudioEngineBridge.WeightsPresent(provider.Id, modelDef?.Id))
        {
            Logs.Debug($"[AudioLab] '{provider.Name}' has no weights on disk, resetting it to not-installed.");
            UnregisterEngine(provider.Id, deleteWeights: false);
            takeOutput(new JObject
            {
                ["error"] = $"{provider.Name} is not installed: its weights are not on disk. Install it from the Audio backend settings and try again."
            });
            return;
        }

        if (provider.Category == AudioCategory.TTS
            && user_input.TryGet(AudioLabParams.StreamChunkSize, out string chunkMode) && chunkMode != "off")
        {
            // Engine-native streaming (real incremental generation, chunks arrive as the model produces them —
            // see AudioEngineBridge.SupportsNativeStreaming) takes priority over AudioLab's own text-chunk-and-
            // regenerate-each-piece loop below. StreamChunkSize's "off" switch still applies to both paths; a
            // native-streaming model just doesn't need text splitting to get the "off" behavior's opposite.
            if (AudioEngineBridge.SupportsNativeStreaming(provider.Id))
            {
                await GenerateLiveNativeStreaming(user_input, batchId, takeOutput, meta, provider, modelDef);
                return;
            }
            string text = user_input.Get(T2IParamTypes.Prompt, "");
            List<string> chunks = SplitIntoChunks(text, chunkMode);
            if (chunks != null)
            {
                await GenerateLiveStreaming(user_input, batchId, takeOutput, meta, provider, modelDef, chunks);
                return;
            }
        }

        await GenerateLiveNormal(user_input, batchId, takeOutput, meta, provider, modelDef);
    }

    /// <summary>Normal (non-streaming) generation path — original behavior.</summary>
    private async Task GenerateLiveNormal(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef)
    {
        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["step"] = 0,
                ["total_steps"] = 1
            }
        });

        Dictionary<string, object> args = BuildEngineArgs(user_input, provider, modelDef);

        try
        {
            JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.SourceSession?.User, user_input.InterruptToken);

            if (result["cancelled"]?.Value<bool>() == true)
            {
                Logs.Info($"[AudioLab] Generation cancelled for {provider.Name}");
                return;
            }

            if (result["success"]?.Value<bool>() == true)
            {
                string audioBase64 = result["audio_data"]?.ToString();
                if (!string.IsNullOrEmpty(audioBase64))
                {
                    byte[] audioBytes = Convert.FromBase64String(audioBase64);
                    MediaType mediaType = (result["output_format"]?.ToString()) switch
                    {
                        "mp3" => MediaType.AudioMp3,
                        "flac" => MediaType.AudioFlac,
                        "ogg" => MediaType.AudioOgg,
                        _ => MediaType.AudioWav,
                    };
                    AudioFile audio = new(audioBytes, mediaType);
                    takeOutput(audio);
                }

                // For STT, output the transcription text and a placeholder audio
                if (provider.Category == AudioCategory.STT)
                {
                    string transcription = result["text"]?.ToString() ?? "";
                    Logs.Info($"[AudioLab] STT transcription via {provider.Name}: {transcription}");
                    takeOutput(new JObject
                    {
                        ["gen_progress"] = new JObject
                        {
                            ["current_status"] = $"Transcription: {transcription}"
                        }
                    });
                    // STT produces no audio, but the pipeline requires at least one output.
                    // Generate a minimal silent WAV so the generation isn't treated as a failure.
                    if (string.IsNullOrEmpty(audioBase64))
                    {
                        byte[] silentWav = GenerateSilentWav(sampleRate: 16000, durationMs: 100);
                        takeOutput(new AudioFile(silentWav, MediaType.AudioWav));
                    }
                }

                meta.LastUsed = DateTime.UtcNow;

                if (provider.Category == AudioCategory.TTS)
                {
                    double duration = result["duration"]?.Value<double>() ?? 0;
                    Logs.Info($"[AudioLab] TTS generated {duration:F2}s of audio via {provider.Name}");
                }
            }
            else
            {
                string error = result["error"]?.ToString() ?? "Unknown error";
                Logs.Error($"[AudioLab] Provider {provider.Name} failed: {error}");
                meta.LastError = error;
                throw new SwarmReadableErrorException($"[AudioLab] {provider.Name}: {error}");
            }
        }
        catch (OperationCanceledException) when (user_input.InterruptToken.IsCancellationRequested)
        {
            Logs.Info($"[AudioLab] Generation cancelled for {provider.Name}");
        }
        catch (Exception ex) when (ex is not SwarmReadableErrorException)
        {
            // Record then rethrow — swallowing here made every non-streaming failure look like an empty success.
            Logs.Error($"[AudioLab] Error processing with {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
            throw;
        }
    }

    /// <summary>Streaming generation path — generates each text chunk separately,
    /// sends intermediate audio chunks for immediate playback, then concatenates
    /// all PCM data into a final WAV file as the real output.</summary>
    private async Task GenerateLiveStreaming(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef, List<string> chunks)
    {
        Logs.Info($"[AudioLab] Streaming TTS: {chunks.Count} chunks via {provider.Name}");

        user_input.Set(T2IParamTypes.DoNotSaveIntermediates, true);

        List<byte[]> pcmChunks = [];
        int sampleRate = 24000;
        int channels = 1;
        int bitsPerSample = 16;
        bool formatRead = false;
        double totalDuration = 0;
        int consecutiveFailures = 0;
        string firstError = null;

        for (int i = 0; i < chunks.Count; i++)
        {
            if (user_input.InterruptToken.IsCancellationRequested)
            {
                Logs.Info($"[AudioLab] Streaming cancelled after {i}/{chunks.Count} chunks for {provider.Name}");
                break;
            }

            double overallPercent = (double)i / chunks.Count * 100;
            takeOutput(new JObject
            {
                ["gen_progress"] = new JObject
                {
                    ["batch_index"] = batchId,
                    ["overall_percent"] = overallPercent,
                    ["current_status"] = $"Generating chunk {i + 1}/{chunks.Count}..."
                }
            });

            T2IParamInput chunkInput = user_input.Clone();
            chunkInput.Set(T2IParamTypes.Prompt, chunks[i]);
            Dictionary<string, object> args = BuildEngineArgs(chunkInput, provider, modelDef);

            try
            {
                JObject result = await AudioServerManager.Instance.ProcessAsync(provider, args, user_input.SourceSession?.User, user_input.InterruptToken);

                if (result["cancelled"]?.Value<bool>() == true)
                {
                    Logs.Info($"[AudioLab] Streaming chunk cancelled for {provider.Name}");
                    break;
                }

                if (result["success"]?.Value<bool>() == true)
                {
                    string audioBase64 = result["audio_data"]?.ToString();
                    if (!string.IsNullOrEmpty(audioBase64))
                    {
                        byte[] audioBytes = Convert.FromBase64String(audioBase64);

                        if (!formatRead)
                        {
                            (sampleRate, channels, bitsPerSample) = AudioIo.ReadWavFormat(audioBytes);
                            formatRead = true;
                        }

                        pcmChunks.Add(AudioIo.StripWavHeader(audioBytes));

                        AudioFile chunkAudio = new(audioBytes, MediaType.AudioWav);
                        takeOutput(new T2IEngine.ImageOutput { File = chunkAudio, IsReal = false });

                        double chunkDuration = result["duration"]?.Value<double>() ?? 0;
                        totalDuration += chunkDuration;
                        Logs.Debug($"[AudioLab] Streamed chunk {i + 1}/{chunks.Count}: {chunkDuration:F2}s");
                    }
                    consecutiveFailures = 0;
                }
                else
                {
                    string error = result["error"]?.ToString() ?? "Unknown error";
                    firstError ??= error;
                    Logs.Warning($"[AudioLab] Chunk {i + 1} failed: {error}");
                    // Abort early on missing dependencies — all subsequent chunks will fail identically
                    if (error.Contains("No module named") || error.Contains("ModuleNotFoundError"))
                    {
                        Logs.Error($"[AudioLab] Missing Python dependency for {provider.Name}: {error}. Install provider dependencies via the AudioLab UI before generating audio.");
                        meta.LastError = $"Missing dependency: {error}. Install via AudioLab UI.";
                        break;
                    }
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining chunks.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Chunk {i + 1} error: {ex.Message}");
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    Logs.Error($"[AudioLab] {consecutiveFailures} consecutive failures, aborting remaining chunks.");
                    break;
                }
            }
        }

        if (pcmChunks.Count > 0)
        {
            // Streaming TTS always saves as WAV — chunks are PCM-concatenated in C#.
            // Output format param applies to single-shot generation only.
            byte[] finalWav = BuildWavFromPcm(pcmChunks, sampleRate, channels, bitsPerSample);
            AudioFile finalAudio = new(finalWav, MediaType.AudioWav);
            takeOutput(finalAudio);  // Real output — saved to disk

            meta.LastUsed = DateTime.UtcNow;
            Logs.Info($"[AudioLab] Streaming TTS complete: {totalDuration:F2}s total via {provider.Name}");
        }
        else
        {
            string errorMsg = firstError ?? meta.LastError ?? "Streaming generation produced no audio";
            Logs.Error($"[AudioLab] Streaming TTS produced no audio via {provider.Name}: {errorMsg}");
            meta.LastError ??= errorMsg;
            throw new SwarmReadableErrorException($"[AudioLab] {provider.Name}: {errorMsg}");
        }

        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["overall_percent"] = 100.0,
                ["current_status"] = "Complete"
            }
        });
    }

    /// <summary>Native streaming path for Engine-backed models with a real <c>IStreamingTtsRunner</c> (the
    /// <c>tts_streaming</c> feature flag) — calls <see cref="AudioEngineBridge.ProcessStreamAsync"/> ONCE with the
    /// full prompt and emits each <see cref="AudioChunk"/> as it arrives, instead of <see cref="GenerateLiveStreaming"/>'s
    /// text-chunk-and-regenerate-each-piece loop. Mirrors that method's takeOutput/final-WAV shape so the rest of
    /// the pipeline (intermediate playback, the saved final file) behaves identically either way.</summary>
    private async Task GenerateLiveNativeStreaming(T2IParamInput user_input, string batchId, Action<object> takeOutput,
        AudioProviderMetadata meta, AudioProviderDefinition provider, AudioModelDefinition modelDef)
    {
        Logs.Info($"[AudioLab] Native streaming TTS via {provider.Name}");
        user_input.Set(T2IParamTypes.DoNotSaveIntermediates, true);

        Dictionary<string, object> args = BuildEngineArgs(user_input, provider, modelDef);

        List<byte[]> pcmChunks = [];
        int sampleRate = 24000;
        int channels = 1;
        double totalDuration = 0;
        int chunkIndex = 0;

        try
        {
            await foreach (AudioChunk chunk in AudioEngineBridge.ProcessStreamAsync(provider.Id, args, user_input.InterruptToken))
            {
                if (user_input.InterruptToken.IsCancellationRequested)
                {
                    Logs.Info($"[AudioLab] Native streaming cancelled after {chunkIndex} chunks for {provider.Name}");
                    break;
                }
                if (chunk.Samples.Length == 0)
                {
                    continue;
                }

                sampleRate = chunk.SampleRate;
                channels = Math.Max(chunk.Channels, 1);
                byte[] pcm16 = FloatToPcm16(chunk.Samples);
                pcmChunks.Add(pcm16);
                totalDuration += chunk.Samples.Length / (double)(sampleRate * channels);
                chunkIndex++;

                byte[] chunkWav = BuildWavFromPcm([pcm16], sampleRate, channels, bitsPerSample: 16);
                takeOutput(new T2IEngine.ImageOutput { File = new AudioFile(chunkWav, MediaType.AudioWav), IsReal = false });
                takeOutput(new JObject
                {
                    ["gen_progress"] = new JObject
                    {
                        ["batch_index"] = batchId,
                        ["current_status"] = $"Streaming chunk {chunkIndex}..."
                    }
                });
                Logs.Debug($"[AudioLab] Native stream chunk {chunkIndex}: {chunk.Samples.Length / (double)sampleRate:F2}s @ offset {chunk.StartSampleOffset}");
            }
        }
        catch (OperationCanceledException) when (user_input.InterruptToken.IsCancellationRequested)
        {
            Logs.Info($"[AudioLab] Native streaming cancelled for {provider.Name}");
        }
        catch (Exception ex) when (ex is not SwarmReadableErrorException)
        {
            Logs.Error($"[AudioLab] Native streaming error for {provider.Name}: {ex.Message}");
            meta.LastError = ex.Message;
            // A stream that produced nothing before failing has no partial result worth keeping; one that
            // already emitted real audio finalizes with what it has rather than discarding it outright.
            if (pcmChunks.Count == 0)
            {
                throw;
            }
        }

        if (pcmChunks.Count > 0)
        {
            byte[] finalWav = BuildWavFromPcm(pcmChunks, sampleRate, channels, bitsPerSample: 16);
            takeOutput(new AudioFile(finalWav, MediaType.AudioWav));
            meta.LastUsed = DateTime.UtcNow;
            Logs.Info($"[AudioLab] Native streaming TTS complete: {totalDuration:F2}s total via {provider.Name}");
        }
        else
        {
            string errorMsg = meta.LastError ?? "Native streaming generation produced no audio";
            Logs.Error($"[AudioLab] Native streaming TTS produced no audio via {provider.Name}: {errorMsg}");
            throw new SwarmReadableErrorException($"[AudioLab] {provider.Name}: {errorMsg}");
        }

        takeOutput(new JObject
        {
            ["gen_progress"] = new JObject
            {
                ["batch_index"] = batchId,
                ["overall_percent"] = 100.0,
                ["current_status"] = "Complete"
            }
        });
    }

    /// <summary>Fallback Generate() — returns empty since GenerateLive() handles output.</summary>
    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        return [];
    }

    #endregion

    #region Model Loading and Shutdown

    /// <summary>Loads a model by matching its name against provider prefixes.
    /// <para>Swarm calls this only when the resident model actually changes, so it is the switch hook: before
    /// accepting the new model we make sure the card has room for it, evicting the outgoing pipelines when it
    /// does not. Without this the engine's own resident-pipeline cache accumulates across families (measured:
    /// 1.9GB → 14.1GB over five different models) and the next multi-GB checkpoint OOMs on load.</para></summary>
    public override Task<bool> LoadModel(T2IModel model, T2IParamInput input)
    {
        string providerId = GetProviderIdFromModel(model.Name);
        if (providerId != null && _providers.ContainsKey(providerId))
        {
            EnsureVramHeadroom(providerId, model.Name);
            CurrentModelName = model.Name;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>Drops resident audio pipelines when the incoming model would not fit in free VRAM.
    /// <para>The engine has its own pressure check, but it fires on a fixed low-VRAM floor rather than on the
    /// incoming model's size — so a 10GB checkpoint arriving with 10GB free looks fine to it and then OOMs.
    /// Here we know the model we are about to load and its declared VRAM need, so we can decide correctly.
    /// A same-model reload never evicts, keeping repeat generations warm.</para></summary>
    private void EnsureVramHeadroom(string providerId, string modelName)
    {
        // Only a same-model reload is safe to skip. A null CurrentModelName used to skip too, which left the
        // very first load on a fresh backend with no headroom check at all — the exact case where another
        // extension's image/video model may already own most of the card.
        if (CurrentModelName == modelName)
        {
            return;
        }
        if (!_providers.TryGetValue(providerId, out AudioProviderMetadata meta))
        {
            return;
        }
        AudioModelDefinition modelDef = GetModelDefinition(modelName, meta.Definition);
        long needBytes = ParseEstimatedBytes(modelDef?.EstimatedVram);
        if (needBytes <= 0)
        {
            // No declared estimate (API providers, CPU-only models): nothing to reason about, leave it resident.
            return;
        }
        long freeBytes = FreeVramBytes();
        if (freeBytes <= 0)
        {
            return;
        }
        long required = needBytes + VramSafetyMarginBytes;
        if (freeBytes >= required)
        {
            return;
        }
        Logs.Info($"[AudioLab] Switching to '{modelName}' needs ~{required / 1073741824.0:0.0}GB but only "
            + $"{freeBytes / 1073741824.0:0.0}GB VRAM is free, releasing resident audio models first.");
        AudioEngineBridge.Unload(providerId, modelDef?.Id);
    }

    /// <summary>Free VRAM on the busiest CUDA device, or -1 when it cannot be determined (no NVIDIA GPU,
    /// nvidia-smi absent). The engine runs on one device, so the card with the least free memory is the one
    /// carrying the audio models.
    /// <para>KNOWN LIMITATION: this is a heuristic, not a real device query. Audio can be pinned to a specific
    /// card via <c>HARTSY_AUDIO_CUDA_DEVICE</c>, but the engine exposes no way to ask which device a pipeline
    /// actually landed on, so on a multi-GPU box the min-free card may not be the audio card and the headroom
    /// check can free memory on the wrong reasoning. Fixing it properly needs engine-side device reporting.</para></summary>
    private static long FreeVramBytes()
    {
        try
        {
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is null || gpus.Length == 0)
            {
                return -1;
            }
            return gpus.Min(g => g.FreeMemory.InBytes);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Could not read free VRAM: {ex.Message}");
            return -1;
        }
    }

    /// <summary>Parses the leading size out of an <see cref="AudioModelDefinition.EstimatedVram"/> string —
    /// the field is human-facing prose ("~12GB", "~16GB (fp16)", "~12GB (lazy load)", "CPU only"), so we take
    /// the first number with a GB/MB unit and ignore the rest. Returns 0 when there is no parseable size.</summary>
    private static long ParseEstimatedBytes(string estimate)
    {
        if (string.IsNullOrWhiteSpace(estimate))
        {
            return 0;
        }
        Match m = Regex.Match(estimate, @"(\d+(?:\.\d+)?)\s*(GB|MB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            // Returning 0 disables the headroom check for this model, which is right for the genuine
            // no-GPU forms but would silently hide a typo in a new provider's value.
            if (!estimate.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                && !estimate.Contains("None", StringComparison.OrdinalIgnoreCase))
            {
                Logs.Warning($"[AudioLab] Unparseable EstimatedVram '{estimate}': VRAM headroom checking is disabled for that model. Use a form like \"~4GB\".");
            }
            return 0;
        }
        double multiplier = m.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1073741824.0 : 1048576.0;
        return (long)(value * multiplier);
    }

    /// <summary>Hands the engine's resident audio models back on request.
    /// <para>Without this override the base <see cref="AbstractBackend.FreeMemory"/> returns false and does
    /// nothing, so Swarm's "free memory" API and its memory-pressure paths could never reclaim audio VRAM —
    /// multi-GB pipelines stayed resident until the process exited.</para></summary>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        AudioEngineBridge.FreeMemory();
        if (systemRam)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        CurrentModelName = null;
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Shuts down the backend, removes models from registry, and cleans up.
    /// Mirrors DynamicAPIBackend.Shutdown().</summary>
    public override async Task Shutdown()
    {
        Logs.Info("[AudioLab] Shutting down audio backend");
        Program.ModelRefreshEvent -= ReRegisterModelsAfterRefresh;
        lock (_modelsLock)
        {
            foreach (string modelName in RegisteredAudioModels.Keys)
            {
                if (Program.MainSDModels.Models.ContainsKey(modelName))
                {
                    Logs.Verbose($"[AudioLab] Removing audio model from global registry: {modelName}");
                    Program.MainSDModels.Models.Remove(modelName, out _);
                }
            }
            RegisteredAudioModels.Clear();
            RemoteModels.Clear();
        }
        _providers.Clear();
        _supportedFeatureSet.Clear();
        // No processes to stop — but the Engine may hold multi-GB of resident audio models plus device memory,
        // so hand those back. The engine object survives and reloads on the next request.
        AudioEngineBridge.FreeMemory();
        Status = BackendStatus.DISABLED;
    }

    /// <summary>Gets all currently enabled provider metadata (for API status endpoints).</summary>
    public IReadOnlyDictionary<string, AudioProviderMetadata> GetProviders() => _providers;

    #endregion

    #region Engine Installation and Management

    /// <summary>Installs an engine: fetches its weights via the in-process C# engine,
    /// registers models, and persists the installed state. When <paramref name="modelId"/> is given, only
    /// that model's file set downloads (multi-GB variants are distinct checkpoints — never pull them all).</summary>
    public async Task<bool> InstallAndRegisterEngine(string providerId, Func<string, Task> onProgress = null, CancellationToken cancel = default, string modelId = null)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        if (definition == null)
        {
            Logs.Error($"[AudioLab] Provider '{providerId}' not found in registry.");
            return false;
        }

        await AudioWeights.Report(onProgress, $"Installing {definition.Name}...");

        try
        {
            // Engine-backed providers run in-process via the HartsyInference C# engine — no venv, no pip,
            // no Python server. This is the migration path; it takes precedence over the Python provisioning
            // below whenever the engine boundary advertises support for the provider.
            bool engineBacked = !definition.IsApiProvider && !definition.NotImplemented
                && AudioEngineBridge.IsProviderSupported(definition.Id);

            if (engineBacked)
            {
                if (AudioEngineBridge.ProviderManagesOwnWeights(definition.Id))
                {
                    // HF-auto-download providers (Whisper, Kokoro, ...). Prefetch now — per distinct weight set —
                    // so the download AND any load error surface here with progress, not mid-generation. A failure
                    // aborts install (we don't register a provider whose weights can't be fetched). Handlers that
                    // must bind a compute device to download (music) no-op here and fetch on first generation.
                    await AudioWeights.Report(onProgress, $"Downloading weights for {definition.Name} (in-process C# engine)...");
                    HashSet<string> fetched = new(StringComparer.Ordinal);
                    // With a model_id, prefetch only that variant — each can be its own multi-GB weight set
                    // (Whisper alone has 7); a bare provider install prefetches the full registered set.
                    IEnumerable<AudioModelDefinition> modelsToFetch = string.IsNullOrEmpty(modelId)
                        ? definition.Models
                        : definition.Models.Where(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
                    foreach (AudioModelDefinition modelDef in modelsToFetch)
                    {
                        cancel.ThrowIfCancellationRequested();
                        // Dedup models that share one weight set (e.g. multiple voices of one TTS repo).
                        IReadOnlyList<string> locs = AudioEngineBridge.GetWeightLocations(definition.Id, modelDef.Id);
                        string dedupKey = locs.Count > 0 ? string.Join("|", locs) : modelDef.Id;
                        if (!fetched.Add(dedupKey))
                        {
                            continue;
                        }
                        JObject result = await AudioEngineBridge.EnsureWeightsAsync(definition.Id, modelDef.Id, onProgress, cancel);
                        if (result?["success"]?.Value<bool>() != true)
                        {
                            string err = result?["error"]?.ToString() ?? "unknown error";
                            await AudioWeights.Report(onProgress, $"Failed to fetch weights for {definition.Name}: {err}");
                            Logs.Error($"[AudioLab] Weight prefetch failed for '{definition.Id}'/'{modelDef.Id}': {err}");
                            return false;
                        }
                        // Release the prefetched pipeline so installing many models doesn't pin RAM/VRAM.
                        AudioEngineBridge.Unload(definition.Id, modelDef.Id);
                    }
                }
                else
                {
                    // Checkpoint providers (music): fetch the .safetensors now. With a model id, only that
                    // variant's files; a bare provider install pulls the DEFAULT model's set instead of every
                    // registered variant (each is a distinct multi-GB checkpoint).
                    string installModel = modelId;
                    if (string.IsNullOrEmpty(installModel))
                    {
                        installModel = definition.Models.FirstOrDefault(m => AudioWeightsRegistry.SpecsFor(definition.Id, m.Id).Length > 0)?.Id;
                    }
                    await AudioWeights.Report(onProgress, $"Downloading weights for {definition.Name}{(installModel is null ? "" : $" ({installModel})")} (in-process C# engine)...");
                    bool registered = await AudioWeights.EnsureProviderWeightsAsync(definition, onProgress, cancel, installModel);
                    if (!registered)
                    {
                        await AudioWeights.Report(onProgress, $"No auto-download is registered for {definition.Name} yet. Place its .safetensors in {AudioWeights.WeightsDirectory(definition)} to enable it.");
                    }
                }
            }
            else if (definition.IsApiProvider)
            {
                // API providers use external cloud APIs — no venv, no deps, no model downloads needed.
                // The lightweight "api" group server starts lazily on first request via EnsureGroupRunningAsync.
                await AudioWeights.Report(onProgress, $"Registering {definition.Name} (cloud API, no local setup needed)...");
            }
            else
            {
                // Not an API provider and not yet supported by the C# engine: with Python removed there is
                // no way to run it. Refuse cleanly, naming the specific blocker, rather than registering a model
                // that can't generate.
                string reason = AudioServices.AudioUnsupportedReasons.Message(definition.Id, definition.Name);
                Logs.Error($"[AudioLab] {reason}");
                await AudioWeights.Report(onProgress, reason);
                return false;
            }

            await AudioWeights.Report(onProgress, $"Registering models for {definition.Name}...");
            AudioProviderMetadata meta = new()
            {
                Definition = definition,
                IsEnabled = true,
                DependenciesInstalled = true
            };
            _providers[providerId] = meta;
            RegisterModelsForProvider(definition);

            if (CategoryFlags.TryGetValue(definition.Category, out string categoryFlag))
            {
                _supportedFeatureSet.TryAdd(categoryFlag, 0);
            }
            foreach (string flag in definition.FeatureFlags)
            {
                _supportedFeatureSet.TryAdd(flag, 0);
            }

            UpdateRemoteModels();

            lock (_modelsLock)
            {
                InstalledEngines.Add(providerId);
            }
            SaveInstalledEnginesConfig();

            Program.ModelRefreshEvent?.Invoke();

            await AudioWeights.Report(onProgress, $"{definition.Name} installed successfully!");
            Logs.Info($"[AudioLab] Engine '{definition.Name}' installed and registered.");
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Logs.Info($"[AudioLab] Install of '{providerId}' cancelled.");
            await AudioWeights.Report(onProgress, "Installation cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to install engine '{providerId}': {ex}");
            await AudioWeights.Report(onProgress, $"Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Uninstalls an engine: removes models from registry and persists the change. When
    /// <paramref name="deleteWeights"/> is set, also deletes the provider's on-disk weights — but only
    /// locations not still needed by another installed provider (shared side-model caches are retained).</summary>
    public void UnregisterEngine(string providerId, bool deleteWeights = false)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        string providerName = definition?.Name ?? providerId;

        if (definition != null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                string modelName = $"Audio Models/{definition.ModelPrefix}/{modelDef.Id}";
                if (Program.MainSDModels.Models.ContainsKey(modelName))
                {
                    Program.MainSDModels.Models.Remove(modelName, out _);
                    Logs.Debug($"[AudioLab] Removed model: {modelName}");
                }
                lock (_modelsLock)
                {
                    RegisteredAudioModels.Remove(modelName);
                }
            }
        }

        if (Models.TryGetValue("Stable-Diffusion", out List<string> modelList) && definition != null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                modelList.Remove($"Audio Models/{definition.ModelPrefix}/{modelDef.Id}");
            }
        }

        _providers.TryRemove(providerId, out _);
        // Must come BEFORE UpdateRemoteModels: that call ends in ReconcileWeights, which would still see
        // this provider installed and recurse straight back into UnregisterEngine.
        lock (_modelsLock)
        {
            InstalledEngines.Remove(providerId);
        }
        SaveInstalledEnginesConfig();

        RebuildFeatureFlags();
        UpdateRemoteModels();

        // Delete on-disk weights AFTER removing from InstalledEngines, so the "still needed by another
        // installed provider" guard below correctly excludes this provider.
        if (deleteWeights)
        {
            DeleteProviderWeights(providerId, definition);
        }

        Program.ModelRefreshEvent?.Invoke();
        Logs.Info($"[AudioLab] Engine '{providerName}' unregistered{(deleteWeights ? " (weights deleted)" : "")}.");
    }

    /// <summary>All provider-private weight locations (absolute, deduped) across a provider's models.</summary>
    private static HashSet<string> WeightLocationsForProvider(string providerId, AudioProviderDefinition definition)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (definition is null)
        {
            return set;
        }
        foreach (AudioModelDefinition modelDef in definition.Models)
        {
            foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, modelDef.Id))
            {
                if (!string.IsNullOrEmpty(loc))
                {
                    try { set.Add(Path.GetFullPath(loc)); } catch { /* unparseable path — skip */ }
                }
            }
        }
        return set;
    }

    /// <summary>Deletes a provider's weight files/dirs, skipping any location still referenced by another
    /// currently-installed provider. Best-effort: logs and continues on per-path failure, never throws.</summary>
    private void DeleteProviderWeights(string providerId, AudioProviderDefinition definition)
    {
        HashSet<string> mine = WeightLocationsForProvider(providerId, definition);
        // The locations above are derived from catalog metadata, which can name a different repo than the one
        // the engine actually downloaded from. Anything the artifact index admitted is ground truth, so fold in
        // the directory each admitted artifact really lives in.
        foreach (AudioArtifact artifact in AudioArtifactIndex.Admitted.Values)
        {
            if (artifact.ProviderId != providerId || string.IsNullOrEmpty(artifact.ArtifactPath))
            {
                continue;
            }
            AudioArtifactIdentity.RemoveSidecar(artifact.ArtifactPath);
            string dir = Path.GetDirectoryName(artifact.ArtifactPath);
            if (!string.IsNullOrEmpty(dir))
            {
                try { mine.Add(Path.GetFullPath(dir)); } catch { /* unparseable path — skip */ }
            }
        }
        if (mine.Count == 0)
        {
            Logs.Info($"[AudioLab] No deletable weight locations known for '{providerId}', nothing removed.");
            return;
        }
        // Release any resident pipeline holding these files before deleting.
        if (definition is not null)
        {
            foreach (AudioModelDefinition modelDef in definition.Models)
            {
                AudioEngineBridge.Unload(providerId, modelDef.Id);
            }
        }
        // Union of locations still needed by other installed providers (providerId already removed above).
        HashSet<string> stillNeeded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string otherId in InstalledEnginesSnapshot())
        {
            foreach (string loc in WeightLocationsForProvider(otherId, AudioProviderRegistry.GetById(otherId)))
            {
                stillNeeded.Add(loc);
            }
        }
        foreach (string path in mine)
        {
            if (stillNeeded.Contains(path))
            {
                Logs.Info($"[AudioLab] Retained shared weights (used by another installed engine): {path}");
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logs.Info($"[AudioLab] Deleted weight file: {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    Logs.Info($"[AudioLab] Deleted weight directory: {path}");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not delete weights at '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>Deletes a single model's weights while leaving the engine installed and the model registered
    /// in the browser (its row flips back to "install"; generation would auto-redownload as usual). Skips any
    /// location still referenced by a sibling model of the same provider or by another installed provider —
    /// so shared files (e.g. ACE-Step's shared config / silence latent) survive. Best-effort, never throws.</summary>
    public void DeleteModelWeights(string providerId, string modelId)
    {
        AudioProviderDefinition definition = AudioProviderRegistry.GetById(providerId);
        if (definition is null)
        {
            return;
        }
        HashSet<string> mine = new(StringComparer.OrdinalIgnoreCase);
        foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, modelId))
        {
            if (!string.IsNullOrEmpty(loc))
            {
                try { mine.Add(Path.GetFullPath(loc)); } catch { /* unparseable path — skip */ }
            }
        }
        if (mine.Count == 0)
        {
            Logs.Info($"[AudioLab] No deletable weight locations known for '{providerId}/{modelId}', nothing removed.");
            return;
        }
        // Release any resident pipeline holding this model's files before deleting.
        AudioEngineBridge.Unload(providerId, modelId);

        // Locations still needed by a sibling model of THIS provider, or by any other installed provider.
        HashSet<string> stillNeeded = new(StringComparer.OrdinalIgnoreCase);
        foreach (AudioModelDefinition sibling in definition.Models)
        {
            if (string.Equals(sibling.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string loc in AudioEngineBridge.GetWeightLocations(providerId, sibling.Id))
            {
                if (!string.IsNullOrEmpty(loc))
                {
                    try { stillNeeded.Add(Path.GetFullPath(loc)); } catch { /* skip */ }
                }
            }
        }
        foreach (string otherId in InstalledEnginesSnapshot())
        {
            if (string.Equals(otherId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string loc in WeightLocationsForProvider(otherId, AudioProviderRegistry.GetById(otherId)))
            {
                stillNeeded.Add(loc);
            }
        }
        foreach (string path in mine)
        {
            if (stillNeeded.Contains(path))
            {
                Logs.Info($"[AudioLab] Retained shared weights (still used by another model/engine): {path}");
                continue;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logs.Info($"[AudioLab] Deleted weight file: {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    Logs.Info($"[AudioLab] Deleted weight directory: {path}");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not delete weights at '{path}': {ex.Message}");
            }
        }
        ReconcileWeights();
        Program.ModelRefreshEvent?.Invoke();
        Logs.Info($"[AudioLab] Removed weights for '{definition.Name}' model '{modelId}'.");
    }

    /// <summary>Returns the set of currently installed engine IDs.</summary>
    public IReadOnlySet<string> GetInstalledEngineIds()
    {
        lock (_modelsLock)
        {
            return new HashSet<string>(InstalledEngines, StringComparer.Ordinal);
        }
    }

    /// <summary>A point-in-time copy, so callers can iterate without racing an install.</summary>
    private HashSet<string> InstalledEnginesSnapshot()
    {
        lock (_modelsLock)
        {
            return new HashSet<string>(InstalledEngines, StringComparer.Ordinal);
        }
    }

    /// <summary>Returns the installed provider IDs flagged "weights missing" by the last reconcile pass.</summary>

    /// <summary>Drops any installed provider whose weights are gone, resetting it to exactly the state it had
    /// before it was ever installed. There is no "repair" concept: with the Python dependency era over, an
    /// install IS just the weight download, so a provider without weights is simply not installed and the normal
    /// Install button brings it back.
    /// <para>Engine-managed (<see cref="AudioEngineBridge.ProviderManagesOwnWeights"/>) and API providers are
    /// skipped — the engine fetches their weights lazily on first load, so "not on disk yet" is the normal
    /// state and says nothing about whether they're installed.</para></summary>
    private void ReconcileWeights()
    {
        // UnregisterEngine republishes models, which routes back here; without this guard a single missing
        // provider recursed thousands of times.
        if (Interlocked.Exchange(ref _reconciling, 1) == 1)
        {
            return;
        }
        try
        {
        foreach (string providerId in InstalledEnginesSnapshot())
        {
            AudioProviderDefinition def = AudioProviderRegistry.GetById(providerId);
            if (def is null || def.IsApiProvider || def.Models.Count == 0
                || AudioEngineBridge.ProviderManagesOwnWeights(providerId))
            {
                continue;
            }
            // A partial set is still usable — only reset when nothing at all is on disk.
            bool anyPresent = false;
            foreach (AudioModelDefinition modelDef in def.Models)
            {
                if (modelDef.SelfManaged || AudioEngineBridge.WeightsPresent(providerId, modelDef.Id))
                {
                    anyPresent = true;
                    break;
                }
            }
            if (!anyPresent)
            {
                Logs.Debug($"[AudioLab] '{def.Name}' has no weights on disk, resetting it to not-installed.");
                UnregisterEngine(providerId, deleteWeights: false);
            }
        }
        }
        finally
        {
            Interlocked.Exchange(ref _reconciling, 0);
        }
    }

    /// <summary>Re-entrancy guard for <see cref="ReconcileWeights"/>.</summary>
    private int _reconciling;

    /// <summary>Rebuilds the supported feature flags from currently active providers.</summary>
    private void RebuildFeatureFlags()
    {
        _supportedFeatureSet.Clear();
        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            if (CategoryFlags.TryGetValue(meta.Definition.Category, out string categoryFlag))
            {
                _supportedFeatureSet.TryAdd(categoryFlag, 0);
            }
            if (meta.Definition.Category != AudioCategory.STT)
            {
                _supportedFeatureSet.TryAdd("audiolab_output", 0);
            }
            foreach (string flag in meta.Definition.FeatureFlags)
            {
                _supportedFeatureSet.TryAdd(flag, 0);
            }
        }
    }

    #endregion

    #region Configuration Persistence

    /// <summary>Loads the installed engines set from the JSON config file.</summary>
    private void LoadInstalledEnginesConfig()
    {
        lock (_modelsLock)
        {
            InstalledEngines.Clear();
        }
        try
        {
            if (File.Exists(InstalledEnginesConfigPath))
            {
                string json = File.ReadAllText(InstalledEnginesConfigPath);
                JObject config = JObject.Parse(json);
                JArray installed = config["installed"] as JArray;
                if (installed != null)
                {
                    foreach (JToken token in installed)
                    {
                        string id = token.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            lock (_modelsLock)
                            {
                                InstalledEngines.Add(id);
                            }
                        }
                    }
                }
                Logs.Debug($"[AudioLab] Loaded {InstalledEnginesSnapshot().Count} installed engine(s) from config.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to load installed engines config: {ex.Message}");
        }
    }

    /// <summary>Saves the installed engines set to the JSON config file.</summary>
    private void SaveInstalledEnginesConfig()
    {
        try
        {
            JObject config = new()
            {
                ["installed"] = new JArray(InstalledEnginesSnapshot().OrderBy(id => id).ToArray())
            };
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledEnginesConfigPath));
            File.WriteAllText(InstalledEnginesConfigPath, config.ToString());
            Logs.Debug($"[AudioLab] Saved {InstalledEnginesSnapshot().Count} installed engine(s) to config.");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to save installed engines config: {ex.Message}");
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Determines the provider ID for a selected model.
    ///
    /// <para>A file-backed model is resolved through the artifact index, which knows what the file on disk
    /// actually is. The prefix match below is the fallback for providers still registering virtual entries,
    /// and is why the display name has to keep its shape until every family is migrated.</para></summary>
    private string GetProviderIdFromModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;

        AudioArtifact artifact = AudioArtifactIndex.Lookup(modelName);
        if (artifact is not null)
        {
            return artifact.ProviderId;
        }
        foreach (AudioProviderMetadata meta in _providers.Values)
        {
            string prefix = $"Audio Models/{meta.Definition.ModelPrefix}/";
            if (modelName.StartsWith(prefix))
            {
                return meta.Definition.Id;
            }
        }
        return null;
    }

    /// <summary>Default generation length when neither the core "Duration" param nor AudioLab's "Max Duration"
    /// was set, per provider. Everything not listed keeps the general-purpose 30s default.</summary>
    private static double DefaultDurationFor(string providerId) => providerId switch
    {
        "audiogen_sfx" => 10.0,
        "stableaudio_music" => 11.0,
        _ => 30.0,
    };

    /// <summary>Gets the AudioModelDefinition for a selected model, preferring the artifact index over the
    /// last-segment split (which cannot tell a variant id from any other trailing path component).</summary>
    private static AudioModelDefinition GetModelDefinition(string modelName, AudioProviderDefinition provider)
    {
        AudioArtifact artifact = AudioArtifactIndex.Lookup(modelName);
        string modelId = artifact?.ModelId ?? modelName.Split('/').LastOrDefault() ?? "";
        return provider.Models.FirstOrDefault(m => m.Id == modelId);
    }

    /// <summary>Gets base64-encoded audio data from an AudioFile T2I parameter.</summary>
    private static string GetBase64Audio(T2IParamInput input, T2IRegisteredParam<AudioFile> param)
    {
        return input.TryGet(param, out AudioFile audio) && audio != null
            ? Convert.ToBase64String(audio.RawData) : "";
    }

    /// <summary>Builds engine kwargs from T2I parameters.
    /// Combines category-level args, model EngineConfig, and provider-specific params.</summary>
    private static Dictionary<string, object> BuildEngineArgs(T2IParamInput input, AudioProviderDefinition provider, AudioModelDefinition modelDef)
    {
        Dictionary<string, object> args = [];

        // Model-variant hint (consumed by the C# engine routing gate to resolve the exact checkpoint;
        // ignored by the Python path and by the engine's process methods). Prefixed to avoid colliding
        // with real engine kwargs.
        if (modelDef is not null)
        {
            args["__model_id"] = modelDef.Id;
        }

        // 1. Category-level args (shared across all providers in a category)
        switch (provider.Category)
        {
            case AudioCategory.TTS:
                args["text"] = input.Get(T2IParamTypes.Prompt, "Hello world");
                args["volume"] = input.TryGet(AudioLabParams.Volume, out double vol) ? vol : 0.8;
                // Shared sampling params (tts_sampling flag)
                if (input.TryGet(AudioLabParams.Temperature, out double sharedTemp))
                    args["temperature"] = sharedTemp;
                if (input.TryGet(AudioLabParams.TopP, out double sharedTopP))
                    args["top_p"] = sharedTopP;
                if (input.TryGet(AudioLabParams.RepetitionPenalty, out double sharedRepPen))
                    args["repetition_penalty"] = sharedRepPen;
                if (input.TryGet(AudioLabParams.TopK, out int sharedTopK))
                    args["top_k"] = sharedTopK;
                if (input.TryGet(AudioLabParams.MinP, out double sharedMinP))
                    args["min_p"] = sharedMinP;
                // NOTE: no shared cfg_scale here. Core's CFGScale is hidden for audio models, yet it always
                // carries a default, so setting it unconditionally sent an unrequested cfg_scale to Kokoro,
                // Piper, Bark, Orpheus, CSM, CosyVoice, PocketTTS, KyutaiTTS and Zonos, none of which take one.
                // Providers with a real CFG knob set it in their own case below.
                // Shared voice reference (tts_voice_ref flag)
                string sharedRef = GetBase64Audio(input, AudioLabParams.ReferenceAudio);
                if (!string.IsNullOrEmpty(sharedRef))
                    args["reference_audio"] = sharedRef;
                if (input.TryGet(AudioLabParams.ReferenceText, out string sharedRefText) && !string.IsNullOrEmpty(sharedRefText))
                    args["ref_text"] = sharedRefText;
                // Seed for reproducibility (pipelines that accept one). SwarmUI resolves -1 to a concrete value upstream.
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long ttsSeed) ? ttsSeed : -1L;
                break;

            case AudioCategory.STT:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.AudioInput);
                args["language"] = input.TryGet(AudioLabParams.Language, out string sttLang) ? sttLang : "en";
                // AssemblyAI reads `language_code` and takes the same bare ISO-639-1 code AudioLabParams.Language
                // offers. Google/Azure also read `language_code` but require a BCP-47 region subtag ("en-US"),
                // which this param can't express yet — forwarding a bare "en" there would be worse than their
                // own defaults, so they keep them until the Language param gains region-qualified values.
                if (provider.Id == "assemblyai_stt" && (string)args["language"] != "auto")
                {
                    args["language_code"] = args["language"];
                }
                // "default" is a single-model sentinel, not a real API model name — leaving model_id unset
                // there keeps each handler's own correct default (e.g. Deepgram nova-3). Local providers
                // carry their real name via EngineConfig["model_name"] instead.
                if (provider.IsApiProvider && modelDef is not null && modelDef.Id != "default")
                {
                    args["model_id"] = modelDef.Id;
                }
                break;

            case AudioCategory.AudioGeneration:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                // Prefer the CORE Swarm audio param (what Swarm users + the HartsyInference ACE-Step path use);
                // fall back to AudioLab's own "Max Duration" for existing AudioLab-UI workflows, then a
                // per-provider default — a flat 30s silently truncated on providers with a lower real ceiling
                // (Stable Audio Open Small's DiT physically caps at 11.89s; anything past that is clamped
                // inside StableAudioPipeline.Generate) or ran past what the model was tuned for (AudioGen was
                // trained on 10s clips per Meta's release; quality degrades noticeably beyond that).
                args["duration"] = input.TryGet(T2IParamTypes.Text2AudioDuration, out double coreDur) ? coreDur
                    : input.TryGet(AudioLabParams.Duration, out double genDur) ? genDur
                    : DefaultDurationFor(provider.Id);
                // Shared AudioCraft sampling (audiocraft_sampling flag)
                if (input.TryGet(AudioLabParams.GuidanceScale, out double genGuidance))
                    args["cfg_coef"] = genGuidance;
                if (input.TryGet(AudioLabParams.AudioCraftTemperature, out double genTemp))
                    args["temperature"] = genTemp;
                if (input.TryGet(AudioLabParams.AudioCraftTopK, out int genTopK))
                    args["top_k"] = genTopK;
                if (input.TryGet(AudioLabParams.AudioCraftTopP, out double genTopP))
                    args["top_p"] = genTopP;
                break;

            case AudioCategory.VoiceConversion:
                args["source_audio"] = GetBase64Audio(input, AudioLabParams.SourceAudio);
                args["target_voice"] = GetBase64Audio(input, AudioLabParams.TargetVoice);
                break;

            case AudioCategory.AudioProcessing:
                args["audio_data"] = GetBase64Audio(input, AudioLabParams.FXInput);
                break;

            default:
                args["prompt"] = input.Get(T2IParamTypes.Prompt, "");
                break;
        }

        // 1b. Output format args (shared across all audio-producing categories)
        if (provider.Category != AudioCategory.STT)
        {
            args["output_format"] = input.TryGet(AudioLabParams.AudioOutputFormat, out string fmt) ? fmt : "wav_16";
            args["output_quality"] = input.TryGet(AudioLabParams.AudioQuality, out string qual) ? qual : "high";
        }

        // 2. Merge model's EngineConfig (model_name, model_size, mode, etc.)
        if (modelDef?.EngineConfig != null)
        {
            foreach (KeyValuePair<string, object> kvp in modelDef.EngineConfig)
            {
                args[kvp.Key] = kvp.Value;
            }
        }

        // 3. Provider-specific params (only the active provider's params are populated)
        switch (provider.Id)
        {
            case "chatterbox_tts":
                args["exaggeration"] = input.TryGet(AudioLabParams.Exaggeration, out double exag) ? exag : 0.5;
                // Engine's SpeechRequest field is CfgScale, read via the "cfg_scale" key by AudioEngineRequests.Speech
                // — "cfg_weight" was silently dropped at this boundary and the value never arrived.
                args["cfg_scale"] = input.TryGet(AudioLabParams.CFGWeight, out double cfgw) ? cfgw : 0.5;
                break;

            case "kokoro_tts":
                args["voice"] = input.TryGet(AudioLabParams.KokoroVoice, out string kv) ? kv : "af_heart";
                args["speed"] = input.TryGet(AudioLabParams.KokoroSpeed, out double ks) ? ks : 1.0;
                break;

            case "piper_tts":
                args["voice"] = input.TryGet(AudioLabParams.PiperVoice, out string pv) ? pv : "en_US-amy-medium";
                args["speed"] = input.TryGet(AudioLabParams.PiperSpeed, out double ps) ? ps : 1.0;
                break;

            case "orpheus_tts":
                args["voice"] = input.TryGet(AudioLabParams.OrpheusVoice, out string ov) ? ov : "tara";
                break;

            case "csm_tts":
                args["speaker_id"] = input.TryGet(AudioLabParams.Speaker, out string sp) ? sp : "0";
                break;

            case "bark_tts":
                args["voice"] = input.TryGet(AudioLabParams.BarkVoice, out string bv) ? bv : "v2/en_speaker_6";
                args["temperature"] = input.TryGet(AudioLabParams.TextTemp, out double tt) ? tt : 0.7;
                args["waveform_temp"] = input.TryGet(AudioLabParams.WaveformTemp, out double wt) ? wt : 0.7;
                break;

            case "vibevoice_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.DiffusionSteps, out int diffSteps) ? diffSteps : 10;
                args["cfg_scale"] = input.TryGet(AudioLabParams.VibeVoiceCFG, out double vvCfg) ? vvCfg : 1.3;
                break;

            case "dia_tts":
                args["top_k"] = input.TryGet(AudioLabParams.CFGFilterTopK, out int cfgTopK) ? cfgTopK : 45;
                args["cfg_scale"] = input.TryGet(AudioLabParams.DiaCFGScale, out double diaCfg) ? diaCfg : 3.0;
                break;

            case "f5_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.NFEStep, out int nfeStep) ? nfeStep : 32;
                args["speed"] = input.TryGet(AudioLabParams.F5Speed, out double f5spd) ? f5spd : 1.0;
                args["cfg_scale"] = input.TryGet(AudioLabParams.F5CFG, out double f5Cfg) ? f5Cfg : 2.0;
                if (input.TryGet(AudioLabParams.F5SwaySampling, out double f5Sway))
                    args["sway_sampling_coef"] = f5Sway;
                break;

            case "zipvoice_tts":
                args["nfe_step"] = input.TryGet(AudioLabParams.ZipVoiceSteps, out int zvSteps) ? zvSteps : 16;
                args["speed"] = input.TryGet(AudioLabParams.ZipVoiceSpeed, out double zvSpd) ? zvSpd : 1.0;
                args["cfg_scale"] = input.TryGet(AudioLabParams.ZipVoiceCFG, out double zvCfg) ? zvCfg : 1.0;
                break;

            case "zonos_tts":
                if (input.TryGet(AudioLabParams.ZonosLanguage, out string zl))
                    args["language"] = zl;
                if (input.TryGet(AudioLabParams.ZonosEmotion, out string ze))
                    args["emotion"] = ze;
                args["speaking_rate"] = input.TryGet(AudioLabParams.SpeakingRate, out double sr) ? sr : 15.0;
                args["pitch_std"] = input.TryGet(AudioLabParams.ZonosPitchStd, out double zps) ? zps : 20.0;
                break;

            case "fishspeech_tts":
                args["max_new_tokens"] = input.TryGet(AudioLabParams.FishSpeechMaxTokens, out int fsMaxTok) ? fsMaxTok : 0;
                args["chunk_length"] = input.TryGet(AudioLabParams.FishSpeechChunkLength, out int fsChunk) ? fsChunk : 200;
                args["normalize_loudness"] = input.TryGet(AudioLabParams.FishSpeechNormalize, out string fsNorm) ? fsNorm == "true" : true;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long fsSeed) ? fsSeed : -1L;
                break;

            case "qwen3_tts":
                args["qwen3_language"] = input.TryGet(AudioLabParams.Qwen3Language, out string q3Lang) ? q3Lang : "Auto";
                args["voice"] = input.TryGet(AudioLabParams.Qwen3Speaker, out string q3Spk) ? q3Spk : "Ryan";
                if (input.TryGet(AudioLabParams.Qwen3Instruct, out string q3Inst) && !string.IsNullOrEmpty(q3Inst))
                    args["qwen3_instruct"] = q3Inst;
                if (input.TryGet(AudioLabParams.Qwen3XVectorOnly, out string q3Xv))
                    args["x_vector_only_mode"] = q3Xv == "true";
                break;

            case "cosyvoice_tts":
                if (input.TryGet(AudioLabParams.CosyVoiceVoice, out string cvv))
                    args["voice"] = cvv;
                break;

            case "pockettts_tts":
                if (input.TryGet(AudioLabParams.PocketTTSVoice, out string pttv))
                    args["voice"] = pttv;
                break;

            case "kyutaitts_tts":
                if (input.TryGet(AudioLabParams.KyutaiTTSVoice, out string ktv))
                    args["voice"] = ktv;
                break;

            case "melotts_tts":
                // The engine takes a numeric slot; these are the checkpoint's spk2id values.
                args["speaker_id"] = (input.TryGet(AudioLabParams.MeloSpeaker, out string meloSpk) ? meloSpk : "EN-US") switch
                {
                    "EN-BR" => 1,
                    "EN_INDIA" => 2,
                    "EN-AU" => 3,
                    "EN-Default" => 4,
                    _ => 0,
                };
                args["speed"] = input.TryGet(AudioLabParams.MeloSpeed, out double meloSpd) ? meloSpd : 1.0;
                break;

            case "styletts2_tts":
                args["diffusion_steps"] = input.TryGet(AudioLabParams.StyleTTS2DiffusionSteps, out int st2Steps) ? st2Steps : 10;
                args["embedding_scale"] = input.TryGet(AudioLabParams.StyleTTS2EmbeddingScale, out double st2Emb) ? st2Emb : 1.0;
                // alpha/beta only mean anything against a reference clip (multi-speaker checkpoints).
                if (input.TryGet(AudioLabParams.StyleTTS2Alpha, out double st2Alpha))
                    args["alpha"] = st2Alpha;
                if (input.TryGet(AudioLabParams.StyleTTS2Beta, out double st2Beta))
                    args["beta"] = st2Beta;
                break;

            case "sparktts_tts":
                // A reference clip switches Spark-TTS from voice-creation to cloning; the creation knobs are
                // then ignored upstream, so only send them when no reference was supplied.
                if (string.IsNullOrEmpty(GetBase64Audio(input, AudioLabParams.ReferenceAudio)))
                {
                    args["gender"] = input.TryGet(AudioLabParams.SparkGender, out string spkGen) ? spkGen : "female";
                    args["pitch"] = input.TryGet(AudioLabParams.SparkPitch, out string spkPitch) ? spkPitch : "moderate";
                    args["speed"] = input.TryGet(AudioLabParams.SparkSpeed, out string spkSpeed) ? spkSpeed : "moderate";
                }
                break;

            case "openai_tts":
                args["voice"] = input.TryGet(AudioLabParams.OpenAIVoice, out string oaVoice) ? oaVoice : "alloy";
                args["speed"] = input.TryGet(AudioLabParams.OpenAISpeed, out double oaSpeed) ? oaSpeed : 1.0;
                // Only gpt-4o-mini-tts accepts instructions; sending it to tts-1 is an API error.
                if (modelDef?.Id == "gpt-4o-mini-tts"
                    && input.TryGet(AudioLabParams.OpenAIInstructions, out string oaInstr) && !string.IsNullOrEmpty(oaInstr))
                {
                    args["instructions"] = oaInstr;
                }
                break;

            case "google_tts":
                args["voice_name"] = input.TryGet(AudioLabParams.GoogleVoiceName, out string gVoice) ? gVoice : "en-US-Neural2-F";
                args["speaking_rate"] = input.TryGet(AudioLabParams.GoogleSpeakingRate, out double gRate) ? gRate : 1.0;
                args["pitch"] = input.TryGet(AudioLabParams.GooglePitch, out double gPitch) ? gPitch : 0.0;
                break;

            case "deepgram_tts":
                if (input.TryGet(AudioLabParams.DeepgramVoice, out string dgVoice) && !string.IsNullOrEmpty(dgVoice))
                    args["model_id"] = dgVoice;
                break;

            case "cartesia_tts":
                if (input.TryGet(AudioLabParams.CartesiaVoice, out string ctVoice) && !string.IsNullOrEmpty(ctVoice))
                    args["voice_id"] = ctVoice;
                if (input.TryGet(AudioLabParams.CartesiaModel, out string ctModel) && !string.IsNullOrEmpty(ctModel))
                    args["model_id"] = ctModel;
                args["speed"] = input.TryGet(AudioLabParams.CartesiaSpeed, out double ctSpd) ? ctSpd : 1.0;
                break;

            case "playht_tts":
                if (input.TryGet(AudioLabParams.PlayHTVoice, out string phVoice) && !string.IsNullOrEmpty(phVoice))
                    args["voice"] = phVoice;
                args["quality"] = input.TryGet(AudioLabParams.PlayHTQuality, out string phQual) ? phQual : "medium";
                args["voice_engine"] = input.TryGet(AudioLabParams.PlayHTEngine, out string phEng) ? phEng : "PlayHT2.0";
                args["speed"] = input.TryGet(AudioLabParams.PlayHTSpeed, out double phSpd) ? phSpd : 1.0;
                break;

            case "dolby_audioproc":
                args["preset"] = input.TryGet(AudioLabParams.DolbyPreset, out string dbPreset) ? dbPreset : "speech";
                break;

            case "elevenlabs_sfx":
                // 0 = omit so the API picks the optimal duration, which is its documented default.
                double sfxDurVal = input.TryGet(AudioLabParams.ElevenSFXDuration, out double sfxDur) ? sfxDur : 0.0;
                if (sfxDurVal > 0) args["duration_seconds"] = sfxDurVal;
                args["prompt_influence"] = input.TryGet(AudioLabParams.ElevenSFXInfluence, out double sfxInf) ? sfxInf : 0.3;
                break;

            case "elevenlabs_vc":
                args["remove_background_noise"] = input.TryGet(AudioLabParams.ElevenRemoveNoise, out string elRn) && elRn == "true";
                break;

            case "azure_stt":
                args["profanity"] = input.TryGet(AudioLabParams.AzureProfanity, out string azProf) ? azProf : "masked";
                break;

            case "deepgram_stt":
                args["model_id"] = input.TryGet(AudioLabParams.DeepgramSTTModel, out string dgm) ? dgm : "nova-3";
                break;

            case "google_cloud_stt":
                args["model_id"] = input.TryGet(AudioLabParams.GoogleSTTModel, out string gsm) ? gsm : "latest_long";
                break;

            case "openai_stt":
                if (input.TryGet(AudioLabParams.OpenAISTTPrompt, out string oaP) && !string.IsNullOrEmpty(oaP))
                    args["prompt"] = oaP;
                break;

            case "assemblyai_stt":
                args["speaker_labels"] = input.TryGet(AudioLabParams.AssemblySpeakerLabels, out string aaLbl) && aaLbl == "true";
                args["sentiment_analysis"] = input.TryGet(AudioLabParams.AssemblySentiment, out string aaSent) && aaSent == "true";
                break;

            case "elevenlabs_tts":
                args["stability"] = input.TryGet(AudioLabParams.ElevenStability, out double elStab) ? elStab : 0.5;
                args["similarity_boost"] = input.TryGet(AudioLabParams.ElevenSimilarity, out double elSim) ? elSim : 0.75;
                args["style"] = input.TryGet(AudioLabParams.ElevenStyle, out double elStyle) ? elStyle : 0.0;
                args["use_speaker_boost"] = !input.TryGet(AudioLabParams.ElevenSpeakerBoost, out string elBoost) || elBoost == "true";
                break;

            case "azure_tts":
                if (input.TryGet(AudioLabParams.AzureStyle, out string azStyle) && !string.IsNullOrEmpty(azStyle))
                {
                    args["style"] = azStyle;
                    args["style_degree"] = input.TryGet(AudioLabParams.AzureStyleDegree, out double azDeg) ? azDeg : 1.0;
                }
                break;

            case "amazon_polly":
                args["engine"] = input.TryGet(AudioLabParams.PollyEngine, out string polEng) ? polEng : "neural";
                args["voice_id"] = input.TryGet(AudioLabParams.PollyVoice, out string polVoice) ? polVoice : "Joanna";
                break;

            case "stableaudio_music":
                args["infer_step"] = input.TryGet(AudioLabParams.StableAudioSteps, out int saSteps) ? saSteps : 8;
                // Stable Audio Open Small is documented as variable-length up to 11 s; asking for more
                // than it was trained for produces truncated or degraded output.
                if (args.TryGetValue("duration", out object saDur) && saDur is double sd && sd > 11.0)
                {
                    args["duration"] = 11.0;
                }
                break;

            case "suno_music":
            case "udio_music":
                // Both cloud handlers read `text`, not the category block's `prompt`.
                args["text"] = input.Get(T2IParamTypes.Prompt, "");
                if (input.TryGet(AudioLabParams.MusicStyle, out string musicStyle) && !string.IsNullOrEmpty(musicStyle))
                {
                    args["style"] = musicStyle;
                }
                if (input.TryGet(AudioLabParams.Instrumental, out string musicInst))
                {
                    args["instrumental"] = musicInst;
                }
                break;

            case "acestep_music":
                // ACE-Step semantics: the main Prompt is the style/genre, the dedicated Lyrics param is the lyrics.
                // The engine's music handler maps genre→style and prompt→lyrics, so route them accordingly here
                // (overriding the category-level args["prompt"] = main prompt set above).
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.Lyrics, out string ly) ? ly : "[Instrumental]";
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long aceSeed) ? aceSeed : -1L;
                args["infer_step"] = input.TryGet(AudioLabParams.InferStep, out int infStep) ? infStep : 0;   // 0 = model default
                // turbo* variants are distilled for no-CFG sampling, so the 7.0 default actively degrades them.
                // An explicit user value always wins; only the fallback is variant-aware.
                bool aceIsTurbo = modelDef?.Id?.StartsWith("turbo", StringComparison.OrdinalIgnoreCase) == true
                    || modelDef?.Id?.StartsWith("xl-turbo", StringComparison.OrdinalIgnoreCase) == true;
                args["guidance_scale"] = input.TryGet(AudioLabParams.ACEGuidanceScale, out double aceGuide) ? aceGuide : (aceIsTurbo ? 1.0 : 7.0);
                args["instrumental"] = input.TryGet(AudioLabParams.Instrumental, out string aceInst) ? aceInst : "false";
                // Prefer the CORE Swarm audio params (what Swarm users expect + the HartsyInference ACE-Step path
                // reads); fall back to AudioLab's own params for existing AudioLab-UI workflows.
                int aceBpmVal = input.TryGet(T2IParamTypes.Text2AudioBPM, out long coreBpm) ? (int)coreBpm
                    : input.TryGet(AudioLabParams.BPM, out int aceBpm) ? aceBpm : 0;
                // 0 = omit so the LM auto-detects, matching upstream's default of none.
                if (aceBpmVal > 0) args["bpm"] = aceBpmVal;
                string keyScale = input.TryGet(T2IParamTypes.Text2AudioKeyScale, out string coreKey) && !string.IsNullOrEmpty(coreKey) ? coreKey
                    : input.TryGet(AudioLabParams.KeyScale, out string aceKey) ? aceKey : null;
                if (!string.IsNullOrEmpty(keyScale)) args["key_scale"] = keyScale;
                args["time_signature"] = input.TryGet(T2IParamTypes.Text2AudioTimeSignature, out string coreTs) && !string.IsNullOrEmpty(coreTs) ? coreTs
                    : input.TryGet(AudioLabParams.TimeSignature, out string aceTs) ? aceTs : "4";
                args["vocal_language"] = input.TryGet(T2IParamTypes.Text2AudioLanguage, out string coreLang) && !string.IsNullOrEmpty(coreLang) ? coreLang
                    : input.TryGet(AudioLabParams.VocalLanguage, out string aceVl) ? aceVl : "unknown";
                // 0 = let the model decide; the shift1/shift3 checkpoints are trained at those exact values,
                // so name them explicitly rather than relying on the engine to infer from the checkpoint.
                double aceShiftDefault = modelDef?.Id switch
                {
                    "turbo-shift1" => 1.0,
                    "turbo-shift3" => 3.0,
                    _ => 0.0,
                };
                args["shift"] = input.TryGet(AudioLabParams.ACEShift, out double aceShift) ? aceShift : aceShiftDefault;
                args["infer_method"] = input.TryGet(AudioLabParams.InferMethod, out string aceIm) ? aceIm : "ode";
                args["use_adg"] = input.TryGet(AudioLabParams.UseADG, out string aceAdg) ? aceAdg : "false";
                args["cfg_interval_start"] = input.TryGet(AudioLabParams.CFGIntervalStart, out double aceCfgS) ? aceCfgS : 0.0;
                args["cfg_interval_end"] = input.TryGet(AudioLabParams.CFGIntervalEnd, out double aceCfgE) ? aceCfgE : 1.0;
                // LM planner params (acestep_lm_params) — TODO: integrate with SwarmUI AbstractLLMBackend
                args["lm_model"] = input.TryGet(AudioLabParams.ACELMModel, out string aceLm) ? aceLm : "none";
                args["thinking"] = input.TryGet(AudioLabParams.Thinking, out string aceThink) ? aceThink : "true";
                args["lm_temperature"] = input.TryGet(AudioLabParams.LMTemperature, out double aceLmTemp) ? aceLmTemp : 0.85;
                args["lm_cfg_scale"] = input.TryGet(AudioLabParams.LMCFGScale, out double aceLmCfg) ? aceLmCfg : 2.0;
                args["lm_top_k"] = input.TryGet(AudioLabParams.LMTopK, out int aceLmTopK) ? aceLmTopK : 0;
                args["lm_top_p"] = input.TryGet(AudioLabParams.LMTopP, out double aceLmTopP) ? aceLmTopP : 0.9;
                if (input.TryGet(AudioLabParams.LMNegativePrompt, out string aceLmNeg) && !string.IsNullOrEmpty(aceLmNeg))
                    args["lm_negative_prompt"] = aceLmNeg;
                args["use_cot_metas"] = input.TryGet(AudioLabParams.UseCotMetas, out string aceCotM) ? aceCotM : "true";
                args["use_cot_caption"] = input.TryGet(AudioLabParams.UseCotCaption, out string aceCotC) ? aceCotC : "true";
                args["use_cot_language"] = input.TryGet(AudioLabParams.UseCotLanguage, out string aceCotL) ? aceCotL : "true";
                // Task params (acestep_task_params)
                args["task_type"] = input.TryGet(AudioLabParams.ACETaskType, out string aceTask) ? aceTask : "text2music";
                string aceSrcAudio = GetBase64Audio(input, AudioLabParams.ACESourceAudio);
                if (!string.IsNullOrEmpty(aceSrcAudio))
                    args["src_audio"] = aceSrcAudio;
                string aceRefAudio = GetBase64Audio(input, AudioLabParams.ACEReferenceAudio);
                if (!string.IsNullOrEmpty(aceRefAudio))
                    args["reference_audio"] = aceRefAudio;
                args["repaint_start"] = input.TryGet(AudioLabParams.RepaintStart, out double aceRpS) ? aceRpS : 0.0;
                args["repaint_end"] = input.TryGet(AudioLabParams.RepaintEnd, out double aceRpE) ? aceRpE : -1.0;
                args["cover_strength"] = input.TryGet(AudioLabParams.CoverStrength, out double aceCovStr) ? aceCovStr : 1.0;
                args["cover_noise_strength"] = input.TryGet(AudioLabParams.CoverNoiseStrength, out double aceCovNs) ? aceCovNs : 0.0;
                break;


            case "yue_music":
                // YuE semantics (mirror ACE-Step): main Prompt = genre/style tags → genre; the dedicated
                // Lyrics param = lyrics → prompt. EncodeStage1Prompt(genre, prompt) consumes them in that order.
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.YuELyrics, out string yueLy) ? yueLy : "";
                args["max_new_tokens"] = input.TryGet(AudioLabParams.YuEMaxTokens, out int yueTokens) ? yueTokens : 3000;
                args["quantization"] = input.TryGet(AudioLabParams.YuEQuantization, out string yueQuant) ? yueQuant : "fp16";
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long yueSeed) ? yueSeed : -1L;
                args["stage2_batch_size"] = input.TryGet(AudioLabParams.YuEStage2BatchSize, out int yueS2Bs) ? yueS2Bs : 4;
                args["temperature"] = input.TryGet(AudioLabParams.YuETemperature, out double yueTemp) ? yueTemp : 0.9;
                args["top_p"] = input.TryGet(AudioLabParams.YuETopP, out double yueTopP) ? yueTopP : 0.93;
                args["repetition_penalty"] = input.TryGet(AudioLabParams.YuERepetitionPenalty, out double yueRepPen) ? yueRepPen : 1.2;
                args["run_n_segments"] = input.TryGet(AudioLabParams.YuESegments, out int yueSegs) ? yueSegs : 2;
                break;

            case "heartlib_music":
                // HeartMuLa semantics (mirror ACE-Step): main Prompt = vocal-style tags → genre; the dedicated
                // Lyrics param = lyrics → prompt. MusicHandler maps genre→HeartMulaTags, prompt→HeartMulaLyrics.
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.HeartLibLyrics, out string hlLy) ? hlLy : "";
                args["cfg_scale"] = input.TryGet(AudioLabParams.HeartLibCFGScale, out double hlCfg) ? hlCfg : 1.5;
                args["temperature"] = input.TryGet(AudioLabParams.HeartLibTemperature, out double hlTemp) ? hlTemp : 1.0;
                args["topk"] = input.TryGet(AudioLabParams.HeartLibTopK, out int hlTopK) ? hlTopK : 50;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long hlSeed) ? hlSeed : -1L;
                break;

            case "minimax_music3":
                // Same split ACE-Step and HeartMuLa use, and the one the engine's MusicRequest already speaks:
                // the main Prompt carries the music description (genre), the dedicated Lyrics param carries the
                // words (prompt).
                args["genre"] = input.Get(T2IParamTypes.Prompt, "");
                args["prompt"] = input.TryGet(AudioLabParams.MiniMaxMusic3Lyrics, out string mmLy) ? mmLy : "";
                args["cfg_scale"] = input.TryGet(AudioLabParams.MiniMaxMusic3CFGScale, out double mmCfg) ? mmCfg : 1.7;
                args["infer_step"] = input.TryGet(AudioLabParams.MiniMaxMusic3Steps, out int mmSteps) ? mmSteps : 30;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long mmSeed) ? mmSeed : -1L;
                break;

            case "whisper_stt":
                args["task"] = input.TryGet(AudioLabParams.WhisperTask, out string whisperTask) ? whisperTask : "transcribe";
                args["beam_size"] = input.TryGet(AudioLabParams.WhisperBeamSize, out int wBeam) ? wBeam : 5;
                if (input.TryGet(AudioLabParams.WhisperInitialPrompt, out string wPrompt) && !string.IsNullOrEmpty(wPrompt))
                    args["initial_prompt"] = wPrompt;
                break;

            case "rvc_clone":
                args["pitch_shift"] = input.TryGet(AudioLabParams.PitchShift, out int pitchShift) ? pitchShift : 0;
                args["f0method"] = input.TryGet(AudioLabParams.F0Method, out string f0m) ? f0m : "rmvpe";
                args["index_rate"] = input.TryGet(AudioLabParams.IndexRate, out double idxRate) ? idxRate : 0.5;
                args["rms_mix_rate"] = input.TryGet(AudioLabParams.RMSMixRate, out double rmsMix) ? rmsMix : 1.0;
                args["protect"] = input.TryGet(AudioLabParams.Protect, out double prot) ? prot : 0.33;
                break;

            case "gptsovits_clone":
                args["text"] = input.Get(T2IParamTypes.Prompt, "");
                // Must be ref_text: that is the key SpeechRequest reads into RefText, which GptSoVitsModel
                // aligns the reference clip against. As "prompt_text" it was silently dropped.
                if (input.TryGet(AudioLabParams.ClonePromptText, out string gpt) && !string.IsNullOrEmpty(gpt))
                    args["ref_text"] = gpt;
                args["language"] = input.TryGet(AudioLabParams.CloneLanguage, out string gl) ? gl : "en";
                break;

            case "demucs_fx":
                args["overlap"] = input.TryGet(AudioLabParams.Overlap, out double overlap) ? overlap : 0.25;
                args["shifts"] = input.TryGet(AudioLabParams.Shifts, out int shifts) ? shifts : 0;
                args["segment"] = input.TryGet(AudioLabParams.DemucsSegment, out double demSeg) ? demSeg : 7.8;
                args["seed"] = input.TryGet(T2IParamTypes.Seed, out long demSeed) ? demSeed : 0L;
                break;

            case "resemble_enhance_fx":
                args["nfe"] = input.TryGet(AudioLabParams.EnhanceNFE, out int nfe) ? nfe : 64;
                args["solver"] = input.TryGet(AudioLabParams.EnhanceSolver, out string solver) ? solver : "midpoint";
                args["lambd"] = input.TryGet(AudioLabParams.EnhanceLambda, out double lambd) ? lambd : 0.1;
                args["tau"] = input.TryGet(AudioLabParams.EnhanceTau, out double tau) ? tau : 0.5;
                break;
        }

        return args;
    }

    #endregion

    #region Chunk Splitting and WAV Helpers

    /// <summary>Common abbreviations whose trailing period should NOT be treated as a sentence boundary.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc",
        "Inc", "Ltd", "Corp", "Ave", "Blvd", "Dept", "Est", "Fig", "Gen",
        "Gov", "No", "Sgt", "Vol"
    };

    /// <summary>Check whether a word ends with clause/sentence punctuation (ignoring abbreviations).</summary>
    private static bool EndsWithBreakPunctuation(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        char last = word[^1];
        if (last == '!' || last == '?' || last == ';' || last == ':' || last == '\u2014') return true;
        if (last == '.' || last == ',')
        {
            // Check for abbreviation — strip trailing period and see if base is a known abbr
            string baseName = word.TrimEnd('.', ',');
            if (Abbreviations.Contains(baseName)) return false;
            // Single-letter initials like "U." or "A." — not a break
            if (baseName.Length == 1 && char.IsUpper(baseName[0])) return false;
            return true;
        }
        return false;
    }

    /// <summary>Check whether a word ends with a sentence-terminal punctuation mark (. ! ?)
    /// while respecting abbreviations. Used by sentence-level splitting.</summary>
    private static bool EndsWithSentencePunctuation(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        char last = word[^1];
        if (last == '!' || last == '?') return true;
        if (last == '.')
        {
            string baseName = word.TrimEnd('.');
            if (Abbreviations.Contains(baseName)) return false;
            if (baseName.Length == 1 && char.IsUpper(baseName[0])) return false;
            return true;
        }
        return false;
    }

    /// <summary>Splits text into chunks using the given semantic mode.
    /// Returns null if fewer than 2 chunks (caller should use the normal non-streaming path).</summary>
    private static List<string> SplitIntoChunks(string text, string mode)
    {
        if (string.IsNullOrWhiteSpace(text) || mode == "off") return null;

        List<string> chunks = mode switch
        {
            "word" => SplitPerWord(text),
            "phrase" => SplitByPhrases(text),
            "sentence" => SplitBySentences(text),
            "paragraph" => SplitByParagraphs(text),
            _ => null
        };

        // Only stream if we got 2+ chunks
        return chunks is { Count: >= 2 } ? chunks : null;
    }

    /// <summary>Each word becomes its own chunk.</summary>
    private static List<string> SplitPerWord(string text)
    {
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 ? new List<string>(words) : null;
    }

    /// <summary>Splits into short phrases of ~5 words, snapping to nearby punctuation.</summary>
    private static List<string> SplitByPhrases(string text)
    {
        const int wordsPerChunk = 5;
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;

        List<string> chunks = [];
        int pos = 0;

        while (pos < words.Length)
        {
            int remaining = words.Length - pos;
            if (remaining <= wordsPerChunk + 2)
            {
                chunks.Add(string.Join(' ', words, pos, remaining));
                break;
            }

            int target = pos + wordsPerChunk;
            int bestBreak = -1;
            for (int probe = Math.Max(pos + 1, target - 2); probe <= Math.Min(words.Length - 1, target + 2); probe++)
            {
                if (EndsWithBreakPunctuation(words[probe]))
                {
                    bestBreak = probe + 1;
                    break;
                }
            }

            int end = bestBreak > 0 ? bestBreak : target;
            end = Math.Min(end, words.Length);
            chunks.Add(string.Join(' ', words, pos, end - pos));
            pos = end;
        }

        // Merge tiny trailing chunk
        if (chunks.Count >= 2)
        {
            string lastChunk = chunks[^1];
            int lastWordCount = lastChunk.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount < 2)
            {
                chunks[^2] = chunks[^2] + " " + lastChunk;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }

    /// <summary>Splits on sentence boundaries (. ! ?) while respecting abbreviations.</summary>
    private static List<string> SplitBySentences(string text)
    {
        string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;

        List<string> chunks = [];
        int sentenceStart = 0;

        for (int i = 0; i < words.Length; i++)
        {
            if (EndsWithSentencePunctuation(words[i]) || i == words.Length - 1)
            {
                int count = i - sentenceStart + 1;
                chunks.Add(string.Join(' ', words, sentenceStart, count));
                sentenceStart = i + 1;
            }
        }

        // Merge a very short trailing chunk (1-2 words) into the previous sentence
        if (chunks.Count >= 2)
        {
            string lastChunk = chunks[^1];
            int lastWordCount = lastChunk.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount <= 2)
            {
                chunks[^2] = chunks[^2] + " " + lastChunk;
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }

    /// <summary>Splits on paragraph boundaries (double newlines).</summary>
    private static List<string> SplitByParagraphs(string text)
    {
        // Split on double newlines (handles \r\n and \n)
        string[] paragraphs = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        List<string> chunks = [];
        foreach (string p in paragraphs)
        {
            string trimmed = p.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                chunks.Add(trimmed);
            }
        }

        // If text has no paragraph breaks, fall back to sentence splitting
        if (chunks.Count < 2)
        {
            return SplitBySentences(text);
        }

        return chunks;
    }

    /// <summary>Builds a complete WAV file from concatenated PCM data.</summary>
    private static byte[] BuildWavFromPcm(List<byte[]> pcmChunks, int sampleRate, int channels, int bitsPerSample)
    {
        int totalPcmBytes = 0;
        foreach (byte[] chunk in pcmChunks)
        {
            totalPcmBytes += chunk.Length;
        }

        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + totalPcmBytes);  // File size - 8
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);                  // Chunk size
        bw.Write((short)1);            // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(totalPcmBytes);
        foreach (byte[] chunk in pcmChunks)
        {
            bw.Write(chunk);
        }

        return ms.ToArray();
    }

    /// <summary>Converts normalized <c>[-1,1]</c> float samples (an Engine <see cref="AudioChunk"/>'s native
    /// format) to little-endian 16-bit PCM bytes, the format every WAV helper in this file already works in.</summary>
    private static byte[] FloatToPcm16(float[] samples)
    {
        byte[] pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(MathF.Round(samples[i] * 32767f), -32768f, 32767f);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>Generates a minimal silent WAV file (all zeros) for use as a placeholder output.</summary>
    private static byte[] GenerateSilentWav(int sampleRate = 16000, int durationMs = 100)
    {
        int numSamples = sampleRate * durationMs / 1000;
        byte[] silence = new byte[numSamples * 2]; // 16-bit mono = 2 bytes per sample
        return BuildWavFromPcm([silence], sampleRate, channels: 1, bitsPerSample: 16);
    }

    #endregion
}
