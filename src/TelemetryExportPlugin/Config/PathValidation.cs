using System;
using System.IO;

namespace TelemetryExportPlugin.Config
{
    public class PathValidationResult
    {
        public bool Success { get; }
        public string Message { get; }

        private PathValidationResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public static PathValidationResult Ok() => new PathValidationResult(true, null);
        public static PathValidationResult Fail(string message) => new PathValidationResult(false, message);
    }

    /// <summary>
    /// Create-if-missing, warn-if-can't. Used both at config-save time and again
    /// at plugin startup (SCHEMA.md "Startup & config validation") since a drive
    /// that was valid when configured can disappear between sessions.
    /// </summary>
    public static class PathValidation
    {
        public static PathValidationResult ValidateWritableDirectory(string path, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return PathValidationResult.Fail($"{fieldName} is required.");
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                // Directory.CreateDirectory silently succeeds for some invalid UNC/drive
                // cases without throwing; probe with a real write to catch those.
                string probeFile = Path.Combine(path, $".{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, string.Empty);
                File.Delete(probeFile);

                return PathValidationResult.Ok();
            }
            catch (Exception ex)
            {
                return PathValidationResult.Fail($"{fieldName} '{path}' is not usable: {ex.Message}");
            }
        }
    }
}
