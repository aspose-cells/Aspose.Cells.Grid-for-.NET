// Program.cs
using Aspose.Cells.GridJs;
var builder = WebApplication.CreateBuilder(args);
var gridJsCacheDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "GridJsCache");
var httpsRedirectionEnabled = builder.Configuration.GetValue("HttpsRedirection:Enabled", true);
Directory.CreateDirectory(gridJsCacheDirectory);
// Add MVC services
builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();
// ---------------------------------------------------------------------
// GridJs service registration
// ---------------------------------------------------------------------
builder.Services.AddScoped<IGridJsService, GridJsService>();
builder.Services.Configure<GridJsOptions>(options =>
{
    // Keep the demo portable across Windows, macOS, and Linux.
    options.FileCacheDirectory = gridJsCacheDirectory;
    // • Base route name for all GridJs actions (e.g. /GridJs/LoadSpreadsheet)
    options.BaseRouteName = "/GridJs";
});
var app = builder.Build();
// ---------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    if (httpsRedirectionEnabled)
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
// Map default MVC route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHealthChecks("/health");
app.Run();
