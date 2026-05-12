using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

/// <summary>
/// Icon representing a job that has failed execution.
/// 
/// Used in the GetIcon method of VisualProvider.cs to visually indicate that a job 
/// has encountered an error and did not complete successfully.
/// Styled with a red warning/error color (#DC2323).
/// </summary>
public class FailedIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2513)'><path d='M8 0C3.584 0 0 3.584 0 8C0 12.416 3.584 16 8 16C12.416 16 16 12.416 16 8C16 3.584 12.416 0 8 0ZM8.8 12H7.2V10.4H8.8V12ZM8.8 8.8H7.2V4H8.8V8.8Z' fill='#DC2323'/></g><defs><clipPath id='clip0_874_2513'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public FailedIcon() : base("FailedIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#DC2323");
    }
}

/// <summary>
/// Icon representing a job that has been aborted by the user or the system.
/// 
/// Used in the GetIcon method of VisualProvider.cs to visually indicate that a job
/// has been manually canceled or aborted by an external action.
/// Styled with a red warning/error color (#DC2323), similar to FailedIcon but with
/// an "X" symbol to represent cancellation.
/// </summary>
public class AbortedIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2507)'><path d='M8 0C3.576 0 0 3.576 0 8C0 12.424 3.576 16 8 16C12.424 16 16 12.424 16 8C16 3.576 12.424 0 8 0ZM12 10.872L10.872 12L8 9.128L5.128 12L4 10.872L6.872 8L4 5.128L5.128 4L8 6.872L10.872 4L12 5.128L9.128 8L12 10.872Z' fill='#DC2323'/></g><defs><clipPath id='clip0_874_2507'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public AbortedIcon() : base("AbortedIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#DC2323");
    }
}

/// <summary>
/// Icon representing a job that is being resumed after a temporary pause.
/// 
/// This icon is referenced in comments within VisualProvider.cs but is not yet actively used
/// in the current job status mapping (marked with TODO for post-MVP implementation).
/// The icon features a play button design with an amber/orange color (#D08F2E) to indicate
/// a transitional state.
/// </summary>
public class ResumingIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2662)'><path d='M8 0C6.41775 0 4.87103 0.469192 3.55544 1.34824C2.23985 2.22729 1.21447 3.47667 0.608895 4.93853C0.00334462 6.40034 -0.154948 8.00887 0.153721 9.56072C0.46239 11.1126 1.22433 12.538 2.34315 13.6569C3.46197 14.7757 4.88741 15.5376 6.43926 15.8463C7.99112 16.1549 9.59965 15.9966 11.0615 15.3911C12.5233 14.7855 13.7727 13.7601 14.6518 12.4446C15.5308 11.129 16 9.58225 16 8C16 6.94942 15.7931 5.90914 15.3911 4.93853C14.989 3.96791 14.3985 3.08601 13.6569 2.34315C12.9153 1.60029 12.0324 1.00997 11.0615 0.608895C10.0909 0.20782 9.05058 0 8 0ZM6.4 11.6V4.4L11.2 8L6.4 11.6Z' fill='#D08F2E'/></g><defs><clipPath id='clip0_874_2662'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public ResumingIcon() : base("ResumingIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#D08F2E");
    }
}

/// <summary>
/// Icon representing a job that has successfully completed execution.
/// 
/// Used in the GetIcon method of VisualProvider.cs to visually indicate that a job
/// has finished successfully with no errors. Features a green checkmark design (#5AA56A) 
/// to clearly indicate success, contrasting with the red error/warning icons.
/// This is one of the most commonly seen icons in a typical workflow where jobs
/// complete successfully.
/// </summary>
public class FinishedIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2510)'><path d='M8 0C3.584 0 0 3.584 0 8C0 12.416 3.584 16 8 16C12.416 16 16 12.416 16 8C16 3.584 12.416 0 8 0ZM6.4 12L2.4 8L3.528 6.872L6.4 9.736L12.472 3.664L13.6 4.8L6.4 12Z' fill='#5AA56A'/></g><defs><clipPath id='clip0_874_2510'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public FinishedIcon() : base("FinishedIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#5AA56A");
    }
}

/// <summary>
/// Icon representing a job that is currently executing.
/// 
/// Used in the GetIcon method of VisualProvider.cs to visually indicate that a job
/// is actively running. Features a person running design with an amber/orange color (#D08F2E)
/// to indicate active processing. This icon conveys to users that computation is in progress
/// and the job is neither waiting nor completed.
/// </summary>
public class RunningIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2497)'><path d='M8 0C3.584 0 0 3.584 0 8C0 12.416 3.584 16 8 16C12.416 16 16 12.416 16 8C16 3.584 12.416 0 8 0ZM9.2 3.2C9.64 3.2 10 3.56 10 4C10 4.44 9.64 4.8 9.2 4.8C8.76 4.8 8.4 4.44 8.4 4C8.4 3.56 8.76 3.2 9.2 3.2ZM11.2 8C10.64 8 9.592 7.568 8.872 6.592L8.544 8.472L9.6 9.624V12.8H8.8V9.936L7.912 8.968L7.496 11.08L4.48 10.464L4.64 9.68L6.864 10.136L7.632 6.224L6.4 6.68V8H5.6V6.12L8.224 5.152C8.616 5.008 9.048 5.2 9.232 5.576C9.896 6.936 10.872 7.2 11.2 7.2V8Z' fill='#D08F2E'/></g><defs><clipPath id='clip0_874_2497'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public RunningIcon() : base("RunningIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#D08F2E");
    }
}

/// <summary>
/// Icon representing a job that is preparing input files and resources before execution.
/// 
/// Used in the GetIcon method of VisualProvider.cs to indicate that a job is in the 
/// preparation/staging phase before actual computation begins. Features a droplet/pipette
/// design with blue color (#2E5BD0) to represent the staging of materials.
/// 
/// The staging phase typically involves copying input files, setting up working directories,
/// and preparing the environment for job execution.
/// </summary>
public class StagingIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><circle cx='8' cy='8' r='8' fill='#2E5BD0'/><path d='M7.705 3.43491C7.79032 3.37159 7.89675 3.3374 8 3.3374C8.10325 3.3374 8.20968 3.37159 8.295 3.43491C8.905 3.87491 10.25 5.22991 10.25 8.49991C10.25 9.57991 9.86 10.8799 9.57 11.6749L8.705 11.9999H6.9C6.69 11.9999 6.5 11.8699 6.43 11.6749C6.14 10.8799 5.75 9.57991 5.75 8.49991C5.75 5.22991 7.095 3.87491 7.705 3.43491ZM9 7.49991C9 6.94991 8.55 6.49991 8 6.49991C7.45 6.49991 7 6.94991 7 7.49991C7 8.04991 7.45 8.49991 8 8.49991C8.55 8.49991 9 8.04991 9 7.49991Z' fill='white'/></svg>";

    public StagingIcon() : base("StagingIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E5BD0");
    }
}

/// <summary>
/// Icon representing a job that is waiting to be executed.
/// 
/// Used in the GetIcon method of VisualProvider.cs to indicate that a job has been 
/// created and queued but is not yet running. Features a clock design with blue color (#2E5BD0)
/// to represent waiting time. This icon appears when a job is in a queue, typically waiting
/// for resources or for prerequisite jobs to complete.
/// </summary>
public class WaitingIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2502)'><path d='M7.992 0C3.576 0 0 3.584 0 8C0 12.416 3.576 16 7.992 16C12.408 16 16 12.416 16 8C16 3.584 12.408 0 7.992 0ZM10.632 11.768L7.2 8.328V4H8.8V7.672L11.768 10.64L10.632 11.768Z' fill='#2E5BD0'/></g><defs><clipPath id='clip0_874_2502'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public WaitingIcon() : base("WaitingIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E5BD0");
    }
}

/// <summary>
/// Icon representing a job that is clearing temporary files and resources.
/// 
/// Used in the GetIcon method of VisualProvider.cs to indicate that a job is in the 
/// cleanup phase, removing temporary files and freeing up resources. This icon currently
/// shares the same visual design as the WaitingIcon (clock face with blue color) but 
/// has a distinct semantic meaning in the job lifecycle.
/// </summary>
public class ClearingIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2502)'><path d='M7.992 0C3.576 0 0 3.584 0 8C0 12.416 3.576 16 7.992 16C12.408 16 16 12.416 16 8C16 3.584 12.408 0 7.992 0ZM10.632 11.768L7.2 8.328V4H8.8V7.672L11.768 10.64L10.632 11.768Z' fill='#2E5BD0'/></g><defs><clipPath id='clip0_874_2502'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public ClearingIcon() : base("ClearingIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E5BD0");
    }
}

/// <summary>
/// Icon representing a job that is building or setting up its execution environment.
/// 
/// Used in the GetIcon method of VisualProvider.cs to indicate that a job is in the 
/// building/setup phase, typically configuring computation environments or compiling
/// necessary tools. Features a pencil/editing design with blue color (#2E5BD0) to
/// represent the construction of the job environment.
/// 
/// The building phase often involves compiling code, setting up containers, or
/// preparing specialized computation environments before the actual processing begins.
/// </summary>
public class BuildingIcon : Icon
{
    private const string SvgContent
        = "<svg width='16' height='16' viewBox='0 0 16 16' fill='none' xmlns='http://www.w3.org/2000/svg'><g clip-path='url(#clip0_874_2520)'><path d='M8 0C3.576 0 0 3.576 0 8C0 12.424 3.576 16 8 16C12.424 16 16 12.424 16 8C16 3.576 12.424 0 8 0ZM10.48 4.056C10.592 4.056 10.704 4.096 10.8 4.184L11.816 5.2C12 5.376 12 5.656 11.816 5.824L11.016 6.624L9.376 4.984L10.176 4.184C10.256 4.096 10.368 4.056 10.48 4.056ZM8.904 5.448L10.552 7.096L5.704 11.944H4.056V10.296L8.904 5.448Z' fill='#2E5BD0'/></g><defs><clipPath id='clip0_874_2520'><rect width='16' height='16' fill='white'/></clipPath></defs></svg>";

    public BuildingIcon() : base("BuildingIcon", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E5BD0");
    }
}

/// <summary>
/// A simplified version of the BuildingIcon with a different visual style.
/// 
/// Provides an alternative visual representation of the building status, with
/// a different design but maintaining the same semantic meaning. Uses a muted
/// blue-gray color (#4D647F) instead of the bright blue of the standard BuildingIcon.
/// 
/// This variant may be used in UI contexts where a subtler or simpler representation
/// is desired compared to the standard BuildingIcon.
/// </summary>
public class BuildingIconSimple : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='21' viewBox='0 0 20 21' fill='none'><path d='M5 13.44L11.06 7.38L13.12 9.44L7.06 15.5H5V13.44ZM10 18.5C12.1217 18.5 14.1566 17.6571 15.6569 16.1569C17.1571 14.6566 18 12.6217 18 10.5C18 8.37827 17.1571 6.34344 15.6569 4.84315C14.1566 3.34285 12.1217 2.5 10 2.5C7.87827 2.5 5.84344 3.34285 4.34315 4.84315C2.84285 6.34344 2 8.37827 2 10.5C2 12.6217 2.84285 14.6566 4.34315 16.1569C5.84344 17.6571 7.87827 18.5 10 18.5ZM14.7 7.85L13.7 8.85L11.65 6.8L12.65 5.8C12.86 5.58 13.21 5.58 13.42 5.8L14.7 7.08C14.92 7.29 14.92 7.64 14.7 7.85ZM10 0.5C11.3132 0.5 12.6136 0.758658 13.8268 1.2612C15.0401 1.76375 16.1425 2.50035 17.0711 3.42893C17.9997 4.35752 18.7362 5.45991 19.2388 6.67317C19.7413 7.88642 20 9.18678 20 10.5C20 13.1522 18.9464 15.6957 17.0711 17.5711C15.1957 19.4464 12.6522 20.5 10 20.5C8.68678 20.5 7.38642 20.2413 6.17317 19.7388C4.95991 19.2362 3.85752 18.4997 2.92893 17.5711C1.05357 15.6957 0 13.1522 0 10.5C0 7.84784 1.05357 5.3043 2.92893 3.42893C4.8043 1.55357 7.34784 0.5 10 0.5Z' fill='#4D647F'/></svg>";

    public BuildingIconSimple() : base("BuildingIconSimple", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#4D647F");
    }
}

/// <summary>
/// A simplified version of the FailedIcon with a different visual style.
/// 
/// Provides an alternative representation of the failed status, with a hollow
/// circle design compared to the solid background of the standard FailedIcon.
/// Maintains the same red warning color (#DC2323) to clearly indicate an error state.
/// 
/// May be used in UI contexts where a more subtle or less prominent representation
/// of failure is appropriate, while still maintaining the semantic meaning.
/// </summary>
public class FailedIconSimple : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20' viewBox='0 0 20 20' fill='none'><path d='M9 13H11V15H9V13ZM9 5H11V11H9V5ZM9.99 0C4.47 0 0 4.48 0 10C0 15.52 4.47 20 9.99 20C15.52 20 20 15.52 20 10C20 4.48 15.52 0 9.99 0ZM10 18C5.58 18 2 14.42 2 10C2 5.58 5.58 2 10 2C14.42 2 18 5.58 18 10C18 14.42 14.42 18 10 18Z' fill='#DC2323'/></svg>";

    public FailedIconSimple() : base("FailedIconSimple", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#DC2323");
    }
}

/// <summary>
/// Icon representing a user profile in light mode UI.
/// 
/// Used in the application header or user-related UI elements to represent 
/// the current user or account functions. Features a circular user profile
/// icon with a white fill, suitable for display against darker backgrounds.
/// </summary>
public class RelayPerson : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40' viewBox='0 0 40 40' fill='none'><path d='M20.0805 33.6C12.5605 33.6 6.48047 27.52 6.48047 20C6.48047 12.48 12.5605 6.40002 20.0805 6.40002C27.6005 6.40002 33.6805 12.48 33.6805 20C33.6805 27.52 27.5205 33.6 20.0805 33.6ZM20.0805 8.00002C13.4405 8.00002 8.08047 13.36 8.08047 20C8.08047 26.64 13.4405 32 20.0805 32C26.7205 32 32.0805 26.64 32.0805 20C32.0805 13.36 26.6405 8.00002 20.0805 8.00002Z' fill='white'/><path d='M12.2408 29.84L10.8008 29.2C11.2008 28.24 12.4808 27.68 13.8408 27.04C15.2008 26.4 16.8808 25.68 16.8808 24.8V23.6C16.4008 23.2 15.6008 22.32 15.4408 21.04C15.0408 20.64 14.4008 19.92 14.4008 18.96C14.4008 18.4 14.6408 17.92 14.8008 17.6C14.6408 16.96 14.4808 15.76 14.4808 14.8C14.4808 11.68 16.6408 9.59998 20.0808 9.59998C21.0408 9.59998 22.2408 9.83998 22.8808 10.56C24.4008 10.88 25.6808 12.64 25.6808 14.8C25.6808 16.16 25.4408 17.28 25.2808 17.84C25.4408 18.08 25.6008 18.48 25.6008 18.96C25.6008 20 25.0408 20.72 24.5608 21.04C24.4008 22.32 23.6808 23.12 23.2008 23.52V24.8C23.2008 25.52 24.6408 26.08 25.9208 26.56C27.4408 27.12 29.0408 27.76 29.6008 29.04L28.0808 29.6C27.8408 28.96 26.5608 28.48 25.3608 28.08C23.6008 27.44 21.6008 26.72 21.6008 24.88V22.8L22.0008 22.56C22.0008 22.56 22.9608 21.92 22.9608 20.64V20.08L23.4408 19.84C23.5208 19.84 23.9208 19.6 23.9208 18.96C23.9208 18.8 23.7608 18.56 23.6808 18.48L23.3608 18.16L23.5208 17.76C23.5208 17.76 23.9208 16.48 23.9208 14.88C23.9208 13.36 23.0408 12.24 22.3208 12.24H21.8408L21.6008 11.84C21.6008 11.52 21.0408 11.2 20.0808 11.2C17.6008 11.2 16.0808 12.56 16.0808 14.8C16.0808 15.84 16.4808 17.6 16.4808 17.6L16.5608 18L16.2408 18.4C16.1608 18.4 16.0008 18.64 16.0008 18.96C16.0008 19.36 16.4808 19.84 16.7208 20L17.0408 20.24V20.64C17.0408 21.84 18.0808 22.48 18.0808 22.56L18.4808 22.8V24.88C18.4808 26.8 16.4008 27.76 14.4808 28.56C13.6008 28.88 12.4008 29.44 12.2408 29.84Z' fill='white'/></svg>";

    public RelayPerson() : base("RelayPerson", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("white");
    }
}

/// <summary>
/// Icon representing a user profile in dark mode UI.
/// 
/// Dark mode variant of the RelayPerson icon, featuring a blue color (#1C487D)
/// fill suitable for display against lighter backgrounds. Used in the same
/// contexts as RelayPerson but adapts to the application's theming system.
/// </summary>
public class RelayPersonDark : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='30' height='30' viewBox='0 0 40 30' fill='none'><path d='M20.0805 33.6C12.5605 33.6 6.48047 27.52 6.48047 20C6.48047 12.48 12.5605 6.40002 20.0805 6.40002C27.6005 6.40002 33.6805 12.48 33.6805 20C33.6805 27.52 27.5205 33.6 20.0805 33.6ZM20.0805 8.00002C13.4405 8.00002 8.08047 13.36 8.08047 20C8.08047 26.64 13.4405 32 20.0805 32C26.7205 32 32.0805 26.64 32.0805 20C32.0805 13.36 26.6405 8.00002 20.0805 8.00002Z' fill='#1C487D'/><path d='M12.2408 29.84L10.8008 29.2C11.2008 28.24 12.4808 27.68 13.8408 27.04C15.2008 26.4 16.8808 25.68 16.8808 24.8V23.6C16.4008 23.2 15.6008 22.32 15.4408 21.04C15.0408 20.64 14.4008 19.92 14.4008 18.96C14.4008 18.4 14.6408 17.92 14.8008 17.6C14.6408 16.96 14.4808 15.76 14.4808 14.8C14.4808 11.68 16.6408 9.59998 20.0808 9.59998C21.0408 9.59998 22.2408 9.83998 22.8808 10.56C24.4008 10.88 25.6808 12.64 25.6808 14.8C25.6808 16.16 25.4408 17.28 25.2808 17.84C25.4408 18.08 25.6008 18.48 25.6008 18.96C25.6008 20 25.0408 20.72 24.5608 21.04C24.4008 22.32 23.6808 23.12 23.2008 23.52V24.8C23.2008 25.52 24.6408 26.08 25.9208 26.56C27.4408 27.12 29.0408 27.76 29.6008 29.04L28.0808 29.6C27.8408 28.96 26.5608 28.48 25.3608 28.08C23.6008 27.44 21.6008 26.72 21.6008 24.88V22.8L22.0008 22.56C22.0008 22.56 22.9608 21.92 22.9608 20.64V20.08L23.4408 19.84C23.5208 19.84 23.9208 19.6 23.9208 18.96C23.9208 18.8 23.7608 18.56 23.6808 18.48L23.3608 18.16L23.5208 17.76C23.5208 17.76 23.9208 16.48 23.9208 14.88C23.9208 13.36 23.0408 12.24 22.3208 12.24H21.8408L21.6008 11.84C21.6008 11.52 21.0408 11.2 20.0808 11.2C17.6008 11.2 16.0808 12.56 16.0808 14.8C16.0808 15.84 16.4808 17.6 16.4808 17.6L16.5608 18L16.2408 18.4C16.1608 18.4 16.0008 18.64 16.0008 18.96C16.0008 19.36 16.4808 19.84 16.7208 20L17.0408 20.24V20.64C17.0408 21.84 18.0808 22.48 18.0808 22.56L18.4808 22.8V24.88C18.4808 26.8 16.4008 27.76 14.4808 28.56C13.6008 28.88 12.4008 29.44 12.2408 29.84Z' fill='#1C487D'/></svg>";

    public RelayPersonDark() : base("RelayPerson", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#1C487D");
    }
}

/// <summary>
/// Icon representing a logout action.
/// 
/// Used in the application header or user menu to indicate the logout function.
/// Features a power icon design with white fill, suitable for display against
/// darker backgrounds in the application's header area.
/// </summary>
public class RelayLogout : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='27' height='28' viewBox='0 0 27 28' fill='none'><path d='M16.4531 5.12299V6.88801C20.1274 8.12442 22.7812 11.6017 22.7812 15.6875C22.7812 20.8052 18.6177 24.9687 13.5 24.9687C8.38229 24.9687 4.21875 20.8052 4.21875 15.6875C4.21875 11.6017 6.87255 8.12442 10.5469 6.88801V5.12299C5.9285 6.41566 2.53125 10.6619 2.53125 15.6875C2.53125 21.7356 7.45184 26.6562 13.5 26.6562C19.5482 26.6562 24.4688 21.7356 24.4688 15.6875C24.4688 10.6619 21.0715 6.41566 16.4531 5.12299Z' fill='white'/><path d='M12.6562 1.34375H14.3438V15.6875H12.6562V1.34375Z' fill='white'/></svg>";

    public RelayLogout() : base("RelayLogout", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("white");
    }
}

/// <summary>
/// Icon representing a downward-pointing arrow.
/// 
/// Used in dropdown menus and expandable UI elements to indicate that clicking
/// will expand content downward. Features a blue color (#2E77D0) that matches
/// the application's accent color scheme.
/// </summary>
public class RelayArrowDown : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8' fill='none'><path d='M6 7.70005L0 1.70005L1.4 0.300049L6 4.90005L10.6 0.300049L12 1.70005L6 7.70005Z' fill='#2E77D0'/></svg>";

    public RelayArrowDown() : base("RelayArrowDown", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E77D0");
    }
}

/// <summary>
/// Icon representing an upward-pointing arrow.
/// 
/// Used in dropdown menus and collapsible UI elements to indicate that clicking
/// will collapse currently expanded content. Features a blue color (#2E77D0) that
/// matches the application's accent color scheme and coordinates with RelayArrowDown.
/// </summary>
public class RelayArrowUp : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8' fill='none'><path d='M6 0.299952L12 6.29995L10.6 7.69995L6 3.09995L1.4 7.69995L0 6.29995L6 0.299952Z' fill='#2E77D0'/></svg>";

    public RelayArrowUp() : base("RelayArrowUp", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E77D0");
    }
}

/// <summary>
/// Icon representing an information indicator.
/// 
/// Used throughout the UI to indicate the presence of additional information or help text.
/// Features a blue outline information circle with the letter "i" (#2E77D0) that matches
/// the application's accent color scheme.
/// 
/// This icon is typically used in tooltips, help sections, or next to fields that
/// benefit from additional explanation.
/// </summary>
public class RelayInfoOutlined : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='18' height='18' viewBox='0 0 18 18' fill='none'><path fill-rule='evenodd' clip-rule='evenodd' d='M0.578003 8.57812C0.578003 4.15985 4.15972 0.578125 8.578 0.578125C12.9963 0.578125 16.578 4.15985 16.578 8.57812C16.578 12.9964 12.9963 16.5781 8.578 16.5781C4.15972 16.5781 0.578003 12.9964 0.578003 8.57812ZM14.978 8.57812C14.978 5.0435 12.1126 2.17813 8.57798 2.17813C5.045 2.18209 2.18195 5.04515 2.17798 8.57812C2.17798 12.1127 5.04336 14.9781 8.57798 14.9781C12.1126 14.9781 14.978 12.1127 14.978 8.57812ZM7.77795 4.57812H9.37795V6.17812H7.77795V4.57812ZM9.37803 7.77812V12.5781H7.77803V9.37813H6.97803V7.77812H9.37803Z' fill='#2E77D0'/></svg>";

    public RelayInfoOutlined() : base("RelayInfoOutlined", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E77D0");
    }
}

/// <summary>
/// Icon representing a right-pointing arrow in tooltip contexts.
/// 
/// Used in tooltips and navigation UI to indicate forward direction or the
/// presence of additional content. Features a blue chevron design (#2E77D0)
/// that matches the application's accent color scheme.
/// </summary>
public class RelayTooltipArrow : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 16 16' fill='none'><path fill-rule='evenodd' clip-rule='evenodd' d='M5.72656 11.06L6.66656 12L10.6666 8L6.66656 4L5.72656 4.94L8.7799 8L5.72656 11.06Z' fill='#2E77D0'/></svg>";

    public RelayTooltipArrow() : base("RelayTooltipArrow", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#2E77D0");
    }
}

/// <summary>
/// Icon representing a stylized tree/structure graphic.
/// 
/// Used as decorative element in certain UI backgrounds or empty states.
/// Features a light gray fill (#E1E1E1) that provides subtle visual interest
/// without drawing focus away from primary content.
/// 
/// This icon is typically used in wider layout contexts as a visual accent
/// rather than an interactive element.
/// </summary>
public class RelayTree : Icon
{
    private const string SvgContent
        = "<svg xmlns='http://www.w3.org/2000/svg' width='181' height='223' viewBox='0 0 181 223' fill='none'><path d='M112.851 157.925H124.06C136.923 157.91 149.389 153.635 159.367 145.818C169.344 138.001 176.224 127.117 178.853 114.993C181.482 102.87 179.699 90.2444 173.803 79.2367C167.908 68.229 158.259 59.5089 146.476 54.5404C146.476 40.2297 140.572 26.5052 130.062 16.3861C119.552 6.26691 105.298 0.582031 90.4348 0.582031C75.5716 0.582031 61.3172 6.26691 50.8073 16.3861C40.2975 26.5052 34.3931 40.2297 34.3931 54.5404C22.6104 59.5089 12.9616 68.229 7.06629 79.2367C1.17097 90.2444 -0.611948 102.87 2.0168 114.993C4.64555 127.117 11.5259 138.001 21.5031 145.818C31.4802 153.635 43.9466 157.91 56.8098 157.925H68.0181V201.091H11.9765V222.675H168.893V201.091H112.851V157.925Z' fill='#E1E1E1'/></svg>";

    public RelayTree() : base("RelayTree", IconVariant.Regular, IconSize.Custom, SvgContent)
    {
        WithColor("#E1E1E1");
    }
}