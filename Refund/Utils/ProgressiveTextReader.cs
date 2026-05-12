using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Serilog;

namespace Refund.Utils
{
    /// <summary>
    /// A utility class that reads a text file and returns only new content 
    /// that has been added since the last read operation.
    /// </summary>
    public class ProgressiveTextReader
    {
        private readonly string _filePath;
        private long _lastPosition = 0;
        private bool _initialized = false;

        /// <summary>
        /// Initializes a new instance of the ProgressiveTextReader class.
        /// </summary>
        /// <param name="filePath">The path to the file to read.</param>
        public ProgressiveTextReader(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>
        /// Reads all lines that have been added to the file since the last read.
        /// </summary>
        /// <returns>The new lines added to the file or an empty array if no changes.</returns>
        public string[] ReadNewLines()
        {
            List<string> newLines = new List<string>();

            try
            {
                if (!File.Exists(_filePath))
                {
                    return Array.Empty<string>();
                }

                using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!_initialized)
                        _initialized = true;

                    // Check if there's new content
                    if (fs.Length <= _lastPosition)
                    {
                        return Array.Empty<string>();
                    }

                    // Move to the last read position
                    fs.Seek(_lastPosition, SeekOrigin.Begin);

                    // Read the new content
                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true, 1024, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            newLines.Add(line);
                        }

                        // Update the last position
                        _lastPosition = fs.Position;
                    }
                }
            }
            catch (IOException ex)
            {
                // Handle file access issues gracefully (e.g., file being written to)
                Log.ForContext<ProgressiveTextReader>().Warning(ex, "Could not read file {FilePath}", _filePath);
            }

            return newLines.ToArray();
        }

        /// <summary>
        /// Reads all new content as a single string with original line breaks.
        /// </summary>
        /// <returns>The new content added to the file or an empty string if no changes.</returns>
        public string ReadNewContent()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Log.ForContext<ProgressiveTextReader>().Debug("File {FilePath} does not exist", _filePath);
                    return string.Empty;
                }

                using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!_initialized)
                        _initialized = true;

                    // Check if there's new content
                    if (fs.Length <= _lastPosition)
                    {
                        return string.Empty;
                    }

                    // Move to the last read position
                    fs.Seek(_lastPosition, SeekOrigin.Begin);

                    // Read the new content
                    byte[] buffer = new byte[fs.Length - _lastPosition];
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    _lastPosition = fs.Position;

                    return Encoding.UTF8.GetString(buffer, 0, bytesRead);
                }
            }
            catch (IOException ex)
            {
                // Handle file access issues gracefully
                Log.ForContext<ProgressiveTextReader>().Warning(ex, "Could not read file {FilePath}", _filePath);
                return string.Empty;
            }
        }

        /// <summary>
        /// Resets the reader to start reading from the beginning of the file.
        /// </summary>
        public void Reset()
        {
            _lastPosition = 0;
            _initialized = false;
        }

        /// <summary>
        /// Resets the reader to treat the current end of file as the starting point.
        /// </summary>
        public void SkipExistingContent()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        _lastPosition = fs.Length;
                        _initialized = true;
                    }
                }
                else
                {
                    _lastPosition = 0;
                    _initialized = true;
                }
            }
            catch (IOException ex)
            {
                Log.ForContext<ProgressiveTextReader>().Warning(ex, "Could not access file {FilePath}", _filePath);
                _lastPosition = 0;
                _initialized = true;
            }
        }
    }
}