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

        // Constructor – GridJsControllerBase requires the service instance.
        public GridJsController(IGridJsService gridJsService) : base(gridJsService)
        {
            _gridJsService = gridJsService;
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
            // Resolve the absolute path of the workbook.
            string fullFilePath = GetFullFilePath(filename);

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
/*
            public new FileResult GetFile(string id)
        {
            using (Stream fs = _gridJsService.GetFile(id))
            {
                String contentType = "application/octet-stream";
                if(id.EndsWith(".zip"))
                {
                    contentType = "application/zip";
                }
                return new FileStreamResult(fs, contentType);
            }
        }
        */

        /// <summary>
        /// Helper – builds the absolute file path from the supplied filename.
        /// </summary>
        private string GetFullFilePath(string filename)
        {
            // Example: files are stored under wwwroot/files/
            string rootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "files");
            return Path.Combine(rootPath, filename);
        }
    }
}