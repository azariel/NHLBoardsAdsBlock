using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HPPH;
using ScreenCapture.NET;

namespace NHLBoardsAdsBlocker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Frames
        private const int maxFPS = 60;
        private const int maxFPSUpperTreshold = maxFPS + 10;
        private const int maxFPSLowerTreshold = maxFPS - 10;
        private const int timingToAttainFPSTargetInMs = (int)(((float)1 / maxFPS) * 1000);
        private const float nbMsToAcceptLateFrame = timingToAttainFPSTargetInMs / 2;// When a new frame is drawed, any frame that were generated BEFORE this one + nbMsToAcceptLateFrame are going to be dropped
        private const int censoringHalfWidth = 10;
        private const int censoringHeight = 200;
        private const int screenIndexToCapture = 0;
        private const int defaultNbCensoringThreads = 6;
        private const int maxNbCensoringThreads = 6;
        private const int maxNbDrawingThreads = 2;
        private const bool showCaptureBorder = true;

        [ThreadStatic]
        private DateTime lastGeneratedFrameDateTimeUtc = DateTime.UtcNow;

        [ThreadStatic]
        private static WriteableBitmap frameBitmap = null;

        // Create a screen-capture service
        IScreenCaptureService screenCaptureService = new DX11ScreenCaptureService();
        IScreenCapture screenCapture;

        // Channels to synchronize buffers between threads
        private Channel<ImageWithConversionDetails> CensoredFramesAsPixelsChannel = Channel.CreateBounded<ImageWithConversionDetails>(
                new BoundedChannelOptions(maxFPSUpperTreshold) // Hard limit of maxFPS frames being processed at any given moment in a single instance
                {
                    SingleWriter = true,
                    SingleReader = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

        private Channel<IImage> ImageFramesChannel = Channel.CreateBounded<IImage>(
                new BoundedChannelOptions(maxFPSUpperTreshold) // Hard limit of maxFPS frames being processed at any given moment in a single instance
                {
                    SingleWriter = true,
                    SingleReader = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

        public MainWindow()
        {
            InitializeComponent();
            Initialize();
        }

        private void Initialize()
        {
            RenderOptions.SetBitmapScalingMode(PreCuredImage, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(PreCuredImage, EdgeMode.Unspecified);
            PreCuredImage.Stretch = Stretch.None;
            PreCuredImage.HorizontalAlignment = HorizontalAlignment.Left;
            PreCuredImage.VerticalAlignment = VerticalAlignment.Top;

            // Get all available graphics cards
            IEnumerable<GraphicsCard> graphicsCards = screenCaptureService.GetGraphicsCards();

            // Get the displays from the graphics card(s) you are interested in
            IEnumerable<Display> displays = screenCaptureService.GetDisplays(graphicsCards.First());

            // Create a screen-capture for all screens you want to capture
            if (displays.Count() <= screenIndexToCapture)
            {
                throw new Exception($"Screen #{screenIndexToCapture} wasn't found. Only [{displays.Count()}] screens were found.");
            }

            screenCapture = screenCaptureService.GetScreenCapture(displays.ToArray()[screenIndexToCapture]);

            // Register the regions you want to capture on the screen
            // Capture the whole screen
            ICaptureZone fullscreen = screenCapture.RegisterCaptureZone(0, 0, screenCapture.Display.Width, screenCapture.Display.Height);
            // Capture a 100x100 region at the top left and scale it down to 50x50
            //ICaptureZone topLeft = screenCapture.RegisterCaptureZone(0, 0, 100, 100, downscaleLevel: 1);

            frameBitmap = new WriteableBitmap(fullscreen.Width, fullscreen.Height, 96, 96, PixelFormats.Pbgra32, null);
            PreCuredImage.Source = frameBitmap;

            // Create a thread to Capture the screen
            Task.Run(async () =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                stopwatch.Start();
                int nbRawImagesPerSecondIterator = 0;
                while (true)
                {
                    // Capture the screen
                    // This should be done in a loop on a separate thread as CaptureScreen blocks if the screen is not updated (still image).
                    if (!screenCapture.CaptureScreen())
                        continue;

                    CopyScreenCaptureToImage(screenCapture, fullscreen);
                    ++nbRawImagesPerSecondIterator;

                    int nbWaitingRawImages = ImageFramesChannel.Reader.Count;

                    if (stopwatch.ElapsedMilliseconds > 1000)
                    {
                        await LogRawFramesBuffer.Dispatcher.Invoke(async () =>
                        {
                            float timingMs = stopwatch.ElapsedMilliseconds / nbRawImagesPerSecondIterator;
                            LogRawFramesBuffer.Content = $"Raw Images buffer: {nbWaitingRawImages} ({timingMs} ms) <=> {nbRawImagesPerSecondIterator} FPS";
                        });

                        nbRawImagesPerSecondIterator = 0;
                        stopwatch.Restart();
                    }

                    // wait until we have space in the channel to add new raw images to process
                    if (nbWaitingRawImages >= maxFPS)
                    {
                        while (ImageFramesChannel.Reader.Count >= maxFPS)
                        {
                            await Task.Delay(15);
                        }
                    }
                }
            });

            // Create a watcher thread that'll adapt the Censoring workers according to the performance (spawn new instances when required, etc)
            Task.Run(async () =>
            {
                List<Task> imagesProcessorThreads = new();

                for (int i = 0; i < defaultNbCensoringThreads; i++)
                {
                    imagesProcessorThreads.Add(Task.Run(CensorRawImagesLoop));
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                Stopwatch stopwatchUI = Stopwatch.StartNew();
                int lastNbProcessedFrames = 0;
                while (true)
                {
                    if (!CensoredFramesAsPixelsChannel.Reader.CanCount)
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    if (stopwatchUI.ElapsedMilliseconds > 1000)
                    {
                        await LogCensoredFramesBuffer.Dispatcher.Invoke(async () =>
                        {
                            LogCensoredFramesBuffer.Content = $"Censored Images buffer: {CensoredFramesAsPixelsChannel.Reader.Count} ([{imagesProcessorThreads.Count}] threads)";
                        });

                        stopwatchUI.Restart();
                    }

                    // time to wait = wait for new thread to pick up the slack
                    if (stopwatch.ElapsedMilliseconds > 15000 && CensoredFramesAsPixelsChannel.Reader.Count <= 1 && imagesProcessorThreads.Count < maxNbCensoringThreads)
                    {
                        // Create a thread to process (remove ads from image)
                        imagesProcessorThreads.Add(Task.Run(CensorRawImagesLoop));

                        stopwatch.Restart();
                        continue;
                    }
                    // TODO: Reduce nb Threads when required

                    await Task.Delay(1000);
                }
            });

            // Create a watcher thread that'll adapt the Output workers according to the performance (spawn new instances when required, etc)
            Task.Run(async () =>
            {
                List<Task> imagesProcessorThreads = new()
                {
                    Task.Run(DrawImageOnScreenLoop),
                };

                Stopwatch stopwatch = Stopwatch.StartNew();
                Stopwatch stopwatchUI = Stopwatch.StartNew();
                int lastNbProcessedFrames = 0;
                while (true)
                {
                    if (!CensoredFramesAsPixelsChannel.Reader.CanCount)
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    if (stopwatchUI.ElapsedMilliseconds > 1000)
                    {
                        await LogProcessedFramesBuffer.Dispatcher.Invoke(async () =>
                        {
                            LogProcessedFramesBuffer.Content = $"Images to Render buffer: {CensoredFramesAsPixelsChannel.Reader.Count} ([{imagesProcessorThreads.Count}] rendering threads)";
                        });

                        stopwatchUI.Restart();
                    }

                    // time to wait = wait for new thread to pick up the slack
                    if (stopwatch.ElapsedMilliseconds > 15000 && CensoredFramesAsPixelsChannel.Reader.Count >= maxFPS / 2 && imagesProcessorThreads.Count < maxNbDrawingThreads)
                    {
                        // Create a thread to process (remove ads from image)
                        imagesProcessorThreads.Add(Task.Run(DrawImageOnScreenLoop));

                        stopwatch.Restart();
                        continue;
                    }
                    // TODO: Reduce nb Threads when required

                    await Task.Delay(1000);
                }
            });
        }

        private async Task CensorRawImagesLoop()
        {
            while (true)
            {
                if (!ImageFramesChannel.Reader.CanCount)
                {
                    await Task.Delay(50);
                    continue;
                }

                if (ImageFramesChannel.Reader.Count <= 0)
                {
                    // TODO: add UI hint
                    await Task.Delay(50);
                    continue;
                }

                var imageToProcess = await ImageFramesChannel.Reader.ReadAsync();
                byte[] framePixels = ConvertIImageToByteArray(imageToProcess, out DateTime generationDateTimeUtc);

                await CensoredFramesAsPixelsChannel.Writer.WriteAsync(new ImageWithConversionDetails
                {
                    CreatedAt = generationDateTimeUtc,
                    ImagePixels = framePixels,
                    ImageColorFormat = imageToProcess.ColorFormat,
                    ImageHeight = imageToProcess.Height,
                    ImageWidth = imageToProcess.Width,
                });

                // await Task.Delay(timingToAttainFPSTargetInMs);// TODO: timelapse.Ms - timingToAttainFPSTargetInMs
            }
        }

        private byte[] ConvertIImageToByteArray(IImage imageToProcess, out DateTime generationDateTimeUtc)
        {
            generationDateTimeUtc = DateTime.MinValue;

            if (imageToProcess == null)
                return null;

            int nbRows = imageToProcess.Rows.Count;
            int nbColumns = imageToProcess.Columns.Count;
            int strideLength = nbColumns * imageToProcess.ColorFormat.BytesPerPixel;
            byte[] pixelsBytes = new byte[strideLength * nbRows];

            // Censor accordingly to configuration
            long ByteIterator = 0;
            int[] markedColumnIterator = new int[imageToProcess.Columns.Count];
            int currentStrideOffset = pixelsBytes.Length - strideLength;
            ByteIterator = currentStrideOffset;
            for (int rowIndex = nbRows - 1; rowIndex >= 0; --rowIndex)
            {
                for (int columnIndex = 0; columnIndex < nbColumns; ++columnIndex)
                {
                    if (showCaptureBorder)
                    {
                        if (rowIndex <= 0 || columnIndex <= 0 || rowIndex >= nbRows - 1 || columnIndex >= nbColumns - 1)
                        {
                            pixelsBytes[ByteIterator] = 255;
                            ++ByteIterator;
                            pixelsBytes[ByteIterator] = 0;
                            ++ByteIterator;
                            pixelsBytes[ByteIterator] = 0;
                            ++ByteIterator;
                            pixelsBytes[ByteIterator] = 255;
                            ++ByteIterator;

                            continue;
                        }
                    }

                    IColor pixel = imageToProcess[columnIndex, rowIndex];

                    if (markedColumnIterator[columnIndex] > 0)
                    {
                        // TODO: Set pixel to black/White
                        pixelsBytes[ByteIterator] = 190;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 190;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 190;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 255;
                        ++ByteIterator;

                        --markedColumnIterator[columnIndex];
                    }
                    else
                    {
                        // If pixel is yellow, we'll mark this column
                        if (EvaluateTriggerOnPixel(pixel))
                        {
                            for (int i = Math.Max(0, columnIndex - censoringHalfWidth); i < Math.Min(nbColumns, columnIndex + censoringHalfWidth); i++)
                            {
                                markedColumnIterator[i] = censoringHeight;
                            }
                        }

                        // Uncomment this section to copy each pixels AS-IS, this would simply copy the pixels as they are, but with 60+ fps, it's a bit sluggish, so instead of
                        // copying the censored stream, we'll just show the censor on top of the original stream
                        //pixelsBytes[ByteIterator] = pixel.B;
                        //++ByteIterator;
                        //pixelsBytes[ByteIterator] = pixel.G;
                        //++ByteIterator;
                        //pixelsBytes[ByteIterator] = pixel.R;
                        //++ByteIterator;
                        //pixelsBytes[ByteIterator] = pixel.A;
                        //++ByteIterator;

                        // Transparent === do not censor
                        pixelsBytes[ByteIterator] = 0;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 0;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 0;
                        ++ByteIterator;
                        pixelsBytes[ByteIterator] = 0;
                        ++ByteIterator;
                    }
                }

                currentStrideOffset -= strideLength;
                ByteIterator = currentStrideOffset;
            }

            generationDateTimeUtc = DateTime.UtcNow;
            return pixelsBytes;
        }

        private bool EvaluateTriggerOnPixel(IColor pixel) => pixel.R > 150 && pixel.G > 120 && pixel.B < 100;

        private async Task DrawImageOnScreenLoop()
        {
            while (true)
            {
                try
                {
                    if (!CensoredFramesAsPixelsChannel.Reader.CanCount)
                    {
                        await Task.Delay(50);
                        continue;
                    }

                    if (CensoredFramesAsPixelsChannel.Reader.Count <= 0)
                    {
                        // TODO: add UI hint
                        await Task.Delay(50);
                        continue;
                    }

                    var imageToProcess = await CensoredFramesAsPixelsChannel.Reader.ReadAsync();

                    if (imageToProcess == null || imageToProcess.CreatedAt <= lastGeneratedFrameDateTimeUtc.AddMilliseconds(-nbMsToAcceptLateFrame))
                    {
                        // This generated frame is too old, get next
                        continue;
                    }

                    PreCuredImage.Dispatcher.Invoke(() =>
                    {
                        CopyIImagePixelsBufferToWriteableBitmapBuffer(imageToProcess, frameBitmap);
                    });

                    lastGeneratedFrameDateTimeUtc = DateTime.UtcNow;

                    // await Task.Delay(10);
                    //if (CensoredFramesAsPixelsChannel.Reader.Count <= 15)
                    //    await Task.Delay(250);// TODO: timelapse.Ms - timingToAttainFPSTargetInMs
                    //else if (CensoredFramesAsPixelsChannel.Reader.Count <= 30)
                    //    await Task.Delay(125);// TODO: timelapse.Ms - timingToAttainFPSTargetInMs
                    //else if (CensoredFramesAsPixelsChannel.Reader.Count <= 60)
                    //    await Task.Delay(75);// TODO: timelapse.Ms - timingToAttainFPSTargetInMs
                }
                catch (Exception e)
                {
                }
            }
        }

        private void CopyScreenCaptureToImage(IScreenCapture screenCapture, ICaptureZone fullscreen)
        {
            //Lock the zone to access the data. Remember to dispose the returned disposable to unlock again.
            IImage image;
            using (fullscreen.Lock())
            {
                // You have multiple options now:
                // 1. Access the raw byte-data
                ReadOnlySpan<byte> rawData = fullscreen.RawBuffer;

                // 2. Use the provided abstraction to access pixels without having to care about low-level byte handling
                // Get the image captured for the zone
                image = fullscreen.Image;
            }

            ImageFramesChannel.Writer.WriteAsync(image, CancellationToken.None);
        }

        private void CopyIImagePixelsBufferToWriteableBitmapBuffer(ImageWithConversionDetails frame, WriteableBitmap writeableBitmap)
        {
            if (frame == null || writeableBitmap == null)
            {
                // TODO: log
                return;
            }

            try
            {
                // Reserve the back buffer for updates.
                writeableBitmap.Lock();

                var dirtyRectangle = new Int32Rect(0, 0, frame.ImageWidth, frame.ImageHeight);
                unsafe
                {
                    writeableBitmap.WritePixels(dirtyRectangle, frame.ImagePixels, frame.ImageWidth * frame.ImageColorFormat.BytesPerPixel, 0);
                }

                // Specify the area of the bitmap that changed.
                writeableBitmap.AddDirtyRect(dirtyRectangle);
            }
            finally
            {
                // Release the back buffer and make it available for display.
                writeableBitmap.Unlock();
            }
        }
    }
}