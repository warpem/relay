using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Refund.Components.ThumbnailPanel;

/// <summary>
/// A panel for displaying and interacting with a collection of thumbnail images.
/// Supports scrolling, selection, and status visualization with a status bar.
/// </summary>
public partial class ThumbnailPanel : ComponentBase, IAsyncDisposable
{
    #region Parameters
    
    /// <summary>
    /// The collection of thumbnail data to display in the panel
    /// </summary>
    [Parameter]
    public List<ThumbnailData> Thumbnails { get; set; }

    /// <summary>
    /// The currently selected thumbnail
    /// </summary>
    [Parameter]
    public ThumbnailData SelectedThumbnail { get; set; }

    /// <summary>
    /// Callback that triggers when the selected thumbnail changes
    /// </summary>
    [Parameter]
    public EventCallback<ThumbnailData> SelectedThumbnailChanged { get; set; }

    /// <summary>
    /// Whether to show the status bar above the thumbnails
    /// </summary>
    [Parameter]
    public bool ShowStatusBar { get; set; } = true;
    
    /// <summary>
    /// The size (width and height) of each thumbnail in pixels
    /// </summary>
    [Parameter]
    public int ThumbnailSize { get; set; } = 140;

    /// <summary>
    /// The check mode for thumbnails (None, Binary, or Ternary)
    /// </summary>
    [Parameter]
    public CheckMode CheckMode { get; set; } = CheckMode.None;

    /// <summary>
    /// Callback that triggers when a thumbnail's check state changes
    /// </summary>
    [Parameter]
    public EventCallback<ThumbnailData> ThumbnailCheckChanged { get; set; }

    #endregion

    #region Private

    /// <summary>
    /// JavaScript runtime for interacting with browser APIs
    /// </summary>
    [Inject]
    protected IJSRuntime JSRuntime { get; set; }

    private ElementReference thumbnailContainer;
    private List<ThumbnailItem> VisibleItems = new();
    private int ThumbnailWidth => ThumbnailSize + 4;

    private int TotalWidth => Thumbnails?.Count * ThumbnailWidth ?? 0;
    private string TotalWidthPx => $"{TotalWidth}px";

    private DotNetObjectReference<ThumbnailPanel> _dotNetHelper;
    private IJSObjectReference _module;
    private bool _moduleLoaded = false;
    private bool _firstRenderCompleted = false;
    
    private ElementReference thumbnailPanelElement;
    
    // New properties for status bars
    private double ComponentWidth { get; set; }
    private double SegmentWidth { get; set; }
    private int SegmentsPerBar { get; set; }
    private int NumberOfBars { get; set; }
    private double StatusBarsHeight { get; set; }
    private List<StatusBar> StatusBars = new();
    private List<HighlightSegment> HighlightSegments = new();
    private const double MinSegmentWidth = 3.0;
    
    // Debounce fields
    private CancellationTokenSource _scrollCts;
    
    #endregion
    
    #region Overrides

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Load the JavaScript module
            _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/ThumbnailPanel/ThumbnailPanel.razor.js");
            _moduleLoaded = true;
            _firstRenderCompleted = true;

            // Create a DotNetObjectReference
            _dotNetHelper = DotNetObjectReference.Create(this);

            // Get the initial component width
            ComponentWidth = await _module.InvokeAsync<double>("getElementWidth", thumbnailPanelElement);

            // Observe component resize
            await _module.InvokeVoidAsync("observeResize", thumbnailPanelElement, _dotNetHelper);

            // Calculate the status bars
            if (ShowStatusBar)
                CalculateStatusBars();

            // Update visible items
            await UpdateVisibleItems();

            // Trigger a re-render
            StateHasChanged();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Thumbnails != null)
        {
            if (SelectedThumbnail != null && !Thumbnails.Contains(SelectedThumbnail))
            {
                SelectedThumbnail = null;
                await SelectedThumbnailChanged.InvokeAsync(null);
            }

            if (_firstRenderCompleted && _moduleLoaded)
            {
                if (ShowStatusBar)
                    CalculateStatusBars();
                await UpdateVisibleItems();
                StateHasChanged();
            }
        }
    }
    
    #endregion

    #region Mouse events
    
    private void OnScroll()
    {
        if (!_moduleLoaded)
            return;

        // Cancel any pending update
        _scrollCts?.Cancel();
        _scrollCts = new CancellationTokenSource();

        // Start a new debounce delay
        _ = DebounceScroll(_scrollCts.Token);
    }

    private async Task DebounceScroll(CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token);

            // Proceed with updating visible items if not cancelled
            if (!token.IsCancellationRequested)
                await UpdateVisibleItems();
        }
        catch (TaskCanceledException) { }
    }

    private async Task ScrollLeft()
    {
        if (!_moduleLoaded)
            return;

        var scrollInfo = await _module.InvokeAsync<ScrollInfo>("getScrollInfo", thumbnailContainer);
        double amount = scrollInfo.ClientWidth;
        await _module.InvokeVoidAsync("scrollLeft", thumbnailContainer, amount);
    }

    private async Task ScrollRight()
    {
        if (!_moduleLoaded)
            return;

        var scrollInfo = await _module.InvokeAsync<ScrollInfo>("getScrollInfo", thumbnailContainer);
        double amount = scrollInfo.ClientWidth;
        await _module.InvokeVoidAsync("scrollRight", thumbnailContainer, amount);
    }

    private async Task OnWheel(WheelEventArgs e)
    {
        if (!_moduleLoaded)
            return;

        await _module.InvokeVoidAsync("scrollBy", thumbnailContainer, e.DeltaY);
    }
    
    private async Task OnStatusBarClicked(MouseEventArgs e, int barIndex)
    {
        // Ensure we have valid data to avoid division by zero
        if (SegmentsPerBar <= 0 || SegmentWidth <= 0 || Thumbnails == null || Thumbnails.Count == 0)
            return;

        // Get the X coordinate relative to the status bar
        double clickX = e.OffsetX;

        // Verify that barIndex is within valid range
        if (barIndex < 0 || barIndex >= NumberOfBars)
            return;

        // Calculate the segment index within the bar
        double segmentIndexInBar = clickX / SegmentWidth;
        int thumbnailIndexInBar = (int)segmentIndexInBar;

        // Compute the overall thumbnail index
        int thumbnailIndex = barIndex * SegmentsPerBar + thumbnailIndexInBar;
        if (thumbnailIndex < 0 || thumbnailIndex >= Thumbnails.Count)
            return;

        // Scroll the thumbnail panel to center around the clicked thumbnail
        await ScrollToThumbnail(thumbnailIndex);
    }
    
    #endregion
    
    #region Status bars

    private void CalculateStatusBars()
    {
        if (Thumbnails == null || Thumbnails.Count == 0 || ComponentWidth <= 0)
        {
            SegmentsPerBar = 1;
            NumberOfBars = 0;
            StatusBars.Clear();
            return;
        }

        int totalThumbnails = Thumbnails.Count;

        // Maximum number of segments per bar based on minimum segment width
        SegmentsPerBar = Math.Min(totalThumbnails, (int)Math.Floor(ComponentWidth / MinSegmentWidth));

        if (SegmentsPerBar <= 0)
            SegmentsPerBar = 1;

        // Calculate SegmentWidth to fill the component width exactly
        SegmentWidth = ComponentWidth / SegmentsPerBar;

        // Recalculate SegmentsPerBar if SegmentWidth is less than MinSegmentWidth
        if (SegmentWidth < MinSegmentWidth)
        {
            SegmentWidth = MinSegmentWidth;
            SegmentsPerBar = (int)Math.Floor(ComponentWidth / SegmentWidth);
        }

        // Calculate the number of bars needed
        NumberOfBars = (int)Math.Ceiling((double)totalThumbnails / SegmentsPerBar);

        StatusBars.Clear();

        int thumbnailIndex = 0;

        for (int barIndex = 0; barIndex < NumberOfBars; barIndex++)
        {
            var statusBar = new StatusBar();

            int segmentsInThisBar = Math.Min(SegmentsPerBar, totalThumbnails - thumbnailIndex);
            var thumbnailsInBar = Thumbnails.Skip(thumbnailIndex).Take(segmentsInThisBar).ToList();

            double currentLeft = 0.0;
            int segmentStartIndex = 0;

            while (segmentStartIndex < thumbnailsInBar.Count)
            {
                var currentStatus = thumbnailsInBar[segmentStartIndex].Status;
                int segmentEndIndex = segmentStartIndex + 1;

                while (segmentEndIndex < thumbnailsInBar.Count && thumbnailsInBar[segmentEndIndex].Status == currentStatus)
                {
                    segmentEndIndex++;
                }

                int segmentLength = segmentEndIndex - segmentStartIndex;
                double segmentPixelWidth = segmentLength * SegmentWidth;

                var segment = new StatusSegment
                {
                    Left = currentLeft,
                    Width = segmentPixelWidth,
                    Color = StatusColorMapping.GetColor(currentStatus ?? ProcessingStatus.Unprocessed)
                };

                statusBar.Segments.Add(segment);

                currentLeft += segmentPixelWidth;
                segmentStartIndex = segmentEndIndex;
            }

            StatusBars.Add(statusBar);
            thumbnailIndex += segmentsInThisBar;
        }

        // Calculate the total height of the status bars
        StatusBarsHeight = NumberOfBars * 8 + (NumberOfBars - 1) * 4; // 8px per bar, 4px gap
    }

    private void UpdateHighlight(int firstVisibleIndex, int visibleItemCount)
    {
        HighlightSegments.Clear();

        // Ensure we have valid SegmentsPerBar to avoid division by zero
        if (SegmentsPerBar <= 0 || Thumbnails == null || Thumbnails.Count == 0)
            return;

        int lastVisibleIndex = Math.Min(firstVisibleIndex + visibleItemCount - 1, Thumbnails.Count - 1);

        for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
        {
            int barIndex = i / SegmentsPerBar;
            int segmentIndexInBar = i % SegmentsPerBar;

            double left = segmentIndexInBar * SegmentWidth;
            double width = SegmentWidth;

            // Merge with previous highlight if in the same bar and adjacent
            if (HighlightSegments.Count > 0)
            {
                var lastSegment = HighlightSegments.Last();
                if (lastSegment.BarIndex == barIndex && Math.Abs(lastSegment.Left + lastSegment.Width - left) < 0.1)
                {
                    lastSegment.Width += width;
                    continue;
                }
            }

            HighlightSegments.Add(new HighlightSegment
            {
                BarIndex = barIndex,
                Left = left,
                Width = width
            });
        }
        
        StateHasChanged();
    }
    
    #endregion
    
    #region Thumbnails

    private async Task UpdateVisibleItems()
    {
        if (!_moduleLoaded)
            return;

        var scrollInfo = await _module.InvokeAsync<ScrollInfo>("getScrollInfo", thumbnailContainer);
        int firstVisibleIndex = (int)(scrollInfo.ScrollLeft / ThumbnailWidth);
        int visibleItemCount = Math.Min((int)((scrollInfo.ClientWidth + ThumbnailWidth - 1) / ThumbnailWidth), Thumbnails.Count);
        int firstVisibleIndexWithMargin = Math.Max(0, firstVisibleIndex - visibleItemCount); 
        
        var newVisibleItems = Thumbnails
            .Skip(firstVisibleIndexWithMargin)
            .Take(visibleItemCount * 3)
            .Select((data, index) => new ThumbnailItem
            {
                Data = data,
                IsSelected = data.Equals(SelectedThumbnail),
                PositionLeft = (firstVisibleIndexWithMargin + index) * ThumbnailWidth
            })
            .ToList();

        VisibleItems = newVisibleItems;
        StateHasChanged();
        
        // Update highlight segments in status bar(s)
        if (ShowStatusBar)
            UpdateHighlight(firstVisibleIndex, visibleItemCount);
    }

    private async Task OnThumbnailSelected(ThumbnailData data)
    {
        SelectedThumbnail = data;
        await SelectedThumbnailChanged.InvokeAsync(data);

        // Update the selection state of visible items
        foreach (var item in VisibleItems)
            item.IsSelected = item.Data.Equals(data);

        StateHasChanged();
    }
    
    /// <summary>
    /// Sets the selected thumbnail programmatically and scrolls to it without triggering the SelectedThumbnailChanged event.
    /// </summary>
    /// <param name="thumbnail">The thumbnail data to select</param>
    /// <returns>Task representing the asynchronous operation</returns>
    public async Task SetSelectedThumbnailAsync(ThumbnailData thumbnail)
    {
        if (thumbnail == null || Thumbnails == null || !Thumbnails.Contains(thumbnail))
            return;
            
        // Set the selected thumbnail without triggering the event
        SelectedThumbnail = thumbnail;
        
        // Find the index of the thumbnail to scroll to
        int thumbnailIndex = Thumbnails.IndexOf(thumbnail);
        if (thumbnailIndex >= 0)
        {
            // Scroll to center the selected thumbnail
            await ScrollToThumbnail(thumbnailIndex);
            
            // Wait for scrolling to finish and then update visible items
            // to ensure the selected thumbnail is rendered correctly
            await Task.Delay(100);
            await UpdateVisibleItems();
        }
    }
    
    private async Task ScrollToThumbnail(int thumbnailIndex)
    {
        if (!_moduleLoaded)
            return;

        // Get the current scroll info
        var scrollInfo = await _module.InvokeAsync<ScrollInfo>("getScrollInfo", thumbnailContainer);

        // Calculate the desired scroll position
        double targetScrollLeft = thumbnailIndex * ThumbnailWidth - (scrollInfo.ClientWidth - ThumbnailWidth) / 2;
        // Ensure the scroll position is within valid range
        targetScrollLeft = Math.Max(0, Math.Min(targetScrollLeft, scrollInfo.ScrollWidth - scrollInfo.ClientWidth));

        // Scroll to the desired position smoothly
        await _module.InvokeVoidAsync("scrollTo", thumbnailContainer, targetScrollLeft);
    }

    private async Task OnThumbnailCheckChanged(ThumbnailData data)
    {
        await ThumbnailCheckChanged.InvokeAsync(data);
    }

    #endregion

    #region Housekeeping

    [JSInvokable]
    public async Task OnComponentResized(double newWidth)
    {
        ComponentWidth = newWidth;
        CalculateStatusBars();
        
        // Also update visible items and highlight when component resizes
        // This ensures the status bar highlights update properly
        if (_moduleLoaded)
        {
            await UpdateVisibleItems();
        }
        else
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module != null)
        {
            await _module.InvokeVoidAsync("unobserveResize", thumbnailPanelElement);
            await _module.DisposeAsync();
        }

        _dotNetHelper?.Dispose();
        _scrollCts?.Cancel();
    }
    
    #endregion
    
    #region Helper classes

    public class ScrollInfo
    {
        public double ScrollLeft { get; set; }
        public double ClientWidth { get; set; }
        public double ScrollWidth { get; set; }
    }

    public class StatusBar
    {
        public List<StatusSegment> Segments { get; set; } = new();
    }

    public class StatusSegment
    {
        public double Left { get; set; }
        public double Width { get; set; }
        public string Color { get; set; }
    }

    public class HighlightSegment
    {
        public int BarIndex { get; set; }
        public double Left { get; set; }
        public double Width { get; set; }
    }

    public class ThumbnailItem
    {
        public ThumbnailData Data { get; set; }
        public bool IsSelected { get; set; }
        public int PositionLeft { get; set; }
    }
    
    #endregion
}

#region Thumbnail data model

/// <summary>
/// Represents the various processing states that can be assigned to thumbnails.
/// 
/// This enum is used to visually distinguish between different states of processing
/// in the ThumbnailPanel's status bar and individual thumbnails. It provides a
/// color-coded representation of processing progress and outcomes.
/// 
/// Used in job expanded views like ExtractParticles2DExpandedView and MotionAndCTF2DExpandedView
/// where thumbnails are typically assigned the Processed status for successfully processed items:
/// <code>
/// Status = ProcessingStatus.Processed
/// </code>
/// </summary>
public enum ProcessingStatus
{
    /// <summary>
    /// Item has not yet been processed (red color in status bar)
    /// </summary>
    Unprocessed,
    
    /// <summary>
    /// Item was processed, but the results are outdated (yellow/amber color in status bar)
    /// </summary>
    Outdated,
    
    /// <summary>
    /// Item was filtered out during processing (light blue color in status bar)
    /// </summary>
    FilteredOut,
    
    /// <summary>
    /// Item was successfully processed (green color in status bar)
    /// </summary>
    Processed,
    
    /// <summary>
    /// Item was manually deselected by the user (gray color in status bar)
    /// </summary>
    Deselected
}

/// <summary>
/// Maps ProcessingStatus values to color codes for visual representation.
/// 
/// This class provides a consistent color mapping for the different processing
/// statuses throughout the ThumbnailPanel components. These colors are used
/// in both the status bar and individual thumbnail status indicators.
/// </summary>
public static class StatusColorMapping
{
    /// <summary>
    /// Gets the CSS color code for a given processing status.
    /// </summary>
    /// <param name="status">The processing status value</param>
    /// <returns>A CSS hex color code (e.g., "#a0d598" for Processed status)</returns>
    public static string GetColor(ProcessingStatus status)
    {
        return status switch
        {
            ProcessingStatus.Unprocessed => "#f09d99",  // Light red
            ProcessingStatus.Outdated => "#f6d282",     // Light amber
            ProcessingStatus.FilteredOut => "#bae8fd",  // Light blue 
            ProcessingStatus.Processed => "#a0d598",    // Light green
            ProcessingStatus.Deselected => "#ddd",      // Light gray
            _ => "transparent"
        };
    }
}

/// <summary>
/// Maps ProcessingStatus values to human-readable text labels.
/// 
/// This class provides consistent textual representations of the different
/// processing statuses for use in tooltips, status text, and accessibility
/// descriptions throughout the ThumbnailPanel components.
/// </summary>
public static class StatusLabelMapping
{
    /// <summary>
    /// Gets a human-readable label for a given processing status.
    /// </summary>
    /// <param name="status">The processing status value, or null</param>
    /// <returns>A lowercase string representing the status (e.g., "processed")</returns>
    public static string GetLabel(ProcessingStatus? status)
    {
        return status switch
        {
            ProcessingStatus.Unprocessed => "unprocessed",
            ProcessingStatus.Outdated => "outdated",
            ProcessingStatus.FilteredOut => "filtered out",
            ProcessingStatus.Processed => "processed",
            ProcessingStatus.Deselected => "deselected",
            null => "no status",
            _ => "unknown"
        };
    }
}

/// <summary>
/// Represents the data for a single thumbnail in the ThumbnailPanel.
/// 
/// This class encapsulates all necessary information for displaying a thumbnail image
/// within the panel, including its content path, processing status, and selection state.
/// It's used extensively in job expanded views like ExtractParticles2DExpandedView and
/// MotionAndCTF2DExpandedView to display lists of processed micrographs or particles.
/// </summary>
public class ThumbnailData
{
    /// <summary>
    /// The zero-based index of the thumbnail within its collection.
    /// 
    /// Used for identification and ordering purposes. When constructing ThumbnailData objects
    /// in job expanded views, this is typically set to the index of the processed item:
    /// <code>
    /// thumbnails.Add(new ThumbnailData
    /// {
    ///     Index = i,
    ///     ImagePath = _job.VisThumbnail(_processedItems[i].Path),
    ///     Status = ProcessingStatus.Processed
    /// });
    /// </code>
    /// </summary>
    public int Index { get; set; }
    
    /// <summary>
    /// The file system path to the thumbnail image.
    /// 
    /// In practice, this is typically set using a job's VisThumbnail method, which
    /// returns the path to a generated thumbnail for a specific processed item:
    /// <code>
    /// ImagePath = _job.VisThumbnail(_processedItems[i].Path)
    /// </code>
    /// 
    /// This property is used to uniquely identify thumbnails (see Equals and GetHashCode),
    /// and is used by the Thumbnail component to display the actual image.
    /// </summary>
    public string ImagePath { get; set; }
    
    /// <summary>
    /// The file system paths to the optional animation images for the thumbnail.
    /// The animation is played on top of the static thumbnail image when the
    /// thumbnail is hovered over.
    /// </summary>
    public string[] AnimationPaths { get; set; }
    
    /// <summary>
    /// The processing status of the item represented by this thumbnail.
    /// 
    /// Used to visually indicate the state of each item in the status bar and in
    /// the thumbnail display. In job expanded views, this is commonly set to
    /// ProcessingStatus.Processed for successfully processed items.
    /// </summary>
    public ProcessingStatus? Status { get; set; }
    
    /// <summary>
    /// Primary label text to display on the thumbnail.
    /// </summary>
    public string Label1 { get; set; } = null;
    
    /// <summary>
    /// Secondary label text to display on the thumbnail.
    /// </summary>
    public string Label2 { get; set; } = null;
    
    /// <summary>
    /// The check state of the thumbnail when using CheckMode.Binary or CheckMode.Ternary.
    /// 
    /// This property is modified by the Thumbnail component when the user interacts with
    /// the checkbox and is propagated back to the parent through the CheckChanged event:
    /// <code>
    /// private async Task OnCheckChanged(bool value)
    /// {
    ///     Data.Check = value;
    ///     await CheckChanged.InvokeAsync(Data);
    /// }
    /// </code>
    /// </summary>
    public bool? Check { get; set; } = null;

    /// <summary>
    /// Determines if this ThumbnailData is equal to another object.
    /// 
    /// Two ThumbnailData objects are considered equal if they have the same ImagePath.
    /// This is used extensively for selection state tracking and rendering optimizations.
    /// This method is called in various contexts, including collection operations and
    /// when checking if a thumbnail is currently selected.
    /// </summary>
    /// <param name="obj">The object to compare with this instance</param>
    /// <returns>True if the objects are equal, False otherwise</returns>
    public override bool Equals(object obj)
    {
        return obj is ThumbnailData data && ImagePath == data.ImagePath;
    }

    /// <summary>
    /// Gets a hash code for this ThumbnailData based on its ImagePath.
    /// 
    /// This method is used in conjunction with Equals to support proper behavior in
    /// collections like HashSet and Dictionary.
    /// </summary>
    /// <returns>A hash code derived from the ImagePath property</returns>
    public override int GetHashCode()
    {
        return ImagePath.GetHashCode();
    }
}

/// <summary>
/// Defines the available modes for checkbox interaction with thumbnails.
/// 
/// This enum is used by both ThumbnailPanel and Thumbnail components to determine
/// how checkbox selection should work for thumbnails. It controls whether checkboxes
/// are displayed and what states they can have.
/// 
/// Used in Thumbnail.razor.cs to control the rendering of checkboxes:
/// <code>
/// [Parameter]
/// public CheckMode CheckMode { get; set; } = CheckMode.None;
/// </code>
/// </summary>
public enum CheckMode
{
    /// <summary>
    /// No checkbox is displayed (default)
    /// </summary>
    None,
    
    /// <summary>
    /// Binary checkbox that can be either checked (true) or unchecked (false)
    /// </summary>
    Binary,
    
    /// <summary>
    /// Ternary checkbox that can be checked (true), unchecked (false), or indeterminate (null)
    /// </summary>
    Ternary
}

#endregion