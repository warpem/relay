using Microsoft.FluentUI.AspNetCore.Components;
using ComponentDev.Components;
using Refund.Services;
using Relay.Emoji;
using EmojiInfo = Relay.Emoji.EmojiInfo;
using Emojis = Microsoft.FluentUI.AspNetCore.Components.Emojis;

Console.WriteLine(EmojiLibrary.ByGlyph.Count);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddServerSideBlazor();
builder.Services.AddFluentUIComponents();

builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

builder.Services.AddScoped<ScatterHighlightService>();
builder.Services.AddScoped<GlobalTooltipService>();

// Add file service for secure file access
builder.Services.AddSingleton<FileService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorPages();
app.MapControllers();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
