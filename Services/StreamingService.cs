﻿using System.IO;
using System.Threading.Tasks;

namespace PikaCore.Services
{
    public class StreamingService : IStreamingService
    {
        private readonly IFileService _fileDownloader;

        public StreamingService(IFileService fileDownloader)
        {
            _fileDownloader = fileDownloader;
        }

        public async Task<Stream> GetVideoByPath(string path)
        {
            var extension = Path.GetExtension(path);
            Stream outStream = null;
            switch (extension)
            {
                case ".mp4":
                case ".mp3":
                case ".m4a":
                    outStream = await _fileDownloader.DownloadAsStreamAsync(path);
                    break;
            }
            return outStream;
        }
    }
}
