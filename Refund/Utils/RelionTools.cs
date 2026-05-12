using System;
using System.Text.RegularExpressions;

namespace Refund.Utils
{
    /// <summary>
    /// Utility methods for parsing and processing RELION output logs and results
    /// </summary>
    public static class RelionTools
    {
        /// <summary>
        /// Extracts the current iteration and total iterations from RELION log
        /// </summary>
        /// <param name="logLines">Array of log lines to analyze</param>
        /// <param name="iteration">Output for current iteration</param>
        /// <param name="totalIterations">Output for total iterations</param>
        /// <returns>True if iteration information was found and parsed</returns>
        public static bool ExtractIterationInfo(string[] logLines, out int iteration, out int totalIterations)
        {
            iteration = 0;
            totalIterations = 0;
            
            foreach (string line in logLines)
            {
                var match = Regex.Match(line, @"Gradient optimisation iteration (\d+) of (\d+)");
                if (match.Success && match.Groups.Count >= 3)
                {
                    if (int.TryParse(match.Groups[1].Value, out int iter))
                        iteration = iter;
                        
                    if (int.TryParse(match.Groups[2].Value, out int total))
                        totalIterations = total;
                        
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Extracts the estimated accuracy (angles) from refinement job output
        /// </summary>
        /// <param name="logLines">Array of log lines to analyze</param>
        /// <param name="angularAccuracy">Output for the angular accuracy value</param>
        /// <returns>True if accuracy was found and parsed</returns>
        public static bool ExtractAngularAccuracy(string[] logLines, out float angularAccuracy)
        {
            angularAccuracy = 0;
            
            foreach (string line in logLines)
            {
                if (line.Contains("Estimated accuracy angles="))
                {
                    var match = Regex.Match(line, @"Estimated accuracy angles=\s*(\d+\.?\d*)");
                    if (match.Success && match.Groups.Count >= 2)
                    {
                        if (float.TryParse(match.Groups[1].Value, out float value))
                        {
                            angularAccuracy = value;
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
    }
}