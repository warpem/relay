using Microsoft.AspNetCore.Components;
using Refund.Services;
using System;
using System.Threading.Tasks;

namespace Refund.Components.GlobalTooltip
{
    public partial class GlobalTooltip : IDisposable
    {
        [Inject]
        private GlobalTooltipService TooltipService { get; set; } = null!;
        
        [Parameter]
        public string TooltipId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// The ID of the element that this tooltip should be anchored to.
        /// </summary>
        [Parameter, EditorRequired]
        public string TargetId { get; set; } = null!;

        /// <summary>
        /// The content to display inside the tooltip.
        /// </summary>
        [Parameter, EditorRequired]
        public RenderFragment ChildContent { get; set; }

        /// <summary>
        /// The placement of the tooltip relative to the target element.
        /// </summary>
        [Parameter]
        public TooltipPlacement Placement { get; set; } = TooltipPlacement.Auto;

        /// <summary>
        /// Distance offset between tooltip and target element in pixels.
        /// </summary>
        [Parameter]
        public int Offset { get; set; } = 8;

        /// <summary>
        /// Whether the tooltip is initially visible.
        /// </summary>
        [Parameter]
        public bool IsVisible { get; set; }

        /// <summary>
        /// Callback when the tooltip becomes visible.
        /// </summary>
        [Parameter]
        public EventCallback OnShown { get; set; }

        /// <summary>
        /// Callback when the tooltip is hidden.
        /// </summary>
        [Parameter]
        public EventCallback OnHidden { get; set; }

        protected override async Task OnInitializedAsync()
        {
            await RegisterTooltip();
        }

        protected override async Task OnParametersSetAsync()
        {
            // Update the tooltip info when parameters change
            await RegisterTooltip();
            
            // Handle initial visibility
            if (IsVisible)
            {
                await ShowTooltip();
            }
        }

        private async Task RegisterTooltip()
        {
            await TooltipService.RegisterTooltip(TooltipId,
                                                 new TooltipInfo
                                                 {
                                                     TargetElementId = TargetId,
                                                     Content = ChildContent,
                                                     IsVisible = IsVisible,
                                                     Placement = Placement,
                                                     Offset = Offset
                                                 });
        }

        /// <summary>
        /// Shows the tooltip.
        /// </summary>
        public async Task ShowTooltip()
        {
            await TooltipService.ShowTooltip(TooltipId);
            IsVisible = true;
            await OnShown.InvokeAsync();
        }

        /// <summary>
        /// Hides the tooltip.
        /// </summary>
        public async Task HideTooltip()
        {
            await TooltipService.HideTooltip(TooltipId);
            IsVisible = false;
            await OnHidden.InvokeAsync();
        }

        public async void Dispose()
        {
            await TooltipService.RemoveTooltip(TooltipId);
        }
    }
}