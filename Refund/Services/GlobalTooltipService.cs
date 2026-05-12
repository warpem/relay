using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Refund.Utils;

namespace Refund.Services
{
    /// <summary>
    /// Service for managing global tooltips that can appear anywhere in the application,
    /// not limited by component boundaries. Supports multiple concurrent tooltips
    /// that are anchored to existing DOM elements.
    /// </summary>
    public class GlobalTooltipService
    {
        private readonly ConcurrentDictionary<string, TooltipInfo> _activeTooltips = new();
        private readonly SemaphoreSlim _eventLock = new(1, 1);

        /// <summary>
        /// Event fired when tooltips are updated (added, removed, or modified).
        /// </summary>
        public event Func<object, EventArgs, Task> TooltipsChanged;

        /// <summary>
        /// Current collection of active tooltips.
        /// </summary>
        public IReadOnlyDictionary<string, TooltipInfo> ActiveTooltips => _activeTooltips;

        /// <summary>
        /// Registers or updates a tooltip with the service.
        /// </summary>
        /// <param name="id">Unique ID for this tooltip</param>
        /// <param name="info">Tooltip information including content and target element</param>
        public async Task RegisterTooltip(string id, TooltipInfo info)
        {
            _activeTooltips[id] = info;
            await InvokeTooltipsChanged(this, EventArgs.Empty);
        }

        /// <summary>
        /// Removes a tooltip from the service.
        /// </summary>
        /// <param name="id">ID of the tooltip to remove</param>
        public async Task RemoveTooltip(string id)
        {
            if (_activeTooltips.TryRemove(id, out _))
            {
                await InvokeTooltipsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Shows a tooltip with the given ID.
        /// </summary>
        /// <param name="id">ID of the tooltip to show</param>
        public async Task ShowTooltip(string id)
        {
            if (_activeTooltips.TryGetValue(id, out var info))
            {
                if (info.IsVisible)
                    return;
                
                info.IsVisible = true;
                await InvokeTooltipsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Hides a tooltip with the given ID.
        /// </summary>
        /// <param name="id">ID of the tooltip to hide</param>
        public async Task HideTooltip(string id)
        {
            if (_activeTooltips.TryGetValue(id, out var info))
            {
                if (!info.IsVisible)
                    return;
                
                info.IsVisible = false;
                await InvokeTooltipsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Removes all tooltips from the service.
        /// </summary>
        public async Task ClearAll()
        {
            if (_activeTooltips.Count > 0)
            {
                _activeTooltips.Clear();
                await InvokeTooltipsChanged(this, EventArgs.Empty);
            }
        }

        private async Task InvokeTooltipsChanged(object sender, EventArgs e)
        {
            if (TooltipsChanged == null)
                return;
            
            try
            {
                await _eventLock.WaitAsync();

                await TooltipsChanged.InvokeAllAsync(sender, e);
            }
            finally
            {
                _eventLock.Release();
            }
        }
    }

    /// <summary>
    /// Information about a tooltip to be rendered.
    /// </summary>
    public class TooltipInfo
    {
        /// <summary>
        /// ID of the target element to anchor the tooltip to.
        /// </summary>
        public string TargetElementId { get; set; } = string.Empty;

        /// <summary>
        /// The tooltip content to render.
        /// </summary>
        public RenderFragment Content { get; set; } = null!;

        /// <summary>
        /// Whether the tooltip is currently visible.
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Optional tooltip placement relative to the target element.
        /// </summary>
        public TooltipPlacement Placement { get; set; } = TooltipPlacement.Auto;

        /// <summary>
        /// Distance offset between tooltip and target element in pixels.
        /// </summary>
        public int Offset { get; set; } = 8;
    }

    /// <summary>
    /// Placement options for tooltips relative to their target element.
    /// </summary>
    public enum TooltipPlacement
    {
        /// <summary>
        /// Automatically choose the best placement based on available space.
        /// </summary>
        Auto,
        
        /// <summary>
        /// Place tooltip above the target element.
        /// </summary>
        Top,
        
        /// <summary>
        /// Place tooltip to the right of the target element.
        /// </summary>
        Right,
        
        /// <summary>
        /// Place tooltip below the target element.
        /// </summary>
        Bottom,
        
        /// <summary>
        /// Place tooltip to the left of the target element.
        /// </summary>
        Left,
        
        /// <summary>
        /// Choose only between left or right based on available space.
        /// </summary>
        Horizontal,
        
        /// <summary>
        /// Choose only between top or bottom based on available space.
        /// </summary>
        Vertical
    }
}