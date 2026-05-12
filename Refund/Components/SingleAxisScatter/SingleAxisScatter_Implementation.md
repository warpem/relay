# SingleAxisScatter Component Implementation Notes

## Overview
We've implemented a high-performance scatter plot component for Blazor that can efficiently display 20,000+ data points. The component is based on the original WPF SingleAxisScatter control from Warp, but reimplemented for Blazor with modern web technologies.

## File Locations

1. **Component Files**:
   - `/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor`
   - `/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor.cs`
   - `/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor.css`
   - `/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor.js`
   - `/Refund/Components/SingleAxisScatter/README.md`

2. **Service File**:
   - `/Refund/Services/ScatterHighlightService.cs`

## Architecture Choices

1. **Rendering Approach**: 
   - HTML Canvas via JavaScript interop for the main plot to handle large numbers of points efficiently
   - SVG for the histogram display on the left side
   - HTML/CSS for axes, labels, and the highlight overlay

2. **Performance Optimizations**:
   - Pre-rendering points to Canvas when data changes
   - Binary search for finding the closest point on hover
   - Debouncing resize events (100ms) and zoom events (100ms)
   - Mouse position-based scrolling for zoomed plots
   - Client-side interaction handling to minimize server round-trips
   - Optimized point data transfer (only sending necessary values to JS)

3. **Cross-Component Communication**:
   - `ScatterHighlightService` for synchronizing between multiple scatter plots
   - Event-based communication to highlight the same point across components

## Key Features

1. **Data Display**:
   - Points drawn from left to right in the sequence they appear in the collection
   - Each point has a Y coordinate value (can be NaN to skip drawing)
   - Customizable point colors with RGBA support
   - Support for arbitrary metadata storage with each point

2. **User Interaction**:
   - Zooming on X axis with Ctrl+Scroll or Shift+Scroll
   - Mouse-position based horizontal scrolling for zoomed content
   - Point highlighting on hover with customizable tooltips
   - Click events to interact with points

3. **Visual Elements**:
   - Left-side histogram showing data distribution
   - Y-axis range highlighting
   - Automatic axis value formatting based on data range
   - Responsive layout that adapts to container size

4. **Customization**:
   - Configurable point radius
   - Custom tooltip templates using RenderFragment
   - Zoom level binding
   - Min/max axis value overrides

## Component Structure

1. **Data Models**:
   ```csharp
   public class ScatterPoint
   {
       public double Value { get; set; }  // Y coordinate (NaN to skip drawing)
       public System.Drawing.Color Color { get; set; }  // RGBA color
       public object? Metadata { get; set; }  // Optional metadata
       
       public ScatterPoint(double value, System.Drawing.Color color, object? metadata = null)
       {
           Value = value;
           Color = color;
           Metadata = metadata;
       }
   }
   ```

2. **Main Parameters**:
   - `Points` - `ObservableCollection<ScatterPoint>` for data binding
   - `RangeHighlightMin/RangeHighlightMax` - Range to highlight on Y axis
   - `PointRadius` - Size of points
   - `Zoom` - Zoom level for X axis
   - `TooltipTemplate` - Custom tooltip content

3. **Events**:
   - `PointClicked` - Fires when a point is clicked
   - `HighlightChanged` - Fires when highlighted point changes

## JavaScript Implementation Details

1. **Canvas Rendering**:
   - Points positioned with horizontal spacing = plot_width / n_points
   - Left/right padding = spacing / 2
   - Points drawn with configurable radius and colors
   - Transparent background for theme compatibility

2. **Point Location**:
   - Horizontal position: `index * stepX + offsetX`
   - Vertical position: `(valueMax - point.value) * stepY`

3. **Interactivity**:
   - Binary search to find closest point on hover
   - Highlights point if cursor is within proximity of the point radius
   - Shows HTML overlay for highlighted point
   - Handles wheel events for zooming with modifier keys
   - Uses mouse position for horizontal scrolling when zoomed

4. **Optimization**:
   - ResizeObserver for efficient dimension tracking
   - Debounced event handlers to minimize server calls
   - Local updates for immediate feedback with delayed server notifications
   - Canvas-based rendering to minimize DOM elements

## Related Components and Dependencies

1. **DynamicTooltipContent**:
   - `/Refund/Components/DynamicTooltipContent.razor`
   - `/Refund/Components/DynamicTooltipContent.razor.cs`
   - Used for flexible tooltip implementation

2. **JS Module Loading**:
   ```csharp
   _module = await JSRuntime.InvokeAsync<IJSObjectReference>(
       "import", "./_content/Refund/Components/SingleAxisScatter/SingleAxisScatter.razor.js");
   ```

## Implementation Challenges & Solutions

1. **Challenge**: Handling large datasets efficiently
   **Solution**: Canvas-based rendering with optimized data transfer

2. **Challenge**: Responsive layout with good performance
   **Solution**: ResizeObserver with debounced updates

3. **Challenge**: Conflict between zoom and scroll
   **Solution**: Mouse position-based scrolling and modifier keys for zoom

4. **Challenge**: Cross-component synchronization
   **Solution**: Scoped service with event-based communication

5. **Challenge**: Minimizing server round-trips
   **Solution**: Debounced events and client-side handling

## Integration Notes

1. **Service Registration**:
   Add to Program.cs:
   ```csharp
   builder.Services.AddScatterHighlightService();
   ```

2. **Usage Example**:
   ```razor
   <SingleAxisScatter 
       Points="@_points"
       PointRadius="3"
       @bind-Zoom="@_zoom"
       PointClicked="OnPointClicked">
       <TooltipTemplate>
           @context.Value.ToString("F2")
           @if (context.Metadata is Movie movie)
           {
               <div>@movie.Name</div>
           }
       </TooltipTemplate>
   </SingleAxisScatter>
   ```

3. **Multiple Synchronized Plots**:
   ```razor
   <div class="scatter-container">
       <SingleAxisScatter Points="@_points1" @bind-Zoom="@_zoom" />
       <SingleAxisScatter Points="@_points2" @bind-Zoom="@_zoom" />
   </div>
   ```

## Future Enhancement Ideas

1. Implement delta updates for very large datasets (only sending changed points)
2. Add Y-axis zooming capability
3. Add support for X values (not just sequence position)
4. Add trend lines or region highlighting
5. Implement touch/mobile optimizations
6. Add export/save functionality

## Lessons Learned

1. Canvas rendering is much more efficient than SVG or DOM for large datasets
2. Binary search is effective for point location in sorted sequences
3. Debouncing events is critical for performance with interactive components
4. Local UI updates before server notification provides better user experience
5. Mouse position-based scrolling provides smoother user experience than traditional scrollbars
6. Careful service design enables effective component synchronization