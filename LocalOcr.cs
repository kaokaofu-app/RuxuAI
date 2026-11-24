using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Jiyue
{
    internal static class LocalOcr
    {
        public static async Task<string> ReadAsync(byte[] png, bool allowEmpty=false)
        {
            if (png == null || png.Length == 0)
                throw new ArgumentException("没有可识别的截图。");
            try
            {
                return await Task.Run(() => RecognizeAsync(png,allowEmpty)).ConfigureAwait(false);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("Windows 文字识别未能处理截图。请检查系统的语言选项是否已安装文字识别组件，或切换到视觉模型。", ex);
            }
        }

        private static async Task<string> RecognizeAsync(byte[] png,bool allowEmpty)
        {
            var languages = OcrEngine.AvailableRecognizerLanguages
                .OrderBy(language => Priority(language.LanguageTag)).ToArray();
            if (languages.Length == 0)
                throw new InvalidOperationException("Windows 未安装可用的文字识别语言。请在系统语言选项中安装中文或英文的 OCR 组件，或切换到视觉模型。");

            using (var source = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(png);
                    await WaitAsync<uint>(writer.StoreAsync()).ConfigureAwait(false);
                }
                var decoder = await WaitAsync(BitmapDecoder.CreateAsync(source)).ConfigureAwait(false);
                uint limit = OcrEngine.MaxImageDimension;
                var transform = new BitmapTransform();
                uint largest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                if (largest > limit)
                {
                    double scale = (double)limit / largest;
                    transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                    transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                }
                using (var image = await WaitAsync(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)).ConfigureAwait(false))
                {
                    foreach (Language language in languages)
                    {
                        var engine = OcrEngine.TryCreateFromLanguage(language);
                        if (engine == null) continue;
                        var result = await WaitAsync(engine.RecognizeAsync(image)).ConfigureAwait(false);
                        string text = String.Join(Environment.NewLine, result.Lines.Select(line => line.Text)).Trim();
                        if (text.Length > 0)
                            return Regex.Replace(text, @"(?<=[\u3400-\u9fff]) +(?=[\u3400-\u9fff])", "");
                    }
                }
            }
            if (allowEmpty) return "";
            throw new InvalidOperationException("截图里没有识别到文字。请放大原文后重新框选；公式和图表可以切换到视觉模型解读。");
        }

        private static int Priority(string tag)
        {
            if (tag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return 0;
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return 1;
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        // 系统自带的分卷 WinMD 直接提供异步完成回调，无需安装 Windows SDK。
        private static Task<T> WaitAsync<T>(IAsyncOperation<T> operation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (sender, status) =>
            {
                try { completion.TrySetResult(sender.GetResults()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            };
            return completion.Task;
        }
    }
}
