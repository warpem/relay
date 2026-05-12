using Microsoft.AspNetCore.Components;

namespace Relay.Panels.Right;

/// <summary>
/// A component that displays application-level information in the right panel when no specific object is selected.
/// </summary>
/// <remarks>
/// This component is shown in the right panel of the application when the user is at the home screen
/// or has not selected any specific project, space, view, or job. It provides general information
/// about the application and quick links to common actions like creating a new project.
/// 
/// The component consists primarily of static content, with no significant state or behavior.
/// Its main purpose is to provide contextual help and guidance to users who haven't yet
/// selected a specific object to work with.
/// </remarks>
public partial class HomeProperties : ComponentBase { }