using System.Text;
using Aspose.Cells.GridJs;

using Microsoft.AspNetCore.Mvc;

namespace GridJsDemo.Controllers
{
    // Route: /GridJs/
    [Route("[controller]/[action]")]
    public class GridJsController : GridJsControllerBase
    {
        private readonly IGridJsService _gridJsService;
        private readonly IWebHostEnvironment _environment;

        // Constructor – GridJsControllerBase requires the service instance.
        public GridJsController(IGridJsService gridJsService, IWebHostEnvironment environment) : base(gridJsService)
        {
            _gridJsService = gridJsService;
            _environment = environment;
        }

        /// <summary>
        /// Returns the workbook JSON that GridJs can render.
        /// </summary>
        /// <param name="filename">File name relative to the server's file folder.</param>
        /// <param name="uid">A unique identifier generated on the client.</param>
        /// <returns>Plain‑text JSON response.</returns>
        [HttpGet]
        public IActionResult LoadSpreadsheet(string filename, string uid)
        {
            if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(uid))
            {
                return BadRequest("Both filename and uid are required.");
            }
            if (!string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal))
            {
                return BadRequest("The filename must not contain a directory path.");
            }

            // Resolve the absolute path of the workbook.
            string fullFilePath = GetFullFilePath(filename);
            if (!System.IO.File.Exists(fullFilePath))
            {
                return NotFound($"Workbook '{filename}' was not found.");
            }

            // Generate JSON with a unique identifier (uid) – this method is provided by GridJsService.
            StringBuilder json = _gridJsService.DetailFileJsonWithUid(fullFilePath, uid);

            // Return the JSON as plain text (required by GridJs client).
            return Content(json.ToString(), "text/plain", Encoding.UTF8);
        }

        public IActionResult Index()
        {
            return new ViewResult
            {
                ViewName = "Index"
            };
        }

        /// <summary>
        /// Helper – builds the absolute file path from the supplied filename.
        /// </summary>
        private string GetFullFilePath(string filename)
        {
            string rootPath = Path.Combine(_environment.WebRootPath, "files");
            return Path.Combine(rootPath, filename);
        }
    }
}
