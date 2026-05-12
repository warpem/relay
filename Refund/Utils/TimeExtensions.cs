namespace Refund.Utils;

public static class TimeExtensions
{
    public static string ToStringAdaptive(this TimeSpan span)
    {
        return span.ToString((int)span.TotalDays > 0 ?
                                 @"dd\.hh\:mm" :
                                 ((int)span.TotalHours > 0 ?
                                      @"hh\:mm\:ss" :
                                      @"mm\:ss"));
    }
}