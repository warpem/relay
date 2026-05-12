using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Refund.Services;

namespace Refund.Components;

/// <summary>
/// Component that displays an image from the server using a secure URL.
/// </summary>
public partial class ServerImage : ComponentBase
{
    /// <summary>
    /// Service used to generate secure URLs for file access
    /// </summary>
    [Inject]
    protected FileService FileService { get; set; }
    
    /// <summary>
    /// The server-side path to the image
    /// </summary>
    [Parameter]
    public string ServerPath { get; set; }

    /// <summary>
    /// Additional HTML attributes to apply to the img element
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object> AdditionalAttributes { get; set; }
}