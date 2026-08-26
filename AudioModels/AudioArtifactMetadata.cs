using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioModels;

/// <summary>Reads the <c>modelspec.*</c> / <c>hartsy.*</c> identity block out of an audio artifact.
///
/// <para>SwarmUI's own metadata load keeps only the fields it models on <c>ModelMetadataStore</c>, and the
/// <c>hartsy.*</c> keys are not among them, so the block is read straight from the file. Safetensors headers
/// are parsed here rather than through the engine, which keeps model discovery working even when the engine
/// DLLs failed to load.</para>
///
/// <para>The <c>.swarm.json</c> sidecar is checked first because it is the escape hatch for formats whose
/// header cannot carry metadata (GGUF quantizations, and ONNX once it is scanned at all), and the same
/// mechanism core uses for <c>.engine</c> files.</para></summary>
public static class AudioArtifactMetadata
{
    private const long MaxHeaderBytes = 64 * 1024 * 1024;

    /// <summary>Identity keys for one artifact, or null when it carries none.</summary>
    public static IReadOnlyDictionary<string, string> Read(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }
        try
        {
            IReadOnlyDictionary<string, string> sidecar = ReadSidecar(filePath);
            if (sidecar is not null)
            {
                return sidecar;
            }
            if (filePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".sft", StringComparison.OrdinalIgnoreCase))
            {
                return ReadSafetensorsHeader(filePath);
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Could not read identity metadata from '{filePath}': {ex.Message}");
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadSidecar(string filePath)
    {
        string sidecarPath = Path.ChangeExtension(filePath, ".swarm.json");
        if (sidecarPath is null || !File.Exists(sidecarPath))
        {
            return null;
        }
        JObject parsed = JObject.Parse(File.ReadAllText(sidecarPath));
        Dictionary<string, string> result = [];
        foreach (JProperty prop in parsed.Properties())
        {
            if (prop.Value.Type == JTokenType.String)
            {
                result[prop.Name] = prop.Value.Value<string>();
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyDictionary<string, string> ReadSafetensorsHeader(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] lengthBytes = new byte[8];
        stream.ReadExactly(lengthBytes);
        long headerLength = BitConverter.ToInt64(lengthBytes);
        if (headerLength <= 0 || headerLength > MaxHeaderBytes || headerLength > stream.Length - 8)
        {
            return null;
        }
        byte[] headerBytes = new byte[headerLength];
        stream.ReadExactly(headerBytes);
        JObject header = JObject.Parse(System.Text.Encoding.UTF8.GetString(headerBytes));
        if (header["__metadata__"] is not JObject metadata)
        {
            return null;
        }
        Dictionary<string, string> result = [];
        foreach (JProperty prop in metadata.Properties())
        {
            if (prop.Value.Type == JTokenType.String)
            {
                result[prop.Name] = prop.Value.Value<string>();
            }
        }
        return result.Count > 0 ? result : null;
    }
}
