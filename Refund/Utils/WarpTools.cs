using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Refund.Utils;

/// <summary>
/// Utility methods for parsing and processing Warp tool logs and outputs
/// </summary>
public static class WarpTools
{
    /// <summary>
    /// Finds the most recent Warp progress line in log output
    /// </summary>
    /// <param name="logLines">Array of log lines to analyze</param>
    /// <returns>Index of the progress line, or -1 if not found</returns>
    public static int FindProgressLine(string[] logLines)
    {
        // Pattern to identify a progress line: n/m, [optional failures], [optional time remaining]
        // This will match formats like "2/200", "4/123, 00:31 remaining", or "4/123, 2 failed, 00:31 remaining"
        string progressPattern = @"\d+/\d+";
        
        // Search from the end to find the most recent progress line
        for (int i = logLines.Length - 1; i >= 0; i--)
        {
            if (Regex.IsMatch(logLines[i], progressPattern))
            {
                return i;
            }
        }
        
        // If no progress line is found, fallback to original approach
        int connectedLine = JobTools.FindLineContaining(logLines, "Connected to");
        if (connectedLine >= 0 && connectedLine < logLines.Length - 1)
            return connectedLine + 1;
            
        return -1;
    }
    
    /// <summary>
    /// Parses Warp-style progress information from a line of text
    /// </summary>
    /// <param name="progressLine">Text line containing progress information</param>
    /// <param name="itemsProcessed">Output for processed items count</param>
    /// <param name="itemsTotal">Output for total items count</param>
    /// <param name="itemsFailed">Output for failed items count</param>
    /// <param name="remainingTime">Output for estimated remaining time</param>
    /// <returns>True if progress was successfully parsed</returns>
    public static bool TryParseProgress(string progressLine, out int itemsProcessed, out int itemsTotal, out int itemsFailed, out string remainingTime)
    {
        itemsProcessed = 0;
        itemsTotal = 0;
        itemsFailed = 0;
        remainingTime = null;
        
        // Match the basic progress pattern (always present)
        var basicMatch = Regex.Match(progressLine, @"(\d+)/(\d+)");
        if (!basicMatch.Success || basicMatch.Groups.Count < 3)
            return false;
            
        // Parse the basic progress values
        if (int.TryParse(basicMatch.Groups[1].Value, out int processed))
            itemsProcessed = processed;
            
        if (int.TryParse(basicMatch.Groups[2].Value, out int total))
            itemsTotal = total;
        
        // Check for failed items: "X failed"
        var failedMatch = Regex.Match(progressLine, @"(\d+)\s+failed");
        if (failedMatch.Success && failedMatch.Groups.Count >= 2)
        {
            if (int.TryParse(failedMatch.Groups[1].Value, out int failed))
                itemsFailed = failed;
        }
        
        // Check for remaining time: "XX:XX remaining" or "XX:XX:XX remaining" or "DD.HH:MM:SS remaining"
        var timeMatch = Regex.Match(progressLine, @"([\d\.:]+)\s+remaining");
        if (timeMatch.Success && timeMatch.Groups.Count >= 2)
        {
            remainingTime = timeMatch.Groups[1].Value;
        }
        
        return true;
    }
    
    /// <summary>
    /// Extracts complete progress information from Warp log output
    /// </summary>
    /// <param name="logLines">Array of log lines to analyze</param>
    /// <param name="itemsProcessed">Output for processed items count</param>
    /// <param name="itemsTotal">Output for total items count</param>
    /// <param name="itemsFailed">Output for failed items count</param>
    /// <param name="remainingTime">Output for estimated remaining time</param>
    /// <returns>True if progress information was found and parsed</returns>
    public static bool ExtractProgressInfo(string[] logLines, out int itemsProcessed, out int itemsTotal, out int itemsFailed, out string remainingTime)
    {
        itemsProcessed = 0;
        itemsTotal = 0;
        itemsFailed = 0;
        remainingTime = null;
        
        int progressLineIndex = FindProgressLine(logLines);
        if (progressLineIndex < 0 || progressLineIndex >= logLines.Length)
            return false;
            
        return TryParseProgress(logLines[progressLineIndex], out itemsProcessed, out itemsTotal, out itemsFailed, out remainingTime);
    }
    
    /// <summary>
    /// Extracts basic progress information from Warp log output (for backward compatibility)
    /// </summary>
    /// <param name="logLines">Array of log lines to analyze</param>
    /// <param name="itemsProcessed">Output for processed items count</param>
    /// <param name="itemsTotal">Output for total items count</param>
    /// <returns>True if progress information was found and parsed</returns>
    public static bool ExtractProgressInfo(string[] logLines, out int itemsProcessed, out int itemsTotal)
    {
        int itemsFailed;
        string remainingTime;
        bool result = ExtractProgressInfo(logLines, out itemsProcessed, out itemsTotal, out itemsFailed, out remainingTime);
        return result;
    }
    
    /// <summary>
    /// Checks if a log file indicates the job has completed successfully
    /// </summary>
    /// <param name="directoryPath"></param>
    /// <returns>True if completion message was found</returns>
    public static bool IsJobCompleted(string directoryPath)
    {
        return File.Exists(Path.Combine(directoryPath, "WARP_JOB_EXIT_SUCCESS"));
    }
    
    /// <summary>
    /// Parses a time remaining string from Warp format into a DateTime representing when the job will finish
    /// </summary>
    /// <param name="remainingTimeString">The time remaining string (e.g., "00:31" or "01:45:30" or "1.02:15:45")</param>
    /// <returns>A DateTime representing the estimated completion time, or null if parsing fails</returns>
    public static TimeSpan? ParseRemainingTimeToCompletion(string remainingTimeString)
    {
        if (string.IsNullOrEmpty(remainingTimeString))
            return null;
            
        TimeSpan timeSpan;
        
        // Parse different time formats
        if (remainingTimeString.Contains('.')) // DD.HH:MM:SS format
        {
            string[] parts = remainingTimeString.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[0], out int days) && 
                TimeSpan.TryParseExact(parts[1], @"hh\:mm\:ss", null, out TimeSpan ts))
            {
                return new TimeSpan(days, ts.Hours, ts.Minutes, ts.Seconds);
            }
        }
        else if (TimeSpan.TryParseExact(remainingTimeString, @"hh\:mm\:ss", null, out TimeSpan hms))
        {
            return hms;
        }
        else if (TimeSpan.TryParseExact(remainingTimeString, @"mm\:ss", null, out TimeSpan ms))
        {
            return ms;
        }
        
        return null;
    }

    public class MiniJsonFsItem
    {
        [JsonPropertyName("Path")]
        public string Path { get; set; } // Movie file name
        
        [JsonPropertyName("ProcessingStatus")]
        public int? Status { get; set; }    // Status value
        
        [JsonPropertyName("Defocus")]
        public double? Defocus { get; set; }  // Defocus value
        
        [JsonPropertyName("Phase")]
        public int? Phase { get; set; }     // Phase
        
        [JsonPropertyName("Resolution")]
        public double? Resolution { get; set; }  // Resolution
        
        [JsonPropertyName("AstigX")]
        public double? AstigmatismX { get; set; }  // Astigmatism in X-axis
        
        [JsonPropertyName("AstigY")]
        public double? AstigmatismY { get; set; }  // Astigmatism in Y-axis
        
        [JsonPropertyName("Motion")]
        public double? Motion { get; set; }  // Motion
        
        [JsonPropertyName("Junk")]
        public double? Junk { get; set; } // Junk value
        
        [JsonPropertyName("Particles")]
        public int? ParticleCount { get; set; }    // Particle count
    }

    public class MiniJsonTsItem
    {
        // Tomostar file name
        [JsonPropertyName("Path")]
        public string Path { get; set; }
        
        // Processing status
        [JsonPropertyName("ProcessingStatus")]
        public int? Status { get; set; }
        
        // Tilt-series movie file paths
        [JsonPropertyName("Tilts")]
        public string[] TiltMoviePaths { get; set; } 
        
        // Angles
        [JsonPropertyName("MinTilt")]
        public double? MinTilt { get; set; }
        
        [JsonPropertyName("MaxTilt")]
        public double? MaxTilt { get; set; }
            
        [JsonPropertyName("MinAxis")]
        public double? MinAxis { get; set; }
        
        [JsonPropertyName("MeanAxis")]
        public double? MeanAxis { get; set; }
        
        [JsonPropertyName("MaxAxis")]
        public double? MaxAxis { get; set; }
            
        // Shifts
        [JsonPropertyName("MinShiftX")]
        public double? MinShiftX { get; set; }
        
        [JsonPropertyName("MeanShiftX")]
        public double? MeanShiftX { get; set; }
        
        [JsonPropertyName("MaxShiftX")]
        public double? MaxShiftX { get; set; }
        
        [JsonPropertyName("MinShiftY")]
        public double? MinShiftY { get; set; }
        
        [JsonPropertyName("MeanShiftY")]
        public double? MeanShiftY { get; set; }
        
        [JsonPropertyName("MaxShiftY")]
        public double? MaxShiftY { get; set; }
        
        // CTF
        [JsonPropertyName("MinDefocus")]
        public double? MinDefocus { get; set; }
        
        [JsonPropertyName("MeanDefocus")]
        public double? MeanDefocus { get; set; }
        
        [JsonPropertyName("MaxDefocus")]
        public double? MaxDefocus { get; set; }
        
        [JsonPropertyName("Astigmatism")]
        public double? Astigmatism { get; set; }
        
        [JsonPropertyName("MinPhase")]
        public double? MinPhase { get; set; }
        
        [JsonPropertyName("MeanPhase")]
        public double? MeanPhase { get; set; }
        
        [JsonPropertyName("MaxPhase")]
        public double? MaxPhase { get; set; }
        
        [JsonPropertyName("CtfResolution")]
        public double? CtfResolution { get; set; }
        
        [JsonPropertyName("CtfInclination")]
        public double? CtfInclination { get; set; }
        
        // Particles
        [JsonPropertyName("Particles")]
        public int? ParticleCount { get; set; }    // Particle count
    }
}