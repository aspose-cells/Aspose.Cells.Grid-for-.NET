/* wwwroot/js/gridjs-demo.js
   --------------------------------------------------------------
   Step‑by‑step script that:
   1. Generates a UUID for the session.
   2. Requests workbook JSON from the server.
   3. Loads the JSON into GridJs Spreadsheet.
   4. Configures server‑side update mode and image handling URLs.
   -------------------------------------------------------------- */
// Utility: generate a RFC4122 version‑4 UUID
function generateUUID() {
    // Simplified but sufficient for demo purposes
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
        const r = Math.random() * 16 | 0,
              v = c === 'x' ? r : (r & 0x3 | 0x8);
        return v.toString(16);
    });
}
// ----------------------------------------------------------------
// Configuration constants – match the routes defined in GridJsController
// ----------------------------------------------------------------
const queryJsonUrl   = "/GridJs/LoadSpreadsheet";
const updateUrl      = "/GridJs/UpdateCell";
const fileDownloadUrl = "/GridJs/Download";
const oleDownloadUrl  = "/GridJs/Ole";
const imageUrl        = "/GridJs/ImageUrl";
const imageUploadUrl1 = "/GridJs/AddImage";
const imageUploadUrl2 = "/GridJs/AddImageByURL";
const imageCopyUrl    = "/GridJs/CopyImage";
// Global GridJs instance
let xs = null;
// ----------------------------------------------------------------
// Main entry point – called when the page finishes loading
// ----------------------------------------------------------------
$(function () {
    const uid = generateUUID();               // Unique session identifier
    const filename = "Sample.xlsx";           // Change if you use a different file
    // Build the request URL with query string parameters
    const requestUrl = `${queryJsonUrl}?filename=${encodeURIComponent(filename)}&uid=${uid}`;
    // ----------------------------------------------------------------
    // 1️⃣ AJAX request: obtain workbook JSON from the server
    // ----------------------------------------------------------------
    $.ajax({
        url: requestUrl,
        method: "GET",
        dataType: "text",          // Server returns plain text (JSON string)
        success: function (responseJsonString) {
            const jsonData = JSON.parse(responseJsonString);
            loadWithOption(jsonData);
        },
        error: function (xhr, status, err) {
            console.error("Failed to load workbook JSON:", err);
        }
    });
});
/**
 * Loads the JSON data into the GridJs UI and applies the required options.
 * @param {Object} jsondata – The deserialized JSON object returned by the server.
 */
function loadWithOption(jsondata) {
    // ----------------------------------------------------------------
    // 2️⃣ Define GridJs load options
    // ----------------------------------------------------------------
    const option = {
        // Server‑side update mode – each cell edit will be POSTed to UpdateCell
        updateMode: 'server',
        updateUrl: updateUrl,
        // UI language (change as needed)
        local: 'en'
    };
    // Clean any previous instance
    $('#gridjs-demo-uid').empty();
    // Extract worksheet array and filename from the server payload
    const sheets = jsondata.data;
    const filename = jsondata.filename;
    // ----------------------------------------------------------------
    // 3️⃣ Initialise GridJs and bind it to the placeholder div
    // ----------------------------------------------------------------
    xs = x_spreadsheet('#gridjs-demo-uid', option)
        .loadData(sheets)
        .updateCellError(function (msg) {
            console.error("Cell update error:", msg);
        });
    // Optional UI tweaks
    if (!jsondata.showtabs) {
        xs.bottombar.hide();               // Hide sheet tabs if not required
    }
    // Store unique identifier and original filename (used by the server)
    xs.setUniqueId(jsondata.uniqueid);
    xs.setFileName(filename);
    // ----------------------------------------------------------------
    // 4️⃣ Set the active sheet and cell (fallback to first sheet if needed)
    // ----------------------------------------------------------------
    let activeSheetName = jsondata.actname;
    if (xs.bottombar.dataNames.indexOf(activeSheetName) >= 0) {
        xs.setActiveSheetByName(activeSheetName)
          .setActiveCell(jsondata.actrow, jsondata.actcol);
    } else {
        // Fallback – first visible worksheet
        activeSheetName = xs.bottombar.dataNames[0];
        xs.setActiveSheetByName(activeSheetName).setActiveCell(0, 0);
    }
    // ----------------------------------------------------------------
    // 5️⃣ Register image‑related and download URLs
    // ----------------------------------------------------------------
    xs.setImageInfo(imageUrl, imageUploadUrl1, imageUploadUrl2, imageCopyUrl, 1000,"/image/loading.gif");
    xs.setFileDownloadInfo(fileDownloadUrl);
    xs.setOleDownloadInfo(oleDownloadUrl);
    xs.setOpenFileUrl("/GridJs/Index"); // URL to reopen the demo page
}