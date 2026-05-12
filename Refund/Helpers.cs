using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refund;

/// <summary>
/// Provides utility methods for common operations used throughout the application,
/// including cryptographic functions, array comparison, and path validation.
/// </summary>
public static class Helper
{
    /// <summary>
    /// Computes the SHA1 hash of a string and returns the result as a Base64 string.
    /// This method is useful for creating unique identifiers or hashing sensitive data.
    /// </summary>
    /// <param name="data">The string data to hash</param>
    /// <returns>Base64-encoded SHA1 hash of the input string</returns>
    public static string ComputeSHA1(string data) => ComputeSHA1(data: Encoding.Unicode.GetBytes(s: data));

    /// <summary>
    /// Computes the SHA1 hash of a byte array and returns the result as a Base64 string.
    /// This is the core implementation of the hash function used by other SHA1 methods.
    /// </summary>
    /// <param name="data">The byte array to hash</param>
    /// <returns>Base64-encoded SHA1 hash of the input data</returns>
    public static string ComputeSHA1(byte[] data)
    {
        using(var hasher = SHA1.Create())
        {
            var HashBytes = hasher.ComputeHash(buffer: data);

            return Convert.ToBase64String(inArray: HashBytes);
        }
    }

    /// <summary>
    /// Determines whether two arrays contain the same elements in the same order.
    /// Uses the Equals method of the element type for comparison.
    /// </summary>
    /// <typeparam name="T">The type of the array elements</typeparam>
    /// <param name="array1">First array to compare</param>
    /// <param name="array2">Second array to compare</param>
    /// <returns>True if arrays have identical elements in the same order; otherwise, false</returns>
    /// <remarks>
    /// This method is primarily used within the RelayBase class to determine whether array 
    /// properties have changed during object adoption, allowing for efficient property updates
    /// without unnecessary notifications. It performs an element-by-element comparison rather 
    /// than reference equality.
    /// </remarks>
    public static bool AreElementsEqual<T>(T[] array1, T[] array2)
    {
        if(array1.Length != array2.Length)
        {
            return false;
        }

        for(var i = 0; i < array1.Length; i++)
        {
            if(!array1[i].Equals(obj: array2[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a file or directory path against multiple criteria:
    /// - Checks if the path is null, empty, or not rooted
    /// - Verifies that the path exists and is accessible
    /// - For files, checks if the extension is in the allowed list
    /// 
    /// This method is used extensively in job parameters validation to ensure paths
    /// reference valid and accessible resources before job execution.
    /// </summary>
    /// <param name="path">The file or directory path to validate</param>
    /// <param name="extensions">List of allowed file extensions (for file paths only)</param>
    /// <returns>Empty string if valid; otherwise, a string with error messages</returns>
    /// <remarks>
    /// Used primarily in import jobs (ImportParticles, ImportMap, ImportTestMap) as part of their
    /// ValidateParameters methods to ensure file paths are properly formatted, accessible, and 
    /// have the correct extensions. The method returns detailed error messages that can be displayed
    /// directly to users in the UI or logged for troubleshooting.
    ///
    /// Extension validation supports wildcard patterns like "*.mrc" and handles both formats with
    /// and without the leading dot.
    /// </remarks>
    public static string ValidatePath(string path, List<string> extensions)
    {
        var validationMessage = new StringBuilder();

        switch(string.IsNullOrWhiteSpace(value: path))
        {
            case true:
                validationMessage.Append(value: "Path is null, empty, or whitespace. ");

                break;
            case false when !Path.IsPathRooted(path: path):
                validationMessage.Append(value: "Path is not well-formed or not rooted. ");

                break;
        }

        if(validationMessage.Length == 0)
        {
            var isPathValid = File.Exists(path: path) || Directory.Exists(path: path);

            if(!isPathValid)
            {
                validationMessage.Append(value: "Path does not exist. ");
            }
            else
            {
                try
                {
                    if(File.Exists(path: path))
                    {
                        using(var stream = File.Open(path: path, mode: FileMode.Open, access: FileAccess.Read))
                        {
                            // Successfully opened the file, so it's not restricted
                        }
                    }
                    else if(Directory.Exists(path: path))
                    {
                        Directory.GetFiles(path: path);
                        // Successfully accessed the directory, so it's not restricted
                    }
                }
                catch(UnauthorizedAccessException)
                {
                    validationMessage.Append(value: "Path is restricted. ");
                }
                catch(Exception ex)
                {
                    validationMessage.Append(handler: $"Error accessing path: {ex.Message}. ");
                }
            }

            if(File.Exists(path: path))
            {
                var extension = Path.GetExtension(path: path)?.ToLowerInvariant();

                var validExtensions = extensions
                    .Select(selector: ext => ext.Replace(oldValue: "*.", newValue: "").TrimStart(trimChar: '.'))
                    .ToList();

                var isExtensionValid = !string.IsNullOrEmpty(value: extension) &&
                                       validExtensions.Contains(item: extension.TrimStart(trimChar: '.'));

                if(!isExtensionValid)
                {
                    validationMessage.Append(value: "Invalid file extension. ");
                }
            }
        }

        return validationMessage.Length > 0
            ? validationMessage.ToString()
            : string.Empty;
    }
}

/// <summary>
/// Custom JSON converter for DateTime values that ensures a consistent serialization format.
/// This converter uses the RFC1123 ('R') format standard for serialization, which provides
/// a well-defined, machine-readable representation of dates.
/// 
/// The deserialization process expects Unix timestamp milliseconds and converts to DateTime.
/// The serialization process writes DateTime values in the RFC1123 format.
/// </summary>
/// <remarks>
/// Original implementation adapted from https://stackoverflow.com/a/66312313/11267786
/// </remarks>
public class DateTimeConverterForCustomStandardFormatR : JsonConverter<DateTime>
{
    /// <summary>
    /// Converts a JSON value to a DateTime by interpreting it as Unix milliseconds.
    /// </summary>
    /// <param name="reader">The JSON reader</param>
    /// <param name="typeToConvert">The target type (DateTime)</param>
    /// <param name="options">The serializer options</param>
    /// <returns>The converted DateTime value</returns>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.UnixEpoch.AddMilliseconds(value: reader.GetInt64());

    /// <summary>
    /// Converts a DateTime to its JSON representation using RFC1123 format.
    /// Uses a fixed-size buffer as an optimization since the RFC1123 format
    /// always produces a string of exactly 29 bytes.
    /// </summary>
    /// <param name="writer">The JSON writer</param>
    /// <param name="value">The DateTime value to write</param>
    /// <param name="options">The serializer options</param>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // The "R" standard format will always be 29 bytes.
        Span<byte> utf8Date = new byte[29];

        var result = Utf8Formatter.TryFormat(value: value, destination: utf8Date, bytesWritten: out _,
            format: new StandardFormat(symbol: 'R'));

        Debug.Assert(condition: result);

        writer.WriteStringValue(utf8Value: utf8Date);
    }
}