﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PikaCore.Controllers.Helpers;
using PikaCore.Controllers.Hubs;
using PikaCore.Data;
using PikaCore.Extensions;
using PikaCore.Models;
using PikaCore.Models.File;
using PikaCore.Security;
using PikaCore.Services;

namespace PikaCore.Controllers.App
{
    public class StorageController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IArchiveService _archiveService;
        private readonly IFileService _fileService;
        private readonly IUrlGenerator _urlGeneratorService;
        private readonly IFileLoggerService _loggerService;
        private readonly IConfiguration _configuration;
        private readonly StorageIndexContext _storageIndexContext;
        private bool _wasArchivingCancelled = true;
        private readonly IHubContext<StatusHub> _hubContext;
        private readonly IdDataProtection _idDataProtection;
        
        #region TempDataMessages
        [TempData(Key = "showGenerateUrlPartial")]public  bool ShowGenerateUrlPartial { get; set; }
        [TempData(Key = "returnMessage")] public  string ReturnMessage { get; set; }

        #endregion

        public StorageController(SignInManager<ApplicationUser> signInManager,
               IArchiveService archiveService, IFileService fileService, IUrlGenerator iUrlGenerator,
               StorageIndexContext storageIndexContext,
               IFileLoggerService fileLoggerService,
               IHubContext<StatusHub> hubContext,
               IConfiguration configuration,
               IdDataProtection idDataProtection)
        {
            _signInManager = signInManager;
            _archiveService = archiveService;
            _fileService = fileService;
            _urlGeneratorService = iUrlGenerator;
            _storageIndexContext = storageIndexContext;
            _loggerService = fileLoggerService;
            _hubContext = hubContext;
            _configuration = configuration;
            _idDataProtection = idDataProtection;
            ((ArchiveService)_archiveService).PropertyChanged += PropertyChangedHandler;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Index()
        {
            return RedirectToActionPermanent(nameof(Browse));
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Browse(string path, int offset = 0, int count = 50)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = Path.DirectorySeparatorChar.ToString();
            }
            var osUser = _configuration.GetSection("OsUser")["OsUsername"];
            var contents = GetContents(path);

            ViewData["path"] = path;
            ViewData["returnUrl"] =  UnixHelper.GetParent(path);

            if (null == contents)
                return RedirectToAction("Error", "Home",
                    new ErrorViewModel
                    {
                        ErrorCode = -1,
                        Message = "There was an error while reading directory content."
                    });
            var lrmv = new FileResultViewModel();
            if (contents.Exists)
            {
                lrmv.Contents = contents;
                lrmv.ContentsList = contents.ToList();

                await lrmv.SortContents();
                var fileInfosList = lrmv.ContentsList;

                var pageCount = fileInfosList.Count / count;

                SetPagingParams(offset, count, pageCount);

                Set("Offset", offset.ToString(), 3600);
                Set("Count", count.ToString(), 3600);
                Set("PageCount", pageCount.ToString(), 3600);

                if (!HttpContext.User.IsInRole("Admin"))
                {
                    try
                    {
                        lrmv.ApplyAcl(osUser);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _loggerService.LogToFileAsync(LogLevel.Debug, "localhost", ex.Message);
                    }
                }

                if (fileInfosList.Count > count)
                {
                    lrmv.ApplyPaging(offset, count);
                }

                var fileInfo = _fileService.RetrieveFileInfoFromSystemPath(path);
                if (fileInfo.Exists)
                {
                    return RedirectToAction(nameof(Download), new { @id = fileInfo.Name });
                }

                return View(lrmv);
            }

            ReturnMessage = "The resource doesn't exist on the filesystem.";
            return View(lrmv);
        }

        [HttpGet]
        [Authorize(Roles = "Admin, FileManagerUser, User")]
        [Route("/[controller]/[action]/{name?}")]
        public async Task<IActionResult> GenerateUrl(string name, string returnUrl)
        {
            var entryName = _idDataProtection.Decode(name);

            if (!string.IsNullOrEmpty(entryName))
            {
                ShowGenerateUrlPartial = false;
                try
                {
                    var s = _storageIndexContext.IndexStorage.ToList().Find(record => record.AbsolutePath.Equals(entryName));

                    if (s == null)
                    {
                        s = new StorageIndexRecord
                        {
                            AbsolutePath = entryName,
                            Urlhash = _urlGeneratorService.GenerateId(entryName),
                            UserId = await IdentifyUser(),
                            Expires = true
                        };
                        _storageIndexContext.Add(s);
                        
                        await _storageIndexContext.SaveChangesAsync();
                    }
                    else
                    {
                        if (s.ExpireDate.Date == DateTime.Now.Date || s.ExpireDate.Date < DateTime.Now.Date)
                        {
                            _storageIndexContext.Update(s);
                            await _storageIndexContext.SaveChangesAsync();
                        }
                    }

                    TempData["urlhash"] = s.Urlhash;
                    ShowGenerateUrlPartial = true;
                    var port = HttpContext.Request.Host.Port;
                    TempData["host"] = HttpContext.Request.Host.Host + 
                                       (port != null ? ":" + HttpContext.Request.Host.Port : "");
                    TempData["protocol"] = "https";
                    TempData["returnUrl"] = returnUrl;
                    return RedirectToAction(nameof(Browse), new { @path = returnUrl });
                }
                catch (InvalidOperationException ex)
                {
                    _loggerService.LogToFileAsync(LogLevel.Error, 
                        HttpContext.Connection.RemoteIpAddress.ToString(), 
                        ex.Message);
                    ReturnMessage = ex.Message;
                    return RedirectToAction(nameof(Browse), new { @path = returnUrl });
                }
            }

            ReturnMessage = "Couldn't generate token for that resource.";
            return RedirectToAction(nameof(Browse), new { @path = returnUrl });
        }

       
        [HttpGet]
        [AllowAnonymous]
        [Route("/[controller]/[action]/{id?}")]
        public ActionResult PermanentDownload(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                StorageIndexRecord s = null;
                try
                {
                    s = _storageIndexContext.IndexStorage.SingleOrDefault(record => record.Urlhash.Equals(id));

                    if (s != null)
                    {
                        var returnPath = _fileService.RetrieveSystemPathFromAbsolute(s.AbsolutePath);
                        if (!s.Expires || (s.ExpireDate != DateTime.Now && s.ExpireDate > DateTime.Now))
                        {
                                var fileBytes = _fileService.AsStreamAsync(s.AbsolutePath);
                                var name = Path.GetFileName(s.AbsolutePath);
                                if (fileBytes != null)
                                {

                                    _loggerService.LogToFileAsync(LogLevel.Information,
                                        HttpContext.Connection.RemoteIpAddress.ToString(),
                                        "Successfully returned requested resource"
                                        + s.AbsolutePath);
                                    return File(fileBytes, MimeAssistant.GetMimeType(name), name, true);
                                }
                                _loggerService.LogToFileAsync(LogLevel.Error, HttpContext.Request.Host.Value,
                                    "Couldn't read requested resource: " + s.AbsolutePath);
                                ReturnMessage = "Couldn't read requested resource: " + s.Urlid;
                                return RedirectToAction(nameof(Browse));
                        }
                        ReturnMessage =
                                        "It seems that this url expired today, you need to generate a new one.";
                        
                        return RedirectToAction(nameof(Browse), new { path = returnPath });
                    }
                    ReturnMessage = "It seems that given token doesn't exist in the database.";
                    return RedirectToAction(nameof(Browse));
                }
                catch (InvalidOperationException ex)
                {
                    ReturnMessage = s != null 
                        ? "Couldn't read requested resource: " + s.Urlid 
                        : "Database error occured.";
                    
                    _loggerService.LogToFileAsync(LogLevel.Error, 
                        HttpContext.Connection.RemoteIpAddress.ToString(), 
                        ex.Message);
                    return RedirectToAction(nameof(Browse));
                }
            }
            ReturnMessage = "No id given or database is down.";
            return RedirectToAction(nameof(Browse));
        }

        [HttpGet]
        [AllowAnonymous]
        public ActionResult Download(string id,  string returnUrl, bool z = false)
        {
            id = _idDataProtection.Decode(id);
            try{
                if (!string.IsNullOrEmpty(id))
                {
                    var fileInfo = _fileService.RetrieveFileInfoFromAbsolutePath(id); 
                    var path = fileInfo.PhysicalPath;
                    _loggerService.LogToFileAsync(LogLevel.Information, HttpContext.Connection.RemoteIpAddress.ToString(), $"Trying to download {path}");
                    if (!fileInfo.Exists)
                    {
                        ReturnMessage = "File doesn't exist on server's filesystem.";
                        return RedirectToAction(nameof(Browse), new { @path = returnUrl });
                    }
                    
                    if (Directory.Exists(path))
                    {
                        ReturnMessage = "This is a folder, cannot download it directly.";
                        return RedirectToAction(nameof(Browse), new { @path = returnUrl });
                    }
                    
                    var fs =  _fileService.AsStreamAsync(path);
                    var mime = MimeAssistant.GetMimeType(path);
                    return File(fs, mime, fileInfo.Name);
                }
            }catch(Exception e){
                _loggerService.LogToFileAsync(LogLevel.Warning, "localhost", e.Message);
            }
            ReturnMessage = "Resource id cannot be null.";
            return RedirectToAction(nameof(Browse), new { @path = returnUrl });

        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Thumb(string id)
        {
            _loggerService.LogToFileAsync(LogLevel.Warning, "localhost", $"Request for thumb of {id}.");
            var format = _configuration.GetSection("Images")["Format"].ToLower();
            var thumbFileName = $"{id}.{format}";
            var absoluteThumbPath = Path.Combine(_configuration.GetSection("Images")["ThumbDirectory"],
                                                 thumbFileName
                                                 );
            var thumbFileStream = _fileService.AsStreamAsync(absoluteThumbPath);
            return File(thumbFileStream, "image/jpeg");
        }

        [HttpPost]
        [AllowAnonymous]
	    [DisableFormValueModelBinding]
        [AutoValidateAntiforgeryToken]
        public async Task<IActionResult> Upload(List<IFormFile> files, string returnUrl)
        {
            files.RemoveAll(element => element.Length > Constants.MaxUploadSize);
            var size = files.Sum(f => f.Length);
            var filePath = Constants.Tmp + Constants.UploadTmp;

            foreach (var formFile in files.Where(formFile => formFile.Length > 0))
            {
                await using var stream = new FileStream(filePath 
                                                        + Path.DirectorySeparatorChar 
                                                        + formFile.FileName, 
                    FileMode.Create);
                await formFile.CopyToAsync(stream);
            }

            var uploadedFiles = Directory.GetFiles(Constants.Tmp + Constants.UploadTmp);
            foreach (var file in uploadedFiles)
            {
                await _fileService.MoveFromTmpAsync(Path.GetFileName(file), Constants.UploadDirectory);
            }

            ReturnMessage = files.Count 
                            + " files uploaded of summary size " 
                            + FileSystemAccessor.DetectUnitBySize(size);
	    /*if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
    {
        ModelState.AddModelError("File", 
            $"The request couldn't be processed (Error 1).");
        // Log error

        return BadRequest(ModelState);
    }

    var boundary = MultipartRequestHelper.GetBoundary(
        MediaTypeHeaderValue.Parse(Request.ContentType),
        _defaultFormOptions.MultipartBoundaryLengthLimit);
    var reader = new MultipartReader(boundary, HttpContext.Request.Body);
    var section = await reader.ReadNextSectionAsync();

    while (section != null)
    {
        var hasContentDispositionHeader = 
            ContentDispositionHeaderValue.TryParse(
                section.ContentDisposition, out var contentDisposition);

        if (hasContentDispositionHeader)
        {
            // This check assumes that there's a file
            // present without form data. If form data
            // is present, this method immediately fails
            // and returns the model error.
            if (!MultipartRequestHelper
                .HasFileContentDisposition(contentDisposition))
            {
                ModelState.AddModelError("File", 
                    $"The request couldn't be processed (Error 2).");
                // Log error

                return BadRequest(ModelState);
            }
            else
            {
                // Don't trust the file name sent by the client. To display
                // the file name, HTML-encode the value.
                var trustedFileNameForDisplay = WebUtility.HtmlEncode(
                        contentDisposition.FileName.Value);
                var trustedFileNameForFileStorage = Path.GetRandomFileName();

                // **WARNING!**
                // In the following example, the file is saved without
                // scanning the file's contents. In most production
                // scenarios, an anti-virus/anti-malware scanner API
                // is used on the file before making the file available
                // for download or for use by other systems. 
                // For more information, see the topic that accompanies 
                // this sample.

                var streamedFileContent = await FileHelpers.ProcessStreamedFile(
                    section, contentDisposition, ModelState, 
                    _permittedExtensions, _fileSizeLimit);

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                using (var targetStream = System.IO.File.Create(
                    Path.Combine(_targetFilePath, trustedFileNameForFileStorage)))
                {
                    await targetStream.WriteAsync(streamedFileContent);

                    _logger.LogInformation(
                        "Uploaded file '{TrustedFileNameForDisplay}' saved to " +
                        "'{TargetFilePath}' as {TrustedFileNameForFileStorage}", 
                        trustedFileNameForDisplay, _targetFilePath, 
                        trustedFileNameForFileStorage);
                }
            }
        }

        // Drain any remaining section body that hasn't been consumed and
        // read the headers for the next section.
        	section = await reader.ReadNextSectionAsync();
    		}*/
            return RedirectToAction(nameof(Browse), new { @path = returnUrl });
        }

        /**
         * <remarks>
         * Deprecated
         * </remarks>
         */
        [HttpGet]
        [AutoValidateAntiforgeryToken]
        [Authorize(Roles = "Admin, FileManagerUser")]
        public async Task<IActionResult> Archive(string id)
        {
            var output = string.Concat(Constants.Tmp, Path.GetDirectoryName(id), ".zip");

            var path = "";

            if (!((ArchiveService)_archiveService).WasStartedAlready())
            {
		        var awaiter = Task.Delay(TimeSpan.FromSeconds(10d));
		        Task.WhenAll(awaiter).Wait();
                var task = await _archiveService.ZipDirectoryAsync(path, output);
               
                await _hubContext.Clients.User(_signInManager.UserManager.GetUserId(HttpContext.User))
                    .SendAsync("ReceiveArchivingStatus", "Zipping task started...");
		        Task.WhenAll(task).Wait();

                if (task.IsCompleted)
                {
                    if (!_wasArchivingCancelled)
                    {
                        return RedirectToAction(nameof(Download), 
                            new { @id = string.Concat(id, ".zip"), 
                                @z = true 
                            });
                    }
                    ReturnMessage = "Archiving was cancelled by user.";
                    return RedirectToAction(nameof(Browse));

                }

                ReturnMessage = "Something unexpected happened.";
                return RedirectToAction(nameof(Browse));
            }

            ReturnMessage = "All signs on the Earth and on the sky say that you have already ordered Pika Cloud to zip something.";
            return RedirectToAction(nameof(Browse));
        }


        [HttpPost]
        [AutoValidateAntiforgeryToken]
        [Authorize(Roles = "Admin, FileManagerUser")]
        public async Task<IActionResult> Create(string name, string returnUrl)
        {
            var pattern = new Regex(@"\W|_");

            if (!pattern.Match(name).Success)
            {
                try
                {
                    var dirInfo = await _fileService.Create(_fileService.RetrieveAbsoluteFromSystemPath(name));
                    
                    ReturnMessage = "Successfully created directory: " + dirInfo.Name;
                    _loggerService.LogToFileAsync(LogLevel.Information,
                        HttpContext.Connection.RemoteIpAddress.ToString(), 
                        "Created directory: " 
                        + dirInfo.FullName);
                    return RedirectToAction(nameof(Browse), new { path = returnUrl });
                }
                catch (Exception e)
                {
                    _loggerService.LogToFileAsync(LogLevel.Error, HttpContext.Connection.RemoteIpAddress.ToString(),
                        "Couldn't create directory because of " + e.Message);
                    ReturnMessage = "Error: Couldn't create directory.";
                    return RedirectToAction(nameof(Browse), new { path = returnUrl });
                }
            }

            ReturnMessage = "You cannot use non-alphabetic characters in directory names.";
            return RedirectToAction(nameof(Browse), new { path = returnUrl });
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public IActionResult Delete(string currentPath)
        {

            var contentsList = this.GetContents(currentPath).ToList();
            var toBeDeletedItemsModel = new DeleteResourcesViewModel();
            toBeDeletedItemsModel.FromFileInfoList(contentsList);

            return View(toBeDeletedItemsModel);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteConfirmation(DeleteResourcesViewModel deleteResourcesViewModel)
        {
            var contents = deleteResourcesViewModel.ToBeDeletedItems;
            var returnPath = _fileService.RetrieveSystemPathFromAbsolute(
                Directory.GetParent(deleteResourcesViewModel.ToBeDeletedItems[0]).FullName);
            if (contents.Count > 0)
            {
                try
                {
                    await _fileService.Delete(contents);

                    _loggerService.LogToFileAsync(LogLevel.Information, 
                        HttpContext.Connection.RemoteIpAddress.ToString(), 
                        "Successfully deleted elements.");
                    ReturnMessage = "Successfully deleted elements.";
                    return RedirectToAction(nameof(Browse), new { path = returnPath });
                }
                catch
                { 
                    ReturnMessage = "Error: Couldn't delete resource.";
                    return RedirectToAction(nameof(Browse), new { path =  returnPath});
                }
            }
            else
            {
                ReturnMessage = "Error: Nothing to be deleted.";
                return RedirectToAction(nameof(Browse), new { path = returnPath });
            }
        }

        [HttpGet]
        [Authorize(Roles = "Admin, FileManagerUser")]
        [AutoValidateAntiforgeryToken]
        public IActionResult Rename(string n)
        {
            var name = _fileService.RetrieveAbsoluteFromSystemPath(n);
            ViewData["path"] = name;
            var rfm = new RenameFileModel
            {
                IsDirectory = IsDirectory(name),
                OldName = name,
                AbsoluteParentPath = Directory.GetParent(name).FullName,
                ReturnUrl = n
            };
            _loggerService.LogToFileAsync(LogLevel.Error, 
                HttpContext.Connection.RemoteIpAddress.ToString(), 
                "Viewing Rename view for " + name);

            return View(rfm);
        }

        [HttpPost]
        [AutoValidateAntiforgeryToken]
        [Authorize(Roles = "Admin, FileManagerUser")]
        public IActionResult Rename(RenameFileModel rfm)
        {
            if (!string.IsNullOrEmpty(rfm.NewName))
            {
                var isRenamed = _fileService.Move(Path.Combine(rfm.AbsoluteParentPath, rfm.OldName), Path.Combine(rfm.AbsoluteParentPath, rfm.NewName));
                if (!isRenamed)
                {
                    ReturnMessage = "Couldn't renamed to " + rfm.NewName;
                    return RedirectToAction(nameof(Browse),
                        new {path = rfm.ReturnUrl});
                }
                ReturnMessage = "Successfully renamed to " + rfm.NewName;

                return RedirectToAction(nameof(Browse), 
                    new { path = rfm.ReturnUrl });

            }
            ModelState.AddModelError(HttpContext.TraceIdentifier, "New name cannot be empty!");
            return View(rfm);
        }

        [HttpPost]
        [Authorize(Roles = "Admin, FileManagerUser")]
        [AutoValidateAntiforgeryToken]
        public IActionResult CancelDownloadAsync(string returnUrl)
        {
            _archiveService.Cancel();
            _hubContext.Clients.User(_signInManager.UserManager.GetUserId(HttpContext.User))
                .SendAsync("ArchivingCancelled", "Cancelled by the user.");

            return RedirectToAction(nameof(Browse), 
                new { path = returnUrl });
        }

        #region HelperMethods

        private async Task<string> IdentifyUser()
        {
            var user = await _signInManager.UserManager.GetUserAsync(HttpContext.User);
            return user != null
                ? await _signInManager.UserManager.GetEmailAsync(user)
                : HttpContext.Connection.RemoteIpAddress.ToString();
        }

        private IDirectoryContents GetContents(string systemPath)
        {
            return _fileService.GetDirectoryContents(systemPath);
        }

        public void PropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            _wasArchivingCancelled = false;
        }

        private static bool IsDirectory(string name)
        {
            return !System.IO.File.Exists(name);
        }

        #endregion

        #region CookierHelperMethods

        private void Set(string key, string value, int? expireTime)
        {
            var option = new CookieOptions
            {
                Expires = expireTime.HasValue
                    ? DateTime.Now.AddMinutes(expireTime.Value)
                    : DateTime.Now.AddMilliseconds(10)
            };
            
            Response.Cookies.Append(key, value, option);
        }

        private void SetPagingParams(int offset, int count, int pageCount)
        {
            TempData["Offset"] = offset;
            TempData["Count"] = count;
            TempData["PageCount"] = pageCount;
        }

        #endregion
    }
}
