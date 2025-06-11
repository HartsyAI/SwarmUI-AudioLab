using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;

namespace Hartsy.Extensions.VoiceAssistant.Common;

/// <summary>
/// Centralized error handling utilities for consistent error management across the extension.
/// </summary>
public static class ErrorHandling
{
    /// <summary>
    /// Creates a standardized error response for API endpoints.
    /// </summary>
    public static JObject CreateErrorResponse(string message, string errorCode = null, Exception exception = null)
    {
        var response = new JObject
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };

        if (!string.IsNullOrEmpty(errorCode))
        {
            response["error_code"] = errorCode;
        }

        if (exception != null)
        {
            Logs.Debug($"[VoiceAssistant] Exception details: {exception}");
            // Don't expose internal exception details to the API
            response["error_type"] = exception.GetType().Name;
        }

        return response;
    }

    /// <summary>
    /// Creates a standardized success response for API endpoints.
    /// </summary>
    public static JObject CreateSuccessResponse(object data = null, string message = null)
    {
        var response = new JObject
        {
            ["success"] = true,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };

        if (!string.IsNullOrEmpty(message))
        {
            response["message"] = message;
        }

        if (data != null)
        {
            if (data is JObject jObject)
            {
                foreach (var property in jObject.Properties())
                {
                    response[property.Name] = property.Value;
                }
            }
            else
            {
                response["data"] = JToken.FromObject(data);
            }
        }

        return response;
    }

    /// <summary>
    /// Logs and wraps exceptions for API responses.
    /// </summary>
    public static JObject HandleException(string operation, Exception exception, string userMessage = null)
    {
        string logMessage = $"[VoiceAssistant] Error in {operation}: {exception.Message}";
        string responseMessage = userMessage ?? $"Operation failed: {exception.Message}";

        // Log at appropriate level based on exception type
        if (exception is ArgumentException || exception is InvalidOperationException)
        {
            Logs.Warning(logMessage);
        }
        else
        {
            Logs.Error(logMessage);
            Logs.Debug($"[VoiceAssistant] {operation} stack trace: {exception}");
        }

        return CreateErrorResponse(responseMessage, GetErrorCode(exception), exception);
    }

    /// <summary>
    /// Gets a standardized error code based on exception type.
    /// </summary>
    private static string GetErrorCode(Exception exception)
    {
        return exception switch
        {
            ArgumentException => "INVALID_ARGUMENT",
            InvalidOperationException => "INVALID_OPERATION",
            TimeoutException => "TIMEOUT",
            FileNotFoundException => "FILE_NOT_FOUND",
            UnauthorizedAccessException => "ACCESS_DENIED",
            HttpRequestException => "NETWORK_ERROR",
            _ => "INTERNAL_ERROR"
        };
    }

    /// <summary>
    /// Creates user-friendly error messages for common scenarios.
    /// </summary>
    public static string GetUserFriendlyMessage(Exception exception)
    {
        return exception switch
        {
            ArgumentException => "Invalid input provided. Please check your request and try again.",
            TimeoutException => "The operation timed out. Please try again or check your network connection.",
            FileNotFoundException => "Required files are missing. Please check the installation.",
            UnauthorizedAccessException => "Access denied. Please check permissions.",
            HttpRequestException => "Network error occurred. Please check your connection and try again.",
            InvalidOperationException when exception.Message.Contains("Python") =>
                "Python environment error. Please ensure SwarmUI with ComfyUI is properly installed.",
            InvalidOperationException when exception.Message.Contains("service") =>
                "Voice service is not available. Please start the service first.",
            _ => "An unexpected error occurred. Please try again or contact support."
        };
    }

    /// <summary>
    /// Validates common input parameters and throws appropriate exceptions.
    /// </summary>
    public static class Validation
    {
        public static void RequireNonNull(object value, string paramName)
        {
            if (value == null)
                throw new ArgumentNullException(paramName);
        }

        public static void RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{paramName} cannot be null or empty", paramName);
        }

        public static void RequireValidLanguage(string language)
        {
            RequireNonEmpty(language, nameof(language));
            if (!Configuration.ServiceConfiguration.SupportedLanguages.Contains(language))
                throw new ArgumentException($"Unsupported language: {language}");
        }

        public static void RequireValidVoice(string voice)
        {
            RequireNonEmpty(voice, nameof(voice));
            if (!Configuration.ServiceConfiguration.AvailableVoices.Contains(voice))
                throw new ArgumentException($"Unsupported voice: {voice}");
        }

        public static void RequireValidVolume(float volume)
        {
            if (volume < 0.0f || volume > 1.0f)
                throw new ArgumentException("Volume must be between 0.0 and 1.0");
        }

        public static void RequireValidTextLength(string text)
        {
            RequireNonEmpty(text, nameof(text));
            if (text.Length > Configuration.ServiceConfiguration.MaxTextLength)
                throw new ArgumentException($"Text length exceeds maximum of {Configuration.ServiceConfiguration.MaxTextLength} characters");
        }
    }
}
