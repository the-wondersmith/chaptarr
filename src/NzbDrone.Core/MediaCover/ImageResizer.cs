using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ImageSharpConfiguration = SixLabors.ImageSharp.Configuration;

namespace NzbDrone.Core.MediaCover
{
    public interface IImageResizer
    {
        void Resize(string source, string destination, int height);
    }

    public class ImageResizer : IImageResizer
    {
        // Covers come from remote metadata providers, so every decode runs on
        // untrusted bytes. Registering only the four formats covers are actually
        // served in keeps ImageSharp's TIFF codec out of the process entirely -
        // that is where its known heap out-of-bounds write (GHSA-v76p-62qx-wwq2)
        // and non-progressing BigTIFF loop (GHSA-wmxv-xphr-5c9g) live. A TIFF
        // cover is now rejected at detection instead of reaching that code.
        private static readonly ImageSharpConfiguration ImageConfiguration = BuildConfiguration();

        private readonly IDiskProvider _diskProvider;
        private readonly bool _enabled;

        public ImageResizer(IDiskProvider diskProvider, IPlatformInfo platformInfo)
        {
            _diskProvider = diskProvider;

            _enabled = true;
        }

        public void Resize(string source, string destination, int height)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                using var image = Image.Load(new DecoderOptions { Configuration = ImageConfiguration }, source);
                image.Mutate(x => x.Resize(0, height));

                // Let ImageSharp select the encoder from the destination extension. The
                // resizer is shared by author, narrator, and book covers; writing JPEG
                // bytes into a .png/.webp/.gif filename produces invalid HTTP content.
                image.Save(destination);
            }
            catch
            {
                if (_diskProvider.FileExists(destination))
                {
                    _diskProvider.DeleteFile(destination);
                }

                throw;
            }
        }

        private static ImageSharpConfiguration BuildConfiguration()
        {
            var configuration = new ImageSharpConfiguration(
                new JpegConfigurationModule(),
                new PngConfigurationModule(),
                new WebpConfigurationModule(),
                new GifConfigurationModule());

            // Thumbnails don't need super high quality
            configuration.ImageFormatsManager.SetEncoder(JpegFormat.Instance, new JpegEncoder
            {
                Quality = 95,
                ColorType = JpegEncodingColor.YCbCrRatio444
            });

            return configuration;
        }
    }
}
