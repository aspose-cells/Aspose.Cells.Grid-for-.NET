// Program.cs
using Aspose.Cells.GridJs;
var builder = WebApplication.CreateBuilder(args);
// Add MVC services
builder.Services.AddControllersWithViews();
// ---------------------------------------------------------------------
// GridJs service registration
// ---------------------------------------------------------------------
builder.Services.AddScoped<IGridJsService, GridJsService>();
builder.Services.Configure<GridJsOptions>(options =>
{
    // • Directory where GridJs caches converted files.
    //   Make sure the folder exists and the application has write permission.
    options.FileCacheDirectory = @"D:\storage\Aspose.Cells.GridJs\";
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
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
// Map default MVC route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();