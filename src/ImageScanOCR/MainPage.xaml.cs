using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using FileAttributes = Windows.Storage.FileAttributes;
using Windows.Storage.FileProperties;

namespace ImageScanOCR
{
    /// <summary>
    /// OCR main page
    /// </summary>
    public sealed partial class MainPage : Page
    {
        StorageFolder baseFolder = null;
        List<IStorageItem> Items;
        ObservableCollection<object> Breadcrumbs = new ObservableCollection<object>();
        ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
        ObservableCollection<Language> languageList = new ObservableCollection<Language>();
        Language selectedLang = null;
        SoftwareBitmap _bitmap = null;


        public MainPage()
        {
            this.InitializeComponent();
            InitFolderViewList();
            initLanguageList();
        }



        private void initLanguageList()
        {
            //get language list
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                languageList.Add(lang);
            }

            //select langauge list 
            String settingLang = localSettings.Values["lang"] as string;
            if (settingLang == null)
            {
                LangCombo.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < languageList.Count; i++)
                {
                    if (languageList[i].LanguageTag == settingLang)
                    {
                        LangCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }










        private void InitFolderViewList()
        {
            if (baseFolder == null)
            {
                // Start with Pictures, videos and Music libraries.
                Items = new List<IStorageItem>();
                Items.Add(KnownFolders.PicturesLibrary);
                Items.Add(KnownFolders.MusicLibrary);
                Items.Add(KnownFolders.VideosLibrary);


                FolderListView.ItemsSource = Items;

                Breadcrumbs.Clear();
                Breadcrumbs.Add(new Crumb("Home", null));
            }
            else
            {
                GetFolderItems(baseFolder);
                Breadcrumbs.Clear();
                Breadcrumbs.Add(new Crumb(baseFolder.Name, null));
            }
        }



        private async void FolderBreadcrumbBar_ItemClicked(muxc.BreadcrumbBar sender, muxc.BreadcrumbBarItemClickedEventArgs args)
        {
            // Don't process last index (current location)
            if (args.Index < Breadcrumbs.Count - 1)
            {
                // Home is special case.
                if (args.Index == 0)
                {
                    InitFolderViewList();
                }
                // Go back to the clicked item.
                else
                {
                    var crumb = (Crumb)args.Item;
                    await GetFolderItems((StorageFolder)crumb.Data);

                    // Remove breadcrumbs at the end until 
                    // you get to the one that was clicked.
                    while (Breadcrumbs.Count > args.Index + 1)
                    {
                        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                    }
                }
            }
        }

        private async void FolderListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            // Ignore if a file is clicked.
            // If a folder is clicked, drill down into it.
            if (e.ClickedItem is StorageFolder)
            {
                StorageFolder folder = e.ClickedItem as StorageFolder;
                await GetFolderItems(folder);
                Breadcrumbs.Add(new Crumb(folder.DisplayName, folder));
            }
            else if (e.ClickedItem is StorageFile)
            {
                Debug.WriteLine("fffie");
                StorageFile file = e.ClickedItem as StorageFile;
                openFile(file);

            }
        }

        private async Task GetFolderItems(StorageFolder folder)
        {
            //get subfolder and only image files from given folder

            List<IStorageItem> currentPathItemList = new List<IStorageItem>();
            foreach (IStorageItem item in await folder.GetItemsAsync())
            {
                if (item is StorageFile)
                {
                    var fileItem = item as StorageFile;
                    //check isImage
                    if (new List<string>() { ".jpeg", ".jpg", ".png", ".gif", ".tiff", ".bmp" }.Contains(fileItem.FileType.ToLower()))
                    {
                        currentPathItemList.Add(item);
                    }
                }
                else
                {
                    currentPathItemList.Add(item);
                }
            }

            FolderListView.ItemsSource = currentPathItemList;
        }




        public async void openFile(StorageFile file)
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
                _bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);


                //diplay image
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(_bitmap);
                PreviewImage.Source = source;

                doOcrOnImage();
            }
        }


        private async void doOcrOnImage()
        {

            //check image is too large
            if (_bitmap.PixelWidth > OcrEngine.MaxImageDimension || _bitmap.PixelHeight > OcrEngine.MaxImageDimension)
            {
                return;
            }

            if (selectedLang == null)
            {
                return;
            }

            //check ocr image exist
            var ocrEngine = OcrEngine.TryCreateFromLanguage(selectedLang);
            if (ocrEngine == null)
            {
                return;
            }


            var ocrResult = await ocrEngine.RecognizeAsync(_bitmap);
            string concatText = "";
            foreach (var line in ocrResult.Lines)
            {
                concatText += line.Text + "\n";
            }

            TextField.Text = concatText;
        }














        private async void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Plain Text", new List<string>() { ".txt" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = "untitled";

            Windows.Storage.StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                // Prevent updates to the remote version of the file until
                // we finish making changes and call CompleteUpdatesAsync.
                Windows.Storage.CachedFileManager.DeferUpdates(file);

                // write to file
                await Windows.Storage.FileIO.WriteTextAsync(file, TextField.Text);

                // Let Windows know that we're finished changing the file so
                // the other app can update the remote version of the file.
                // Completing updates may require Windows to ask for user input.
                Windows.Storage.Provider.FileUpdateStatus status = await Windows.Storage.CachedFileManager.CompleteUpdatesAsync(file);
                if (status == Windows.Storage.Provider.FileUpdateStatus.Complete)
                {
                    // saved
                }
                else
                {
                    //couldn't be saved
                }
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(TextField.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
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
                Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.AddOrReplace("PickedFolderToken", pickedFolder);

                baseFolder = pickedFolder;
                InitFolderViewList();
            }
        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedLang = e.AddedItems[0] as Language;
            localSettings.Values["lang"] = selectedLang.LanguageTag;
        }
    }
    public readonly struct Crumb
    {
        public Crumb(String label, object data)
        {
            Label = label;
            Data = data;
        }
        public string Label { get; }
        public object Data { get; }
        public override string ToString() => Label;
    }


    public class FileSymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            FileAttributes attributes = (FileAttributes)value;

            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                return "Folder";
            return "Page2";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
