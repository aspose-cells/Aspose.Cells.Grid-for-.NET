using Aspose.Cells.GridJs;
using Microsoft.AspNetCore.Mvc;

namespace GridJsDemo.Controllers
{
    /// <summary>
    /// Compatibility route for gridjs-spreadsheet 26.7.1 framework adapters.
    /// The package defaults to /GridJs2 while the native .NET demo uses /GridJs.
    /// </summary>
    [Route("[controller]/[action]")]
    public class GridJs2Controller : GridJsControllerBase
    {
        public GridJs2Controller(IGridJsService gridJsService) : base(gridJsService)
        {
        }
    }
}
