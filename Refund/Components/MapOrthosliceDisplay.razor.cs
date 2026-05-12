using Microsoft.AspNetCore.Components;
using Refund.Services;

namespace Refund.Components;

public partial class MapOrthosliceDisplay
{
    [Parameter, EditorRequired] 
    public string MapOrthoslicesPath { get; set; }
    
    [Parameter] 
    public string MaskOrthoslicesPath { get; set; }
    
    [Parameter] 
    public string MaskIsolinesPath { get; set; }

    [Parameter] 
    public bool IsVerticalLayout { get; set; }
    
    [Parameter, EditorRequired] 
    public int RenderedImageSidelengthPx { get; set; }
    
    [Parameter, EditorRequired] 
    public int SpacingPx { get; set; }

    [Parameter] 
    public int BorderWidthPx { get; set; }
    
    [Parameter] 
    public string BorderColor { get; set; } = "mediumpurple";

    [Parameter] 
    public bool MaskIsolinesAreVisible { get; set; } = true;
    
    [Parameter] 
    public bool MaskOrthoslicesAreVisible { get; set; } = true;
    
    [Parameter] 
    public (int R, int G, int B) OverlayRGB { get; set; } = (1, 1, 0);
    
    [Inject]
    private FileService FileService { get; set; }

    private string Id { get; } = Guid.NewGuid().ToString("N");
    
    private string MapOrthoslicesUrl { get; set; }
    private string MaskOrthoslicesUrl { get; set; }
    private string MaskIsolinesUrl { get; set; }
    
    private string _previousMapOrthoslicesPath;
    private string _previousMaskOrthoslicesPath;
    private string _previousMaskIsolinesPath;
    
    private readonly Dictionary<string, string> _cacheMapOrthoslices = new();
    private readonly Dictionary<string, string> _cacheMaskOrthoslices = new();
    private readonly Dictionary<string, string> _cacheMaskIsolines = new();

    protected override async Task OnParametersSetAsync()
    {
        await Task.Run(() =>
        {
            if (MapOrthoslicesPath != _previousMapOrthoslicesPath)
            {
                _previousMapOrthoslicesPath = MapOrthoslicesPath;
                LoadMapOrthoslices();
            }

            if (MaskIsolinesPath != _previousMaskIsolinesPath)
            {
                _previousMaskIsolinesPath = MaskIsolinesPath;
                LoadMaskIsolines();
            }

            if (MaskOrthoslicesPath != _previousMaskOrthoslicesPath)
            {
                _previousMaskOrthoslicesPath = MaskOrthoslicesPath;
                LoadMaskOrthoslices();
            }
        });
    }

    private void LoadMapOrthoslices()
    {
        if (!string.IsNullOrWhiteSpace(MapOrthoslicesPath))
            MapOrthoslicesUrl = FileService.GetUrl(MapOrthoslicesPath);
    }

    private void LoadMaskIsolines()
    {
        if (!string.IsNullOrWhiteSpace(MaskIsolinesPath))
            MaskIsolinesUrl = FileService.GetUrl(MaskIsolinesPath);
    }
    
    private void LoadMaskOrthoslices()
    {
        if (!string.IsNullOrWhiteSpace(MaskOrthoslicesPath))
            MaskOrthoslicesUrl = FileService.GetUrl(MaskOrthoslicesPath);
    }
}