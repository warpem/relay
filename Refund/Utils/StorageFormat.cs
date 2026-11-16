using System.Globalization;

namespace Refund.Utils;

/// <summary>Sizes and counts for storage displays. Binary multiples, like df -h and du -h.</summary>
public static class StorageFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>"0 B", "812 KB", "12.3 GB", "499 TB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes)} B");
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        string number = value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        return $"{number} {Units[unit]}";
    }

    /// <summary>"8,431", "12.3 k", "1.2 M", "157 M".</summary>
    public static string Count(long count)
    {
        if (count < 10_000) return count.ToString("N0", CultureInfo.InvariantCulture);
        if (count < 1_000_000) return Scaled(count / 1e3, "k");
        if (count < 1_000_000_000) return Scaled(count / 1e6, "M");
        return Scaled(count / 1e9, "B");
    }

    private static string Scaled(double value, string suffix) =>
        $"{value.ToString(value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture)} {suffix}";

    /// <summary>"3 days", "5 months", "2 years"; "just now" under a minute.</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return Plural((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Plural((int)age.TotalHours, "hour");
        if (age < TimeSpan.FromDays(61)) return Plural((int)age.TotalDays, "day");
        if (age < TimeSpan.FromDays(730)) return Plural((int)(age.TotalDays / 30.44), "month");
        return Plural((int)(age.TotalDays / 365.25), "year");
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
