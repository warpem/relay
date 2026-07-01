using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Refund.Utils
{
    /// <summary>
    /// Utility methods for job log parsing and progress tracking.
    /// Contains only generic, reusable code that can be shared across job types.
    /// </summary>
    public static class JobTools
    {
        #region Log Processing Helpers

        /// <summary>
        /// Creates the results directory if it doesn't exist
        /// </summary>
        /// <param name="resultsDir">Path to results directory</param>
        /// <returns>True if directory exists or was created successfully</returns>
        public static void EnsureResultsDirectory(string resultsDir)
        {
            if (!Directory.Exists(resultsDir))
                Directory.CreateDirectory(resultsDir);
        }

        /// <summary>
        /// Checks if a log file exists and has changed since last check
        /// </summary>
        /// <param name="logPath">Path to log file</param>
        /// <param name="lastLogSize">Reference to previous log size</param>
        /// <returns>True if log file has changed, false if unchanged or doesn't exist</returns>
        public static bool HasLogFileChanged(string logPath, ref long lastLogSize)
        {
            if (!File.Exists(logPath))
                return false;

            long currentSize = new FileInfo(logPath).Length;
            if (currentSize == lastLogSize)
                return false;

            lastLogSize = currentSize;
            return true;
        }

        /// <summary>
        /// Cleans log lines of terminal control characters and handles progress bar formatting
        /// </summary>
        /// <param name="logLines">Raw log lines</param>
        /// <returns>Cleaned log lines</returns>
        public static string[] CleanProgressBarLines(string[] logLines)
        {
            string[] cleanedLines = new string[logLines.Length];
            Array.Copy(logLines, cleanedLines, logLines.Length);
            
            for (int i = 0; i < cleanedLines.Length; i++)
                if (cleanedLines[i].Contains('\r'))
                    // Take only content after the last carriage return
                    cleanedLines[i] = cleanedLines[i].Substring(cleanedLines[i].LastIndexOf('\r') + 1);

            return cleanedLines;
        }

        /// <summary>
        /// Reads the tail of a log file and returns its last <paramref name="maxLines"/> non-empty
        /// lines. Only the final <paramref name="maxWindowBytes"/> bytes are read (logs can be huge);
        /// if the file exceeds the window the first, partially-read line is dropped. CRLF endings are
        /// normalized and \r progress-bar lines are collapsed to their final segment.
        /// </summary>
        /// <param name="path">Path to the log file.</param>
        /// <param name="maxLines">Maximum number of lines to return.</param>
        /// <param name="maxWindowBytes">Maximum number of trailing bytes to read.</param>
        /// <returns>The cleaned trailing lines, or an empty array if the file does not exist.</returns>
        public static string[] ReadLogTail(string path, int maxLines, int maxWindowBytes = 512 * 1024)
        {
            if (!File.Exists(path))
                return Array.Empty<string>();

            string content;
            bool truncated;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, stream.Length - maxWindowBytes);
                truncated = start > 0;
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
            }

            string[] rawLines = content.Split('\n');
            IEnumerable<string> lines = truncated ? rawLines.Skip(1) : rawLines;

            // Strip the CR of CRLF endings first (leaving embedded \r for CleanProgressBarLines),
            // then collapse progress-bar lines, then drop blanks and take the last N.
            string[] stripped = lines
                .Select(l => l.EndsWith("\r") ? l.Substring(0, l.Length - 1) : l)
                .ToArray();

            return CleanProgressBarLines(stripped)
                .Where(l => l.Length > 0)
                .TakeLast(maxLines)
                .ToArray();
        }

        /// <summary>
        /// Searches for a specific pattern in log lines
        /// </summary>
        /// <param name="logLines">Log lines to search</param>
        /// <param name="pattern">Pattern to search for</param>
        /// <returns>Index of first matching line, or -1 if not found</returns>
        public static int FindLineContaining(string[] logLines, string pattern)
        {
            for (int i = 0; i < logLines.Length; i++)
                if (logLines[i].Contains(pattern))
                    return i;
            
            return -1;
        }

        #endregion

        #region Result File Checking

        /// <summary>
        /// Checks if specific required result files exist
        /// </summary>
        /// <param name="resultFilePaths">Paths to required result files</param>
        /// <returns>True if all files exist</returns>
        public static bool DoResultFilesExist(params string[] resultFilePaths)
        {
            foreach (string path in resultFilePaths)
                if (!File.Exists(path))
                    return false;
            
            return true;
        }
        
        #endregion
        
        #region File Operations
        
        /// <summary>
        /// Safely writes content to a file with atomic replace semantics
        /// </summary>
        /// <param name="content">The content to write to the file</param>
        /// <param name="path">The destination file path</param>
        /// <param name="timeoutMilliseconds">Maximum time in milliseconds to retry if file is locked</param>
        public static void WriteLogFile(string content, string path, int timeoutMilliseconds = 5_000)
        {
            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            
            // Create a temporary file
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);
            
            // Start a timer to track attempts
            var watch = new Stopwatch();
            watch.Start();
            
            // Try to replace the destination file with the temporary file
            while (true)
            {
                try
                {
                    // Attempt to move the temp file to the destination
                    File.Move(tempPath, path, true);
                    break; // Success
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Check if we've exceeded the timeout
                    if (watch.ElapsedMilliseconds > timeoutMilliseconds)
                        throw new TimeoutException($"Failed to replace file {path} after {timeoutMilliseconds}ms");
                    
                    // Wait a short time before retrying
                    Thread.Sleep(50);
                }
            }
        }
        
        #endregion
    }
}