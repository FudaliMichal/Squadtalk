using System.Collections;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Shared;
using Squadtalk.Extensions;
using Squadtalk.Services;
using tusdotnet.Interfaces;
using tusdotnet.Models;

namespace Squadtalk.Controllers;

[ApiController]
[Route("api/folder")]
public class FolderFileController : ControllerBase
{

    private readonly ILogger<FolderFileController> _logger;
    private readonly string _filepath;

    public FolderFileController(ILogger<FolderFileController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _filepath = configuration.GetString("FilePath");
    }

    [HttpPost("posted")]
    public async Task<IActionResult> Post([FromForm] IEnumerable<IFormFile> files)
    {
        

        foreach (var file in files)
        {
            _logger.LogInformation("{FileName}",file.FileName);
            
            
            var randName = Path.GetRandomFileName();

            var fullPath = Path.Join(_filepath, randName);
            
            await using var fileStream = System.IO.File.Create(fullPath);
            await using var userFile = file.OpenReadStream();
            
            await userFile.CopyToAsync(fileStream);
            
        }

        return Ok("s2pack");
    }
}
