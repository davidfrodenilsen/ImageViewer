using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Microsoft.Win32; // For FileDialog
using System.Threading.Tasks;
using System.Threading;
using System.Drawing; // For bildehåndtering
using System.Drawing.Imaging; // For EXIF-metadata
using DrawingPoint = System.Drawing.Point;
using WpfPoint = System.Windows.Point;




namespace ImageViewer
{
    public partial class MainWindow : Window
    {
        private Dictionary<string, BitmapImage> imageCache = new Dictionary<string, BitmapImage>();
        private List<string> files = new List<string>(); // Initialisert som tom liste
        private int currentIndex = -1;
        private CancellationTokenSource _cancellationTokenSource;
        private int _activeIndex = -1; // Holder styr på hvilket bilde som skal vises


        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += (s, e) => this.Focus(); // Sett fokus på vinduet når det lastes
            _cancellationTokenSource?.Dispose();
             _cancellationTokenSource = new CancellationTokenSource();
            SelectFolder();
        }


        private async void SelectFolder()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "All Files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                var selectedFile = dialog.FileName;
                var folderPath = Path.GetDirectoryName(selectedFile);

                if (folderPath != null)
                {
                    // Hent filene
                    files = await Task.Run(() => Directory.GetFiles(folderPath)
                        .Where(IsImageOrVideo)
                        .ToList());
                    currentIndex = files.IndexOf(selectedFile);

                    if (files.Any())
                    {
                        // Sett _activeIndex for det første bildet
                        _activeIndex = currentIndex;

                        // Last inn det første bildet synkront (høy prioritet)
                        LoadMedia(currentIndex);

                        // Start forhåndslasting i bakgrunnen
                        _ = Task.Run(() => PreloadAdjacentImages(_cancellationTokenSource.Token));
                    }
                }
            }
        }







        private async Task PreloadAdjacentImages(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            const int preloadCount = 2; // Redusert antall for raskere navigasjon
            const int maxConcurrentPreloads = 2; // Færre samtidige oppgaver

            using (SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentPreloads))
            {
                List<Task> preloadTasks = new List<Task>();

                for (int i = 1; i <= preloadCount; i++)
                {
                    int nextIndex = (currentIndex + i) % files.Count;
                    int prevIndex = (currentIndex - i + files.Count) % files.Count;

                    // Forhåndslast fremover
                    if (!imageCache.ContainsKey(files[nextIndex]))
                    {
                        preloadTasks.Add(PreloadImageAsync(files[nextIndex], semaphore, cancellationToken));
                    }

                    // Forhåndslast bakover
                    if (!imageCache.ContainsKey(files[prevIndex]))
                    {
                        preloadTasks.Add(PreloadImageAsync(files[prevIndex], semaphore, cancellationToken));
                    }
                }

                try
                {
                    await Task.WhenAll(preloadTasks);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Forhåndslasting avbrutt.");
                }
            }
        }





        private async Task PreloadImageAsync(string file, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken); // Begrens samtidige laster
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                var bitmap = LoadAndRotateImage(file); // Last bildet
                if (bitmap != null) // Sjekk at bildet ble lastet
                {
                    lock (imageCache)
                    {
                        if (!imageCache.ContainsKey(file)) // Sjekk igjen i tilfelle en annen tråd lastet det
                        {
                            imageCache[file] = bitmap;
                            ManageCache(); // Begrens cache-størrelse
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException)
            {
                Console.WriteLine($"Forhåndslasting av {file} avbrutt.");
            }
            finally
            {
                semaphore.Release();
            }
        }



        private void ManageCache()
        {
            const int maxCacheSize = 10; // Mindre cache for raskere håndtering

            if (imageCache.Count > maxCacheSize)
            {
                var keysToRemove = imageCache.Keys
                    .Where(key => !IsAdjacentToCurrent(key))
                    .Take(imageCache.Count - maxCacheSize)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    imageCache.Remove(key);
                }
            }
        }

        // Hjelpemetode for å sjekke om et bilde er nært nåværende indeks
        private bool IsAdjacentToCurrent(string filePath)
        {
            int index = files.IndexOf(filePath);
            return Math.Abs(currentIndex - index) <= 2; // Juster radius etter behov
        }



        private void PreloadImage(string file)
        {
            if (!file.EndsWith(".mp4") && !file.EndsWith(".avi"))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(file);
                    bitmap.DecodePixelWidth = 200; // Bruk lav oppløsning for miniatyrbilder
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Feil under forhåndslasting av bildet: {ex.Message}");
                }
            }
        }






        private bool IsImageOrVideo(string file)
        {
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".mp4", ".avi" };
            return extensions.Contains(Path.GetExtension(file).ToLower());
        }

        private void LoadMedia(int index)
        {
            if (index < 0 || index >= files.Count)
            {
                Console.WriteLine($"Ugyldig indeks: {index}");
                return;
            }

            // Oppdater aktiv indeks
            _activeIndex = index;
            currentIndex = index;
            var file = files[index];
            Console.WriteLine($"Laster media for indeks {index}: {file}");

            try
            {
                // Sjekk om bildet allerede er i cachen
                if (imageCache.TryGetValue(file, out var cachedBitmap))
                {
                    MainImage.Source = cachedBitmap;
                    return;
                }

                if (file.EndsWith(".mp4") || file.EndsWith(".avi"))
                {
                    MainImage.Visibility = Visibility.Collapsed;
                    MainVideo.Visibility = Visibility.Visible;
                    MainVideo.Source = new Uri(file);
                    MainVideo.Play();
                }
                else
                {
                    MainVideo.Visibility = Visibility.Collapsed;
                    MainImage.Visibility = Visibility.Visible;

                    // Last lavoppløselig bilde synkront
                    var lowResBitmap = LoadImageWithWidth(file, 400);
                    if (lowResBitmap != null)
                    {
                        MainImage.Source = lowResBitmap;
                    }

                    // Last fulloppløselig bilde i bakgrunnen og legg i cache
                    _ = Task.Run(() =>
                    {
                        var fullResBitmap = LoadAndRotateImage(file);
                        if (fullResBitmap != null)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (index == _activeIndex)
                                {
                                    MainImage.Source = fullResBitmap;
                                }

                                lock (imageCache)
                                {
                                    imageCache[file] = fullResBitmap;
                                    ManageCache();
                                }
                            });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under innlasting av media for indeks {index}: {ex.Message}");
            }
        }







        private async Task LoadFullResImageAsync(string filePath)
        {
            try
            {
                var bitmap = await Task.Run(() => LoadImageWithWidth(filePath, 0)); // 0 for full oppløsning
                if (bitmap != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (filePath == files[currentIndex]) // Sjekk at filen fortsatt er aktiv
                        {
                            MainImage.Source = bitmap;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under fulloppløselig innlasting av {filePath}: {ex.Message}");
            }
        }



        private BitmapImage LoadImageWithWidth(string filePath, int width)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.DecodePixelWidth = width; // Angi ønsket bredde
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze(); // Gjør bildet trådtrygt
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under innlasting av bilde med bredde {width}: {ex.Message}");
                return null;
            }
        }



        private BitmapImage LoadLowResImage(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.DecodePixelWidth = 400; // Lav oppløsning for rask innlasting
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under innlasting av lavoppløselig bilde: {ex.Message}");
                return null;
            }
        }

        private BitmapImage LoadFullResImage(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under innlasting av fulloppløselig bilde: {ex.Message}");
                return null;
            }
        }

        private BitmapImage LoadAndRotateImage(string filePath)
        {
            try
            {
                using (var image = System.Drawing.Image.FromFile(filePath))
                {
                    // Hent EXIF-orientering
                    const int orientationId = 0x112;
                    if (image.PropertyIdList.Contains(orientationId))
                    {
                        var orientation = image.GetPropertyItem(orientationId).Value[0];
                        RotateFlipType rotateFlip = RotateFlipType.RotateNoneFlipNone;

                        switch (orientation)
                        {
                            case 6:
                                rotateFlip = RotateFlipType.Rotate90FlipNone;
                                break;
                            case 3:
                                rotateFlip = RotateFlipType.Rotate180FlipNone;
                                break;
                            case 8:
                                rotateFlip = RotateFlipType.Rotate270FlipNone;
                                break;
                        }

                        image.RotateFlip(rotateFlip);
                    }

                    using (var memoryStream = new MemoryStream())
                    {
                        image.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                        memoryStream.Seek(0, SeekOrigin.Begin);

                        // Opprett BitmapImage på UI-tråden
                        return Application.Current.Dispatcher.Invoke(() =>
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = memoryStream;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            bitmap.Freeze(); // Gjør BitmapImage trådtrygt
                            return bitmap;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under rotasjon eller konvertering: {ex.Message}");
                return null; // Returner null ved feil
            }
        }








        private BitmapSource RotateBitmapAccordingToExif(BitmapImage bitmap)
        {
            try
            {
                // Hent metadataene
                var frame = BitmapFrame.Create(bitmap);
                var metadata = (BitmapMetadata)frame.Metadata;

                if (metadata != null && metadata.ContainsQuery("System.Photo.Orientation"))
                {
                    // Hent orienteringsverdi
                    var orientation = (ushort)metadata.GetQuery("System.Photo.Orientation");

                    // Opprett transformasjon basert på orienteringen
                    var transform = new RotateTransform();
                    switch (orientation)
                    {
                        case 6: // Rotasjon 90 grader med klokken
                            transform.Angle = 90;
                            break;
                        case 3: // Rotasjon 180 grader
                            transform.Angle = 180;
                            break;
                        case 8: // Rotasjon 270 grader mot klokken
                            transform.Angle = 270;
                            break;
                        default: // Ingen rotasjon nødvendig
                            return bitmap;
                    }

                    // Påfør rotasjonen
                    var rotatedBitmap = new TransformedBitmap(bitmap, transform);
                    return rotatedBitmap;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under behandling av EXIF-data: {ex.Message}");
            }

            // Returner originalen hvis ingen rotasjon er nødvendig
            return bitmap;
        }




        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Right:
                    Console.WriteLine("Tastetrykk: Right");
                    Navigate(1); // Gå til neste bilde
                    break;

                case Key.Left:
                    Console.WriteLine("Tastetrykk: Left");
                    Navigate(-1); // Gå til forrige bilde
                    break;

                case Key.Delete:
                    Console.WriteLine("Tastetrykk: Delete");
                    DeleteCurrentFile(); // Slett gjeldende fil
                    break;

                case Key.Escape:
                    Console.WriteLine("Tastetrykk: Escape");
                    Application.Current.Shutdown(); // Lukk programmet
                    break;

                default:
                    Console.WriteLine($"Tastetrykk: {e.Key}");
                    break;
            }
        }





        private void MainImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainImage == null) return;

            // Hent eller opprett transformasjonsgruppe
            var transformGroup = MainImage.RenderTransform as TransformGroup;
            if (transformGroup == null)
            {
                transformGroup = new TransformGroup();
                transformGroup.Children.Add(new ScaleTransform());
                transformGroup.Children.Add(new TranslateTransform());
                MainImage.RenderTransform = transformGroup;
            }

            // Hent eksisterende transformasjoner
            var scaleTransform = transformGroup.Children[0] as ScaleTransform;
            var translateTransform = transformGroup.Children[1] as TranslateTransform;

            // Beregn minimum zoom for å fylle skjermen
            double minScaleX = ActualWidth / MainImage.ActualWidth;
            double minScaleY = ActualHeight / MainImage.ActualHeight;
            double minScale = Math.Min(minScaleX, minScaleY);

            // Bestem zoom-faktor
            double zoom = e.Delta > 0 ? 1.1 : 1 / 1.1;

            // Beregn nye skalaverdier
            double newScaleX = scaleTransform.ScaleX * zoom;
            double newScaleY = scaleTransform.ScaleY * zoom;

            // Beregn offset før zooming
            var mousePosition = e.GetPosition(MainImage);
            var beforeZoom = MainImage.TranslatePoint(mousePosition, this);

            // Sjekk om ny skalering er innenfor grensene
            if (newScaleX >= minScale && newScaleY >= minScale)
            {
                // Oppdater skalering
                scaleTransform.ScaleX = newScaleX;
                scaleTransform.ScaleY = newScaleY;

                // Hvis vi zoomer til minimum, sentrer bildet
                if (newScaleX <= minScale * 1.01 && newScaleX >= minScale * 0.99 &&
                    newScaleY <= minScale * 1.01 && newScaleY >= minScale * 0.99)
                {
                    CenterImage(scaleTransform, translateTransform);
                }
                else
                {
                    // Beregn ny posisjon etter zoom
                    var afterZoom = MainImage.TranslatePoint(mousePosition, this);

                    // Juster translatetransform for å holde musepeker på samme relative posisjon
                    translateTransform.X += beforeZoom.X - afterZoom.X;
                    translateTransform.Y += beforeZoom.Y - afterZoom.Y;
                }
            }

            e.Handled = true;
        }



        private bool IsImageCentered(ScaleTransform scaleTransform, TranslateTransform translateTransform)
        {
            double scaledImageWidth = MainImage.ActualWidth * scaleTransform.ScaleX;
            double scaledImageHeight = MainImage.ActualHeight * scaleTransform.ScaleY;

            double centerX = (ActualWidth - scaledImageWidth) / 2;
            double centerY = (ActualHeight - scaledImageHeight) / 2;

            // Sjekk om translasjonen allerede plasserer bildet nær senteret
            return Math.Abs(translateTransform.X - centerX) < 0.1 &&
                Math.Abs(translateTransform.Y - centerY) < 0.1;
        }



        private void CenterImage(ScaleTransform scaleTransform, TranslateTransform translateTransform)
        {
            // Beregn dimensjonene av det skalerte bildet
            double scaledImageWidth = MainImage.ActualWidth * scaleTransform.ScaleX;
            double scaledImageHeight = MainImage.ActualHeight * scaleTransform.ScaleY;

            // Beregn midtpunktet for å sentrere bildet
            double centerX = (ActualWidth - scaledImageWidth) / 2;
            double centerY = (ActualHeight - scaledImageHeight) / 2;

            // Oppdater translatetransform for å plassere bildet i midten
            translateTransform.X = centerX;
            translateTransform.Y = centerY;
        }







        private WpfPoint _lastMousePosition;
        private WpfPoint _imageOffset = new WpfPoint(0, 0);

        public WpfPoint GetImageCoordinates(WpfPoint screenPoint)
        {
            if (MainImage == null) return new WpfPoint(0, 0);

            var transformGroup = MainImage.RenderTransform as TransformGroup;
            if (transformGroup == null) return screenPoint;

            var scaleTransform = transformGroup.Children[0] as ScaleTransform;
            var translateTransform = transformGroup.Children[1] as TranslateTransform;

            double x = (screenPoint.X - translateTransform.X) / scaleTransform.ScaleX;
            double y = (screenPoint.Y - translateTransform.Y) / scaleTransform.ScaleY;

            return new WpfPoint(x, y);
        }




        private void Navigate(int step)
        {
            if (!files.Any()) return;

            // Avbryt eventuell pågående forhåndslasting
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            // Beregn ny indeks
            int newIndex = (currentIndex + step + files.Count) % files.Count;

            // Oppdater aktiv indeks
            _activeIndex = newIndex;
            currentIndex = newIndex;

            // Last inn bildet synkront for rask respons
            LoadMedia(newIndex);

            // Start forhåndslasting av tilstøtende bilder i bakgrunnen
            _ = Task.Run(() => PreloadAdjacentImages(_cancellationTokenSource.Token));
        }









        private void DeleteCurrentFile()
        {
            if (currentIndex < 0 || currentIndex >= files.Count) return;

            var fileToDelete = files[currentIndex];
            files.RemoveAt(currentIndex);
            File.Delete(fileToDelete);

            if (currentIndex >= files.Count) currentIndex = files.Count - 1;

            LoadMedia(currentIndex);

        }


        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainVideo.IsLoaded && MainVideo.CanPause)
            {
                if (MainVideo.CanPause)
                {
                    MainVideo.Pause();
                    PlayPauseButton.Content = "Play";
                }
                else
                {
                    MainVideo.Play();
                    PlayPauseButton.Content = "Pause";
                }
            }
        }

        private void ProgressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainVideo.NaturalDuration.HasTimeSpan)
            {
                var duration = MainVideo.NaturalDuration.TimeSpan.TotalSeconds;
                MainVideo.Position = TimeSpan.FromSeconds(e.NewValue * duration / 100);
            }
        }
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            // Dette kan brukes til å håndtere musebevegelser
        }

    }
}
