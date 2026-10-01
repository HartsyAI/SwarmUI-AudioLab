using System.Runtime.CompilerServices;

// Lets the extension's Tests/ project exercise internal, pure-logic helpers (eg
// AudioEngineBridge.OpenResidentPinCoreAsync/ClearResidentPinCore) without needing a live Engine to drive them.
[assembly: InternalsVisibleTo("Hartsy.Extensions.AudioLab.Tests")]
