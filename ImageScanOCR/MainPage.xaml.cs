using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls;

namespace ImageScanOCR {
    /// <summary>
    /// OCR main page
    /// explorer image from left side panel
    /// select image to display image and process ocr to display text in right panel
    /// </summary>
    public sealed partial class MainPage : Page {
        //observable
        ObservableCollection<ExplorerItem> Breadcrumbs = new ObservableCollection<ExplorerItem>();
        ObservableCollection<ExplorerItem> ExplorerList = new ObservableCollection<ExplorerItem>();
        ObservableCollection<Language> LanguageList = new ObservableCollection<Language>();
        ObservableCollection<SoftwareBitmapSource> ImageList = new ObservableCollection<SoftwareBitmapSource>();

        //
        Language SelectedLang = null;
        SoftwareBitmap CurrentBitmap = null;
        List<string> CurrentOcrResult = new List<string>() { };
        List<string> PrevOcrResult = new List<string>() { };
        ApplicationDataContainer LocalSettings = SettingHandler.GetSetting();
        CancellationTokenSource TokenSource = new CancellationTokenSource();
        string CurrentProcessedItemName = "New Document";
        private bool IsCropped = false;
        private BoxCoordinates cropBoxCoordinates;
        CoreCursor cursorBeforePointerEntered = Window.Current.CoreWindow.PointerCursor;
        private bool textFieldFocused = false;

        // Capture API objects.
        private SizeInt32 _lastSize;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private CanvasDevice _canvasDevice;
        private CompositionGraphicsDevice _compositionGraphicsDevice;
        private Compositor _compositor;
        private CompositionDrawingSurface _surface;
        private CanvasBitmap _currentFrame;
        private DispatcherTimer _captureDispatcherTimer = new DispatcherTimer();
        private DateTime _lastOcrRuntime = DateTime.Now;

        public MainPage() {
            Debug.WriteLine("Start==============================");
            TaskScheduler.UnobservedTaskException += OnUnobservedException;
            this.InitializeComponent();
            InitTitleBar();
            InitFolderViewList();
            InitLanguageList();
            InitTooltip();
            InitFolderRefresh();
            SetupCapture();
        }




        private static void OnUnobservedException(object sender, UnobservedTaskExceptionEventArgs e) {
            // Occurs when an exception is not handled on a background thread.
            // ie. A task is fired and forgotten Task.Run(() => {...})
            // suppress and handle it manually.
            Debug.WriteLine("OnUnobservedException==============================");
            Debug.WriteLine(e.Exception.ToString());
            e.SetObserved();
        }




        private void InitTitleBar() {
            var titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            // Hide default title bar.
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;

            // Set XAML element as a draggable region.
            //Window.Current.SetTitleBar(AppTitleBar);
        }



        private void InitTooltip() {
            String isTooltipShowed = LocalSettings.Values["IsTooltipShowed"] as string;
            if (isTooltipShowed == "false") {
                MoreLanguageTip.IsOpen = true;
                LocalSettings.Values["IsTooltipShowed"] = "true";
            }
        }





        //language setting============================================================================================================================
        private void InitLanguageList() {
            LanguageList = new ObservableCollection<Language>(OcrProcessor.GetOcrLangList());
            SelectLanguageFromSetting();
        }


        private void SelectLanguageFromSetting() {
            String settingLang = LocalSettings.Values["Language"] as string;

            //set matched lang as selected item
            //if no saved lang use first item
            for (int i = 0; i < LanguageList.Count; i++) {
                if (LanguageList[i].LanguageTag == settingLang || settingLang == "") {
                    LangCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            //save language when changed and reprocess image
            SelectedLang = e.AddedItems[0] as Language;
            LocalSettings.Values["Language"] = SelectedLang.LanguageTag;
            ProcessImage(CurrentBitmap);
        }

        private async void AddLanguage_Click(object sender, RoutedEventArgs e) {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage-adddisplaylanguage"));
        }





        //folder explorer============================================================================================================================

        private async void InitFolderViewList() {
            Breadcrumbs.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) => UpdateExplorerListView();

            try {
                //open recent picked folder 
                string token = (string)LocalSettings.Values["RecentAccessFolderToken"];
                if (token != "" && StorageApplicationPermissions.FutureAccessList.ContainsItem(token)) {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    Breadcrumbs.Add(new ExplorerItem(folder));
                } else {
                    // if no recent picked folder, show picture library
                    Breadcrumbs.Add(new ExplorerItem(KnownFolders.PicturesLibrary));
                }
            } catch (FileNotFoundException) {
                Debug.WriteLine("File not found");
            }
        }



        private void InitFolderRefresh() {
            var dispatcherTimer = new DispatcherTimer();
            dispatcherTimer.Tick += (object source, object e) => RefreshFileList();
            dispatcherTimer.Interval = new TimeSpan(0, 0, 5);
            dispatcherTimer.Start();
        }


        private async void RefreshFileList() {
            if (Breadcrumbs.Any()) {
                List<ExplorerItem> recentFileList = await Breadcrumbs.Last().GetChildItemList();

                //remove deleted file from dir
                for (int i = 0; i < ExplorerList.Count(); i++) {
                    if (!recentFileList.Any(item => item.Name == ExplorerList[i].Name)) {
                        ExplorerList.RemoveAt(i);
                        i--;
                    }
                }

                //append new add file from dir
                for (int i = 0; i < recentFileList.Count(); i++) {
                    if (ExplorerList.Count <= i || recentFileList[i].Name != ExplorerList[i].Name) {
                        ExplorerList.Insert(i, recentFileList[i]);
                    }
                }
            }
        }




        private void FolderBreadcrumbBar_ItemClicked(muxc.BreadcrumbBar sender, muxc.BreadcrumbBarItemClickedEventArgs clickedItem) {
            // Don't process last index (current location),  right most one
            if (clickedItem.Index == Breadcrumbs.Count - 1) {
                return;
            }

            // Remove breadcrumbs at the end until 
            // you get to the one that was clicked.
            while (Breadcrumbs.Count > clickedItem.Index + 1) {
                Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
            }
        }


        //if is folder open , update breadcrum and exploer list
        //if file open image and process
        private async void FolderListView_ItemClick(object sender, ItemClickEventArgs e) {
            var clickedItem = e.ClickedItem as ExplorerItem;
            if (clickedItem.Label == "folder" || clickedItem.Label == "pdf") {
                Breadcrumbs.Add(clickedItem);
            } else if (clickedItem.Label == "file" || clickedItem.Label == "pdfPage") {
                CurrentProcessedItemName = clickedItem.Name;
                CurrentBitmap = await clickedItem.GetBitmapImage();
                ProcessImage(CurrentBitmap);
            }
        }




        //open folder and reset
        private async void OpenFolder_Click(object sender, RoutedEventArgs e) {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder pickedFolder = await folderPicker.PickSingleFolderAsync();
            if (pickedFolder != null) {
                // Folder was picked you can now use it
                var token = StorageApplicationPermissions.FutureAccessList.Add(pickedFolder);
                LocalSettings.Values["RecentAccessFolderToken"] = token;
                Breadcrumbs.Clear();
                Breadcrumbs.Add(new ExplorerItem(pickedFolder));
            }
        }

        private async void UpdateExplorerListView() {
            if (Breadcrumbs.Any()) {
                List<ExplorerItem> recentFileList = await Breadcrumbs.Last().GetChildItemList();
                ExplorerList.Clear();
                recentFileList.ForEach(ExplorerList.Add);
            }
        }







        //image process=============================================================================================================================================
        private async void Rotate_Click(object sender, RoutedEventArgs e) {
            CurrentBitmap = await ImageProcessor.SoftwareBitmapRotate(CurrentBitmap);
            ProcessImage(CurrentBitmap);
        }

        public async void ProcessImage(SoftwareBitmap imageItem, bool updateDisplayImage = true) {
            if (imageItem == null) {
                return;
            }

            if (updateDisplayImage) {
                resetImageProcess();
                DisplayImage(imageItem);
                showSingleImageBox();
            }
            if (IsCropped) {
                (uint x, uint y, uint w, uint h) = cropBoxCoordinates.GetRatioXYWH(imageItem.PixelWidth, imageItem.PixelHeight);
                if (w > 1 && h > 1) { //if too small crop skip
                    imageItem = await ImageProcessor.GetCroppedImage(imageItem, x, y, w, h);
                }
            }


            CurrentOcrResult = await OcrProcessor.GetText(imageItem, SelectedLang);
            DisplayText();
        }

        public async void DisplayImage(SoftwareBitmap imageItem) {
            PreviewImage.Source = await ImageProcessor.getImageSource(imageItem);
        }

        private void showSingleImageBox() {
            ImageSingleBox.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Visible;
            MyCanvas.Visibility = Visibility.Visible;
        }
        private void showScrollImageBox() {
            ImageListView.Visibility = Visibility.Visible;
        }
        private void showCaptureBox() {
            ImageSingleBox.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Visible;
            CaptureBox.Visibility = Visibility.Visible;
            MyCanvas.Visibility = Visibility.Visible;
        }
        private void resetImageProcess() {
            //image reset
            ImageSingleBox.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            MyCanvas.Visibility = Visibility.Collapsed;
            CropBox.Visibility = Visibility.Collapsed;
            CurrentOcrResult.Clear();
            IsCropped = false;
            cropBoxCoordinates = null;

            //batch reset
            ImageListView.Visibility = Visibility.Collapsed;
            ImageList.Clear();
            CancelBatchProcess();
            ProgressItem.IsActive = false;
            ShowBatchButton();

            //capture reset
            CaptureBox.Visibility = Visibility.Collapsed;
            ShowCaptureButton();
            StopCapture();
        }

        private void ScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) {
            var scrollViewer = ((ScrollViewer)sender);
            ImageSingleGrid.Width = scrollViewer.ActualWidth;
            ImageSingleGrid.Height = scrollViewer.ActualHeight;
        }



        //Batch  ============================================================================================================================

        private async void BatchProcess_Click(object sender, RoutedEventArgs e) {
            resetImageProcess();
            showScrollImageBox();
            ShowCancelBatchButton();
            ProgressItem.IsActive = true;

            await ProcessBatch();

            ShowBatchButton();
            DisplayText();
            ProgressItem.IsActive = false;
        }

        private void ShowBatchButton() {
            BatchProcess.Visibility = Visibility.Visible;
            CancelBatch.Visibility = Visibility.Collapsed;
        }
        private void ShowCancelBatchButton() {
            BatchProcess.Visibility = Visibility.Collapsed;
            CancelBatch.Visibility = Visibility.Visible;
        }


        private async Task ProcessBatch() {
            TokenSource = new CancellationTokenSource();
            var token = TokenSource.Token;

            foreach (ExplorerItem item in ExplorerList) {
                if (token.IsCancellationRequested) {
                    break;
                }

                if (item.Label == "pdfPage" || item.Label == "file") {
                    CurrentProcessedItemName = ((ExplorerItem)Breadcrumbs.Last()).Name;
                    SoftwareBitmap bitmapImage = await item.GetBitmapImage();

                    List<string> textList = await OcrProcessor.GetText(bitmapImage, SelectedLang);
                    textList.Add("");                                 //add empty line to separate result
                    CurrentOcrResult.AddRange(textList);

                    //display update
                    ImageList.Add(await ImageProcessor.getImageSource(bitmapImage));
                    DisplayText();
                }
            }
        }


        private void CancelBatch_Click(object sender, RoutedEventArgs e) {
            CancelBatchProcess();
        }

        private void CancelBatchProcess() {
            if (TokenSource != null) {
                TokenSource.Cancel();
            }
        }





        //text process =====================================================================================================================================


        private void SaveFile_Click(object sender, RoutedEventArgs e) {
            TextProcessor.SaveTextFIle(CurrentProcessedItemName, TextField.Text);
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e) {
            TextProcessor.CopyTextToClipboard(TextField.Text);
        }

        private void WrapText_Click(object sender, RoutedEventArgs e) {
            ChangeTextWrapSetting();
            DisplayText();
        }

        private void ChangeTextWrapSetting() {
            string currentSelectedItem = (string)LocalSettings.Values["WrapText"];
            LocalSettings.Values["WrapText"] = TextProcessor.GetNextTextMode(currentSelectedItem);
        }
        private void DisplayText() {
            //if ocr result is not changed, skip
            if (PrevOcrResult.SequenceEqual(CurrentOcrResult)) {
                return;
            }
            PrevOcrResult = new List<String>(CurrentOcrResult.ToArray());
            string currentMode = LocalSettings.Values["WrapText"] as string;
            TextField.Text = TextProcessor.GetWrapText(CurrentOcrResult, currentMode);
        }



        //drag drop =====================================================================================================================================

        private void Grid_DragOver(object sender, DragEventArgs e) {
            e.AcceptedOperation = DataPackageOperation.Copy;
            // To display the data which is dragged    
            e.DragUIOverride.Caption = "drop here to view image";
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsCaptionVisible = true;
        }

        private async void Grid_Drop(object sender, DragEventArgs e) {
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Any()) {
                    var storageFile = items[0] as StorageFile;

                    if (new List<string>() { ".jpeg", ".jpg", ".png", ".gif", ".tiff", ".bmp" }.Contains(storageFile.FileType.ToLower())) {
                        ExplorerItem item = new ExplorerItem(storageFile);
                        CurrentProcessedItemName = item.Name;
                        CurrentBitmap = await item.GetBitmapImage();
                        ProcessImage(CurrentBitmap);
                    }
                }
            }
        }



        ///  canvas crop=======================================================



        private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e) {
            IsCropped = false;
            cropBoxCoordinates = null;
            CropBox.Visibility = Visibility.Collapsed;
            MyCanvas.Width = ((Image)sender).ActualWidth;
            MyCanvas.Height = ((Image)sender).ActualHeight;
        }

        private void Canvas_MouseEntered(object sender, PointerRoutedEventArgs e) {
            Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Cross, 0);
        }
        private void Canvas_MouseDown(object sender, PointerRoutedEventArgs e) {
            (int X, int Y, bool IsLeftButtonPressed) = GetPointerStatus(sender, e);

            if (IsLeftButtonPressed) {
                UpdateCropBox(X, Y, true);
            }
            e.Handled = true;
        }
        private void Canvas_MouseMove(object sender, PointerRoutedEventArgs e) {
            (int X, int Y, bool IsLeftButtonPressed) = GetPointerStatus(sender, e);
            UpdateCropBox(X, Y);
            e.Handled = true;
        }

        private void Canvas_MouseUp(object sender, PointerRoutedEventArgs e) {
            (int X, int Y, bool IsLeftButtonPressed) = GetPointerStatus(sender, e);

            if (!IsLeftButtonPressed) {
                ProcessCanvas(X, Y);
            }
            // Prevent most handlers along the event route from handling the same event again.
            e.Handled = true;

        }

        private void Canvas_MouseExited(object sender, PointerRoutedEventArgs e) {
            (int X, int Y, bool IsLeftButtonPressed) = GetPointerStatus(sender, e);
            ProcessCanvas(X, Y);
            Window.Current.CoreWindow.PointerCursor = cursorBeforePointerEntered;
            e.Handled = true;
        }


        private void ProcessCanvas(int X, int Y) {
            if (IsCropped || cropBoxCoordinates == null) {
                return;
            }
            IsCropped = true;
            UpdateCropBox(X, Y);
            ProcessImage(CurrentBitmap, false);
        }


        private (int, int, bool) GetPointerStatus(object sender, PointerRoutedEventArgs e) {
            //return X, Y, IsLeftButtonPressed
            PointerPoint ptrPt = e.GetCurrentPoint((UIElement)sender);
            return ((int)ptrPt.Position.X, (int)ptrPt.Position.Y, ptrPt.Properties.IsLeftButtonPressed);
        }

        private void UpdateCropBox(int X, int Y, bool Reset = false) {
            if (Reset) {
                IsCropped = false;
                cropBoxCoordinates = new BoxCoordinates(X, Y, (int)MyCanvas.Width, (int)MyCanvas.Height);
            }
            if (IsCropped == true || cropBoxCoordinates == null) {
                return;
            }

            cropBoxCoordinates.UpdateCoordinates(X, Y);

            CropBox.Translation = new Vector3() {
                X = cropBoxCoordinates.X,
                Y = cropBoxCoordinates.Y,
                Z = 0
            };
            CropBox.Width = cropBoxCoordinates.W;
            CropBox.Height = cropBoxCoordinates.H;
            CropBox.Visibility = Visibility.Visible;
        }




        //Screen capture======================================================================================

        private async void Capture_Click(object sender, RoutedEventArgs e) {
            resetImageProcess();
            ShowCancelCaptureButton();
            await StartCaptureAsync();
        }

        private void CancelCapture_Click(object sender, RoutedEventArgs e) {
            ShowCaptureButton();
            StopCapture();
        }

        private void ShowCaptureButton() {
            Capture.Visibility = Visibility.Visible;
            CancelCapture.Visibility = Visibility.Collapsed;
        }
        private void ShowCancelCaptureButton() {
            Capture.Visibility = Visibility.Collapsed;
            CancelCapture.Visibility = Visibility.Visible;
        }

        private void SetupCapture() {
            if (!GraphicsCaptureSession.IsSupported()) {
                // Hide the capture UI if screen capture is not supported.
                Capture.Visibility = Visibility.Collapsed;
            }

            _canvasDevice = new CanvasDevice();

            _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
                Window.Current.Compositor,
                _canvasDevice);

            _compositor = Window.Current.Compositor;

            _surface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(400, 400),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);    // This is the only value that currently works with
                                                    // the composition APIs.


            var visual = _compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            var brush = _compositor.CreateSurfaceBrush(_surface);
            brush.HorizontalAlignmentRatio = 0.5f;
            brush.VerticalAlignmentRatio = 0.5f;
            brush.Stretch = CompositionStretch.Uniform;
            visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(CaptureBox, visual);
        }

        public async Task StartCaptureAsync() {
            // The GraphicsCapturePicker follows the same pattern the
            // file pickers do.
            var picker = new GraphicsCapturePicker();
            GraphicsCaptureItem item = await picker.PickSingleItemAsync();

            // The item may be null if the user dismissed the
            // control without making a selection or hit Cancel.
            if (item != null) {
                resetImageProcess();
                showCaptureBox();
                StartCaptureInternal(item);
            } else {
                ShowCaptureButton();
            }
        }



        private void StartCaptureInternal(GraphicsCaptureItem item) {
            // Stop the previous capture if we had one.
            StopCapture();

            _item = item;
            _lastSize = _item.Size;


            _framePool = Direct3D11CaptureFramePool.Create(
               _canvasDevice, // D3D device
               DirectXPixelFormat.B8G8R8A8UIntNormalized, // Pixel format
               2, // Number of frames
               _item.Size); // Size of the buffers

            _framePool.FrameArrived += (s, a) => {
                using (var frame = _framePool.TryGetNextFrame()) {
                    ProcessFrame(frame);
                }

            };

            _item.Closed += (s, a) => {
                StopCapture();
            };

            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
        }

        public void StopCapture() {
            _session?.Dispose();
            _framePool?.Dispose();
            _item = null;
            _session = null;
            _framePool = null;

        }

        private void ProcessFrame(Direct3D11CaptureFrame frame) {
            // Resize and device-lost leverage the same function on the
            // Direct3D11CaptureFramePool. Refactoring it this way avoids
            // throwing in the catch block below (device creation could always
            // fail) along with ensuring that resize completes successfully and
            // isn’t vulnerable to device-lost.
            bool needsReset = false;
            bool recreateDevice = false;

            if ((frame.ContentSize.Width != _lastSize.Width) ||
                (frame.ContentSize.Height != _lastSize.Height)) {
                needsReset = true;
                _lastSize = frame.ContentSize;
            }

            try {
                // Take the D3D11 surface and draw it into a  
                // Composition surface.

                // Convert our D3D11 surface into a Win2D object.
                CanvasBitmap canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                    _canvasDevice,
                    frame.Surface);

                _currentFrame = canvasBitmap;

                // Helper that handles the drawing for us.
                FillSurfaceWithBitmap(canvasBitmap);
                OcrOnCapturedImage(canvasBitmap);
            }


            // This is the device-lost convention for Win2D.
            catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult)) {
                // We lost our graphics device. Recreate it and reset
                // our Direct3D11CaptureFramePool.  
                needsReset = true;
                recreateDevice = true;
            }

            if (needsReset) {
                ResetFramePool(frame.ContentSize, recreateDevice);
            }
        }


        private void FillSurfaceWithBitmap(CanvasBitmap canvasBitmap) {
            CanvasComposition.Resize(_surface, canvasBitmap.Size);
            using (var session = CanvasComposition.CreateDrawingSession(_surface)) {
                session.Clear(Colors.Transparent);
                session.DrawImage(canvasBitmap);
            }
        }


        private async void OcrOnCapturedImage(CanvasBitmap canvasBitmap) {
            //run ones every 0.7 second, stop if textEdit is focused
            if (DateTime.Now.Subtract(_lastOcrRuntime).TotalSeconds > 0.7 && !textFieldFocused) {
                _lastOcrRuntime = DateTime.Now;
                SoftwareBitmap softwareBitmapImg = await SoftwareBitmap.CreateCopyFromSurfaceAsync(canvasBitmap, BitmapAlphaMode.Premultiplied);
                CurrentBitmap = softwareBitmapImg;
                DisplayImage(softwareBitmapImg);
                ProcessImage(softwareBitmapImg, false);
            }
        }



        private void ResetFramePool(SizeInt32 size, bool recreateDevice) {
            do {
                try {
                    if (recreateDevice) {
                        _canvasDevice = new CanvasDevice();
                    }

                    _framePool.Recreate(
                        _canvasDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        size);
                }
                // This is the device-lost convention for Win2D.
                catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult)) {
                    _canvasDevice = null;
                    recreateDevice = true;
                }
            } while (_canvasDevice == null);
        }


        private void TextField_GettingFocus(UIElement sender, GettingFocusEventArgs args) {
            textFieldFocused = true;
        }

        private void TextField_LosingFocus(UIElement sender, LosingFocusEventArgs args) {
            textFieldFocused = false;
        }

    }



    public static class SettingHandler {
        public static Dictionary<string, string> DefaultSetting = new Dictionary<string, string>(){
            {"Language", ""},
            {"WrapText", "newLine"},
            {"IsTooltipShowed", "false"},
            {"RecentAccessFolderToken", ""}
        };

        public static ApplicationDataContainer GetSetting() {
            var localSettings = ApplicationData.Current.LocalSettings;
            //if no setting, set default value
            foreach (var item in DefaultSetting) {
                if (localSettings.Values[item.Key] == null) {
                    localSettings.Values[item.Key] = item.Value;
                }
            }

            return localSettings;
        }
    }

    public static class TextProcessor {

        public static List<string> wrapTextModeList = new List<string>(){
            "newLine","space","sentence"
        };

        public static String GetNextTextMode(String currentSelectedItem) {
            int selectedIndex = (wrapTextModeList.IndexOf(currentSelectedItem) + 1) % wrapTextModeList.Count;
            string selectedName = wrapTextModeList[selectedIndex];
            return selectedName;
        }

        public static String GetWrapText(List<string> currentOcrResult, string currentMode) {
            string resultText = "";
            if (currentMode == "newLine") {
                resultText = String.Join("\n", currentOcrResult.ToArray());
            } else if (currentMode == "space") {
                resultText = String.Join(" ", currentOcrResult.ToArray());
            } else if (currentMode == "sentence")  //split by sentence
              {
                resultText = String.Join(" ", currentOcrResult.ToArray());
                string[] sentences = Regex.Split(resultText, @"(?<=[\.!\?])\s+");
                resultText = String.Join("\n", sentences);
            }
            return resultText;
        }

        public static async void SaveTextFIle(String fileName, String text) {
            var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".txt" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(fileName); //get current item name without extension


            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null) {
                // Prevent updates to the remote version of the file until
                // we finish making changes and call CompleteUpdatesAsync.
                CachedFileManager.DeferUpdates(file);

                // write to file
                await FileIO.WriteTextAsync(file, text);
            }
        }

        public static void CopyTextToClipboard(String text) {
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
        }


    }


    public static class ImageProcessor {

        public static async Task<SoftwareBitmap> LoadImage(StorageFile file, uint maxSize = 2000) {
            try {
                using (var stream = await file.OpenAsync(FileAccessMode.Read)) {
                    SoftwareBitmap softwareBitmap;
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                    ImageProperties imageProperties = await file.Properties.GetImagePropertiesAsync();


                    if (imageProperties.Width < maxSize && imageProperties.Height < maxSize) {
                        softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    } else {
                        softwareBitmap = await GetResizedImage(decoder, imageProperties.Width, imageProperties.Height, maxSize);
                    }
                    return softwareBitmap;
                }
            } catch (Exception) {
                Debug.WriteLine("Load Image Fail");
                return null;
            }
        }

        public static async Task<SoftwareBitmap> GetResizedImage(BitmapDecoder decoder, uint width, uint height, uint maxSize) {
            float ratio = (float)width / height;
            if (width > maxSize) {
                width = maxSize;
                height = (uint)(maxSize / ratio);
            }
            if (height > maxSize) {
                width = (uint)(ratio * maxSize);
                height = maxSize;
            }

            var transform = new BitmapTransform() {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        }

        public static async Task<SoftwareBitmap> GetCroppedImage(SoftwareBitmap softwareBitmap, uint x, uint y, uint w, uint h) {

            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream()) {
                w = (uint)Math.Min(w, softwareBitmap.PixelWidth - x);
                h = (uint)Math.Min(h, softwareBitmap.PixelHeight - y);

                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
                encoder.SetSoftwareBitmap(softwareBitmap);
                encoder.BitmapTransform.Bounds = new BitmapBounds() {
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                };

                await encoder.FlushAsync();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                return await decoder.GetSoftwareBitmapAsync(softwareBitmap.BitmapPixelFormat, softwareBitmap.BitmapAlphaMode);
            }
        }

        public async static Task<SoftwareBitmap> SoftwareBitmapRotate(SoftwareBitmap softwarebitmap) {
            if (softwarebitmap == null) {
                return null;
            }
            //https://github.com/kiwamu25/BarcodeScanner/blob/f5359693019ea957813b364b456bba571f881060/BarcodeScanner/BarcodeScanner/MainPage.xaml.cs
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream()) {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
                encoder.SetSoftwareBitmap(softwarebitmap);
                encoder.BitmapTransform.Rotation = BitmapRotation.Clockwise90Degrees;
                await encoder.FlushAsync();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                return await decoder.GetSoftwareBitmapAsync(softwarebitmap.BitmapPixelFormat, BitmapAlphaMode.Premultiplied);
            }
        }



        public async static Task<SoftwareBitmapSource> getImageSource(SoftwareBitmap softwareBitmap) {
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }


    }


    public static class OcrProcessor {
        public static IReadOnlyList<Language> GetOcrLangList() {
            return OcrEngine.AvailableRecognizerLanguages;
        }
        public static async Task<List<string>> GetText(SoftwareBitmap imageItem, Language SelectedLang) {
            //check item is null
            //check no selectedLang
            //check image is too large
            if (imageItem == null ||
                SelectedLang == null ||
                imageItem.PixelWidth > OcrEngine.MaxImageDimension ||
                imageItem.PixelHeight > OcrEngine.MaxImageDimension
                ) {
                return new List<string> { "" };
            }


            //check ocr image exist
            var ocrEngine = OcrEngine.TryCreateFromLanguage(SelectedLang);
            if (ocrEngine == null) {
                return new List<string> { "" };
            }


            var ocrResult = await ocrEngine.RecognizeAsync(imageItem);


            List<string> textList = new List<string>() { };
            foreach (var line in ocrResult.Lines) {
                textList.Add(line.Text);
            }
            return textList;
        }

    }



    public class ExplorerItem {

        public ExplorerItem(IStorageItem item) {
            string label = (item is StorageFile) ? "file" : "folder";

            if (label == "file" && ".pdf".Contains(((StorageFile)item).FileType.ToLower())) {
                label = "pdf";
            }
            SetExplorerListItem(item, item.Name, label);
        }
        public ExplorerItem(PdfPage item, String name) {

            SetExplorerListItem(item, name, "pdfPage");
        }

        public void SetExplorerListItem(object data, string name, string label) {
            Data = data;
            Name = name;
            Label = label;


            if (label == "file") {
                ItemSymbol = "\uEB9F";
            } else if (label == "folder") {
                ItemSymbol = "\uED43";
            } else if (label == "pdf") {
                ItemSymbol = "\uEA90";
            } else if (label == "pdfPage") {
                ItemSymbol = "\uE7C3";
            }
        }

        public object Data { get; set; }
        public string Name { get; set; }
        public string Label { get; set; }
        public string ItemSymbol { get; set; }

        public override string ToString() => Name;



        public async Task<List<ExplorerItem>> GetChildItemList() {
            if (Label == "folder") {
                return await GetFolderChildItem((StorageFolder)Data);
            } else if (Label == "pdf") {
                return await GetPdfChildItem((StorageFile)Data);
            }
            return null;
        }

        public async Task<List<ExplorerItem>> GetPdfChildItem(StorageFile file) {

            //https://blog.pieeatingninjas.be/2016/02/06/displaying-pdf-files-in-a-uwp-app/
            //https://pspdfkit.com/blog/2019/open-pdf-in-uwp/

            List<ExplorerItem> childList = new List<ExplorerItem>();
            PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);

            for (uint i = 0; i < doc.PageCount; i++) {
                PdfPage pdfPage = doc.GetPage(i);
                childList.Add(new ExplorerItem(pdfPage, pdfPage.Index.ToString() + "_" + file.Name));
            }
            return childList;
        }

        public async Task<List<ExplorerItem>> GetFolderChildItem(StorageFolder folder) {
            List<ExplorerItem> childList = new List<ExplorerItem>();

            foreach (IStorageItem item in await folder.GetItemsAsync()) {
                if (item is StorageFile) {
                    var fileItem = item as StorageFile;
                    //check isImage
                    if (new List<string>() { ".jpeg", ".jpg", ".png", ".gif", ".tiff", ".bmp", ".pdf" }.Contains(fileItem.FileType.ToLower())) {
                        childList.Add(new ExplorerItem(item));
                    }
                } else if (item is StorageFolder) {
                    childList.Add(new ExplorerItem(item));
                }
            }
            return childList;
        }


        public async Task<SoftwareBitmap> GetBitmapImage() {

            if (Label == "pdfPage") {
                return await LoadPdfPageImage((PdfPage)Data);
            } else if (Label == "file") {
                return await ImageProcessor.LoadImage((StorageFile)Data);
            }
            return null;
        }


        private async Task<SoftwareBitmap> LoadPdfPageImage(PdfPage page) {

            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream()) {
                await page.RenderToStreamAsync(stream);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                SoftwareBitmap bitmapItem = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                return bitmapItem;
            }
        }

        public static implicit operator string(ExplorerItem v) {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// crop box coordinate
    /// </summary>
    class BoxCoordinates {
        int _initialPointX, _initialPointY;
        int _maxWidth, _maxHeight;
        public int X, Y, W, H;

        public BoxCoordinates(int x, int y, int maxWidth, int maxHeight) {
            _initialPointX = x;
            _initialPointY = y;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            UpdateCoordinates(x, y);
        }

        public void UpdateCoordinates(int X, int Y) {
            X = Math.Max(Math.Min(X, _maxWidth), 0);
            Y = Math.Max(Math.Min(Y, _maxHeight), 0);

            this.X = Math.Min(_initialPointX, X);
            this.Y = Math.Min(_initialPointY, Y);

            W = Math.Max(_initialPointX, X) - this.X;
            H = Math.Max(_initialPointY, Y) - this.Y;

            W = Math.Min(W, _maxWidth - this.X);
            H = Math.Min(H, _maxHeight - this.Y);
        }

        public (uint, uint, uint, uint) GetRatioXYWH(double NewWidth, double NewHeight) {
            double ratioWidth = NewWidth / _maxWidth;
            double ratioHeight = NewHeight / _maxHeight;
            uint x = (uint)(X * ratioWidth);
            uint y = (uint)(Y * ratioHeight);
            uint w = (uint)(W * ratioWidth);
            uint h = (uint)(H * ratioHeight);
            return (x, y, w, h);
        }

    }

}
