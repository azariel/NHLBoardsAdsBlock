using HPPH;

namespace NHLBoardsAdsBlocker
{
    internal class ImageWithConversionDetails
    {
        internal byte[] ImagePixels { get; set; }
        internal IColorFormat ImageColorFormat { get; set; }
        internal int ImageWidth { get; set; }
        internal int ImageHeight { get; set; }
        internal DateTime CreatedAt { get; set; } = DateTime.MinValue;
    }
}
