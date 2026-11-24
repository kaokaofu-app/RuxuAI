using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace Jiyue
{
    internal static class ImageImport
    {
        static readonly ConditionalWeakTable<Dialog,Action<string,byte[]>> handlers=new ConditionalWeakTable<Dialog,Action<string,byte[]>>();
        const long MaxPixels=24000000,MaxBytes=32L*1024*1024;

        internal static void Bind(Dialog window,Action<string,byte[]> receive)
        {
            handlers.Add(window,receive);
            window.AllowDrop=true;
            window.DragEnter+=delegate(object sender,DragEventArgs e) {
                e.Effect=Available(window) && FilePath(e.Data)!=null ? e.AllowedEffect&DragDropEffects.Copy : DragDropEffects.None;
            };
            window.DragDrop+=async delegate(object sender,DragEventArgs e) {
                string path=FilePath(e.Data);
                if(path==null || !Available(window)) return;
                try {
                    var png=await Task.Run(()=>ReadFile(path));
                    if(Available(window)) receive(Path.GetFileName(path),png);
                } catch(Exception error) { Report(window,error); }
            };
        }
        static bool Available(Dialog window)
        {
            if(window.IsDisposed || window.Disposing) return false;
            foreach(Control child in window.Controls) if(child is SettingsDialog && child.Visible) return false;
            return true;
        }
        static string FilePath(IDataObject data)
        {
            var paths=data==null?null:data.GetData(DataFormats.FileDrop) as string[];
            if(paths==null || paths.Length!=1) return null;
            switch(Path.GetExtension(paths[0]).ToLowerInvariant()) {
                case ".png":case ".jpg":case ".jpeg":case ".bmp":case ".gif":case ".tif":case ".tiff":return paths[0];
                default:return null;
            }
        }
        static void CheckSize(int width,int height)
        {
            if(width<=0 || height<=0 || (long)width*height>MaxPixels)
                throw new InvalidDataException("图片尺寸过大，请裁剪后再试（最多 2400 万像素）。");
        }
        internal static byte[] ReadFile(string path)
        {
            using(var input=File.OpenRead(path)) {
                if(input.Length>MaxBytes) throw new InvalidDataException("请选择小于 32 MB 的图片。");
                var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                var frame=decoder.Frames[0];CheckSize(frame.PixelWidth,frame.PixelHeight);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(frame);
                using(var output=new MemoryStream()) {encoder.Save(output);return output.ToArray();}
            }
        }
        internal static bool TryPaste(Dialog window)
        {
            Action<string,byte[]> receive;
            if(!handlers.TryGetValue(window,out receive) || !Available(window)) return false;
            try {
                if(!Clipboard.ContainsImage()) return false;
                using(var picture=Clipboard.GetImage()) {
                    if(picture==null) return false;
                    CheckSize(picture.Width,picture.Height);
                    EncodePaste(window,new Bitmap(picture),receive);
                }
            } catch(Exception e) { Report(window,e); }
            return true;
        }
        static async void EncodePaste(Dialog window,Bitmap bitmap,Action<string,byte[]> receive)
        {
            try {
                byte[] png=await Task.Run(delegate {
                    using(bitmap) using(var output=new MemoryStream()) {bitmap.Save(output,ImageFormat.Png);return output.ToArray();}
                });
                if(Available(window)) receive("剪贴板截图",png);
            } catch(Exception e) { Report(window,e); }
        }
        static void Report(Dialog window,Exception error)
        {
            if(!window.IsDisposed) Toast.Show("图片读取失败。"+error.Message,ToastKind.Error,window);
        }
    }
}
