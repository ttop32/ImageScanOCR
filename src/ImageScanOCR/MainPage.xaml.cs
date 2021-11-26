using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.Data.Pdf;
using System.Threading;
using Windows.ApplicationModel.Core;
using Windows.Storage.AccessCache;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using Windows.UI.ViewManagement;
using Windows.UI;



namespace ImageScanOCR
{
    /// <summary>
    /// OCR main page
    /// explorer image from left side panel
    /// select image to display image and process ocr to display text in right panel
    /// </summary>
    public sealed partial class MainPage : Page
    {
        ExplorerItem BaseFolder = null;
        ObservableCollection<object> Breadcrumbs = new ObservableCollection<object>();
        ObservableCollection<ExplorerItem> ExplorerList = new ObservableCollection<ExplorerItem>();

        ObservableCollection<Language> LanguageList = new ObservableCollection<Language>();
        Language SelectedLang = null;

        SoftwareBitmap CurrentBitmap = null;
        List<string> CurrentOcrResult = new List<string>() { };

        ApplicationDataContainer LocalSettings = ApplicationData.Current.LocalSettings;
        Dictionary<string, string> DefaultSetting = new Dictionary<string, string>(){
            {"Language", ""},
            {"WrapText", "newLine"},
            {"IsTooltipShowed", "false"},
            {"RecentAccessFolderToken", ""}
        };
        CancellationTokenSource TokenSource = new CancellationTokenSource();
        string CurrentProcessedItemName = "New Document";



        public MainPage()
        {
            this.InitializeComponent();
            InitTitleBar();
            InitSetting();
            InitFolderViewList();
            InitLanguageList();
            InitTooltip();
        }





        private void InitTitleBar()
        {
            var titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            // Hide default title bar.
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;

            // Set XAML element as a draggable region.
            //Window.Current.SetTitleBar(AppTitleBar);
        }



        private void InitSetting()
        {
            //if no setting, set default value
            foreach (var item in DefaultSetting)
            {
                if (LocalSettings.Values[item.Key] == null)
                {
                    LocalSettings.Values[item.Key] = item.Value;
                }
            }
        }

        private void InitTooltip()
        {
            String isTooltipShowed = LocalSettings.Values["IsTooltipShowed"] as string;
            if (isTooltipShowed == "false")
            {
                MoreLanguageTip.IsOpen = true;
                LocalSettings.Values["IsTooltipShowed"] = "true";
            }
        }






        //language setting============================================================================================================================
        private void InitLanguageList()
        {
            //get language list
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                LanguageList.Add(lang);
            }
            SelectLanguageFromSetting();


        }
        private void SelectLanguageFromSetting()
        {
            String settingLang = LocalSettings.Values["Language"] as string;

            //set matched lang as selected item
            //if no saved lang use first item
            for (int i = 0; i < LanguageList.Count; i++)
            {
                if (LanguageList[i].LanguageTag == settingLang || settingLang == "")
                {
                    LangCombo.SelectedIndex = i;
                    break;
                }
            }

        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //save language when changed 
            SelectedLang = e.AddedItems[0] as Language;
            LocalSettings.Values["Language"] = SelectedLang.LanguageTag;
            ProcessImage(CurrentBitmap);
        }












        //folder explorer============================================================================================================================
        private async void InitFolderViewList()
        {
            //open recent picked folder 
            string token = (string)LocalSettings.Values["RecentAccessFolderToken"];
            if (token != "" && StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
            {
                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    BaseFolder = new ExplorerItem(folder);
                    UpdateExplorerListView(BaseFolder);
                    Breadcrumbs.Clear();
                    Breadcrumbs.Add(new Crumb(BaseFolder.Name, null));
                    return;
                }
                catch (FileNotFoundException) { }
            }

            // if no recent picked folder 
            // Start with Pictures, Videos and Music libraries.
            ExplorerList.Clear();
            ExplorerList.Add(new ExplorerItem(KnownFolders.PicturesLibrary));
            ExplorerList.Add(new ExplorerItem(KnownFolders.MusicLibrary));
            ExplorerList.Add(new ExplorerItem(KnownFolders.VideosLibrary));
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new Crumb("Home", null));
        }



        private void FolderBreadcrumbBar_ItemClicked(muxc.BreadcrumbBar sender, muxc.BreadcrumbBarItemClickedEventArgs clickedItem)
        {
            // Don't process last index (current location),  right most one
            if (clickedItem.Index < Breadcrumbs.Count - 1)
            {
                // Home is special case, skip   left most one,
                if (clickedItem.Index == 0)
                {
                    InitFolderViewList();
                }
                // Go back to the clicked item.
                else
                {
                    var crumb = (Crumb)clickedItem.Item;
                    //await GetFolderItems((StorageFolder)crumb.Data);
                    UpdateExplorerListView((ExplorerItem)crumb.Data);

                    // Remove breadcrumbs at the end until 
                    // you get to the one that was clicked.
                    while (Breadcrumbs.Count > clickedItem.Index + 1)
                    {
                        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                    }
                }
            }
        }

        private async void FolderListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedItem = e.ClickedItem as ExplorerItem;
            if (clickedItem.Label == "folder" || clickedItem.Label == "pdf")
            {
                UpdateExplorerListView(clickedItem);
                Breadcrumbs.Add(new Crumb(clickedItem.Name, clickedItem));
            }
            else if (clickedItem.Label == "file" || clickedItem.Label == "pdfPage")
            {
                CurrentProcessedItemName = clickedItem.Name;
                CurrentBitmap = await clickedItem.GetBitmapImage();
                ProcessImage(CurrentBitmap);
            }
        }


        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder pickedFolder = await folderPicker.PickSingleFolderAsync();
            if (pickedFolder != null)
            {
                // Folder was picked you can now use it
                var token = StorageApplicationPermissions.FutureAccessList.Add(pickedFolder);
                LocalSettings.Values["RecentAccessFolderToken"] = token;
                BaseFolder = new ExplorerItem(pickedFolder);
                InitFolderViewList();
            }
        }

        private async void UpdateExplorerListView(ExplorerItem item)
        {
            List<ExplorerItem> childItemList = await item.GetChildItemList();
            ExplorerList.Clear();
            childItemList.ForEach(ExplorerList.Add);
        }







        //image process=============================================================================================================================================
        private async void Rotate_Click(object sender, RoutedEventArgs e)
        {
            CurrentBitmap = await SoftwareBitmapRotate(CurrentBitmap);
            ProcessImage(CurrentBitmap);
        }

        public async void ProcessImage(SoftwareBitmap imageItem)
        {
            CancelBatchProcess();    //cancel any running batch
            DisplayImage(imageItem);
            CurrentOcrResult = await DoOcrOnImage(imageItem);
            DisplayText();

        }

        public async void DisplayImage(SoftwareBitmap imageItem)
        {
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(imageItem);
            PreviewImage.Source = source;
        }

        public async static Task<SoftwareBitmap> SoftwareBitmapRotate(SoftwareBitmap softwarebitmap)
        {
            if (softwarebitmap == null)
            {
                return null;
            }
            //https://github.com/kiwamu25/BarcodeScanner/blob/f5359693019ea957813b364b456bba571f881060/BarcodeScanner/BarcodeScanner/MainPage.xaml.cs
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
                encoder.SetSoftwareBitmap(softwarebitmap);
                encoder.BitmapTransform.Rotation = BitmapRotation.Clockwise90Degrees;
                await encoder.FlushAsync();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                return await decoder.GetSoftwareBitmapAsync(softwarebitmap.BitmapPixelFormat, BitmapAlphaMode.Premultiplied);
            }
        }






        //OCR============================================================================================================================
        private async Task<List<string>> DoOcrOnImage(SoftwareBitmap imageItem)
        {
            //check item is null
            //check no selectedLang
            //check image is too large
            if (imageItem == null ||
                SelectedLang == null ||
                imageItem.PixelWidth > OcrEngine.MaxImageDimension ||
                imageItem.PixelHeight > OcrEngine.MaxImageDimension
                )
            {
                return new List<string> { "" };
            }


            //check ocr image exist
            var ocrEngine = OcrEngine.TryCreateFromLanguage(SelectedLang);
            if (ocrEngine == null)
            {
                return new List<string> { "" };
            }


            var ocrResult = await ocrEngine.RecognizeAsync(imageItem);


            List<string> textList = new List<string>() { };
            foreach (var line in ocrResult.Lines)
            {
                textList.Add(line.Text);
            }
            return textList;
        }


        //Batch  ============================================================================================================================

        private async void BatchProcess_Click(object sender, RoutedEventArgs e)
        {
            BatchProcess.Visibility = Visibility.Collapsed;
            CancelBatch.Visibility = Visibility.Visible;
            ProgressItem.IsActive = true;

            await ProcessBatch();

            DisplayText();
            ProgressItem.IsActive = false;
            BatchProcess.Visibility = Visibility.Visible;
            CancelBatch.Visibility = Visibility.Collapsed;
        }


        private async Task ProcessBatch()
        {
            //handle try catch for cancel batch interruption
            //iterate current folder list and process ocr
            TokenSource = new CancellationTokenSource();
            var token = TokenSource.Token;
            try
            {
                await Task.Run(async () =>
                {
                    CurrentOcrResult.Clear();
                    foreach (var item in ExplorerList)
                    {
                        if (item.Label == "pdfPage" || item.Label == "file")
                        {
                            CurrentProcessedItemName = ((Crumb)Breadcrumbs.Last()).Label;
                            SoftwareBitmap bitmapImage = await item.GetBitmapImage();
                            var textList = await DoOcrOnImage(bitmapImage);
                            textList.Add("");                                 //add empty line to separate result
                            token.ThrowIfCancellationRequested();            //check cancel batch 
                            CurrentOcrResult.AddRange(textList);
                        }
                    }
                }, token);

            }
            catch (OperationCanceledException) { }
        }


        private void CancelBatch_Click(object sender, RoutedEventArgs e)
        {
            CancelBatchProcess();
        }

        private void CancelBatchProcess()
        {
            if (TokenSource != null)
            {
                TokenSource.Cancel();
            }
        }








        //text process =====================================================================================================================================


        private async void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".txt" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = Path.GetFileNameWithoutExtension(CurrentProcessedItemName); //get current item name without extension


            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                // Prevent updates to the remote version of the file until
                // we finish making changes and call CompleteUpdatesAsync.
                CachedFileManager.DeferUpdates(file);

                // write to file
                await FileIO.WriteTextAsync(file, TextField.Text);
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(TextField.Text);
            Clipboard.SetContent(dataPackage);
        }


        private void WrapText_Click(object sender, RoutedEventArgs e)
        {
            ChangeTextWrapSetting();
            DisplayText();
        }

        private void ChangeTextWrapSetting()
        {
            //select item, rotate
            List<string> wrapTextModeList = new List<string>(){
                "newLine","space","sentence"
            };

            string currentSelectedItem = (string)LocalSettings.Values["WrapText"];
            int selectedIndex = (wrapTextModeList.IndexOf(currentSelectedItem) + 1) % wrapTextModeList.Count;
            string selectedName = wrapTextModeList[selectedIndex];
            LocalSettings.Values["WrapText"] = selectedName;

        }
        private void DisplayText()
        {
            string currentMode = LocalSettings.Values["WrapText"] as string;
            string resultText = "";
            if (currentMode == "newLine")
            {
                resultText = String.Join("\n", CurrentOcrResult.ToArray());
            }
            else if (currentMode == "space")
            {
                resultText = String.Join(" ", CurrentOcrResult.ToArray());
            }
            else if (currentMode == "sentence")  //split by sentence
            {
                resultText = String.Join(" ", CurrentOcrResult.ToArray());
                string[] sentences = Regex.Split(resultText, @"(?<=[\.!\?])\s+");
                resultText = String.Join("\n", sentences);
            }
            TextField.Text = resultText;
        }







    }
    public readonly struct Crumb
    {
        public Crumb(string label, object data)
        {
            Label = label;
            Data = data;
        }
        public string Label { get; }
        public object Data { get; }
        public override string ToString() => Label;
    }



    public class ExplorerItem
    {

        public ExplorerItem(IStorageItem item)
        {
            string label = (item is StorageFile) ? "file" : "folder";

            if (label == "file" && ".pdf".Contains(((StorageFile)item).FileType.ToLower()))
            {
                label = "pdf";
            }
            SetExplorerListItem(item, item.Name, label);
        }
        public ExplorerItem(PdfPage item, String name)
        {

            SetExplorerListItem(item, name, "pdfPage");
        }

        public void SetExplorerListItem(object data, string name, string label)
        {
            Data = data;
            Name = name;
            Label = label;


            if (label == "file")
            {
                ItemSymbol = "\uEB9F";
            }
            else if (label == "folder")
            {
                ItemSymbol = "\uED43";
            }
            else if (label == "pdf")
            {
                ItemSymbol = "\uEA90";
            }
            else if (label == "pdfPage")
            {
                ItemSymbol = "\uE7C3";
            }
        }

        public object Data { get; set; }
        public string Name { get; set; }
        public string Label { get; set; }
        public string ItemSymbol { get; set; }

        public override string ToString() => Name;






        public async Task<List<ExplorerItem>> GetChildItemList()
        {
            if (Label == "folder")
            {
                return await GetFolderChildItem((StorageFolder)Data);
            }
            else if (Label == "pdf")
            {
                return await GetPdfChildItem((StorageFile)Data);
            }
            return null;
        }

        public async Task<List<ExplorerItem>> GetPdfChildItem(StorageFile file)
        {

            //https://blog.pieeatingninjas.be/2016/02/06/displaying-pdf-files-in-a-uwp-app/
            //https://pspdfkit.com/blog/2019/open-pdf-in-uwp/

            List<ExplorerItem> childList = new List<ExplorerItem>();
            PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);

            for (uint i = 0; i < doc.PageCount; i++)
            {
                PdfPage pdfPage = doc.GetPage(i);
                childList.Add(new ExplorerItem(pdfPage, pdfPage.Index.ToString() + "_" + file.Name));
            }
            return childList;
        }

        public async Task<List<ExplorerItem>> GetFolderChildItem(StorageFolder folder)
        {
            List<ExplorerItem> childList = new List<ExplorerItem>();

            foreach (IStorageItem item in await folder.GetItemsAsync())
            {
                if (item is StorageFile)
                {
                    var fileItem = item as StorageFile;
                    //check isImage
                    if (new List<string>() { ".jpeg", ".jpg", ".png", ".gif", ".tiff", ".bmp", ".pdf" }.Contains(fileItem.FileType.ToLower()))
                    {
                        childList.Add(new ExplorerItem(item));
                    }
                }
                else
                {
                    childList.Add(new ExplorerItem(item));
                }
            }
            return childList;
        }







        public async Task<SoftwareBitmap> GetBitmapImage()
        {
            if (Label == "pdfPage")
            {
                return await LoadPdfPageImage((PdfPage)Data);
            }
            else if (Label == "file")
            {
                return await LoadImage((StorageFile)Data);
            }
            return null;
        }

        public async Task<SoftwareBitmap> LoadImage(StorageFile file)
        {
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                //calculate resize width height
                float maxSize = 2000;
                ImageProperties imageProperties = await file.Properties.GetImagePropertiesAsync();
                float width = Convert.ToSingle(imageProperties.Width);
                float height = Convert.ToSingle(imageProperties.Height);
                float ratio = width / height;
                if (width > maxSize)
                {
                    width = maxSize;
                    height = maxSize / ratio;
                }
                if (height > maxSize)
                {
                    width = ratio * maxSize;
                    height = maxSize;
                }

                //load image and resize
                var transform = new BitmapTransform() { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Cubic };
                var decoder = await BitmapDecoder.CreateAsync(stream);
                SoftwareBitmap bitmapItem = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                return bitmapItem;
            }
        }

        private async Task<SoftwareBitmap> LoadPdfPageImage(PdfPage page)
        {
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(stream);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                SoftwareBitmap bitmapItem = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                return bitmapItem;
            }
        }
    }

}
