using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jiyue
{
    internal sealed class ScrollImageResult
    {
        internal byte[] Png;
        internal string Notice;
    }

    internal static class LongArticleImage
    {
        const int MaxHeight = 12000, MaxPixels = 14000000;
        internal const int MaxScreens = 8;
        const int TileHeight = 1500;
        const int Columns = 48;

        sealed class Sample
        {
            internal readonly byte[] Shades;
            internal readonly bool[] Detailed;
            internal readonly int Height;

            internal Sample(Bitmap image)
            {
                Height = image.Height;
                Shades = new byte[Height*Columns];
                Detailed = new bool[Height];
                var rectangle = new Rectangle(Point.Empty,image.Size);
                var bits = image.LockBits(rectangle,ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
                try {
                    byte[] row = new byte[image.Width*3];
                    for (int y=0;y<Height;y++) {
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y*bits.Stride),row,0,row.Length);
                        int min=255, max=0;
                        for (int k=0;k<Columns;k++) {
                            int x=((2*k+1)*image.Width/(2*Columns))*3;
                            byte value=(byte)((row[x]*29+row[x+1]*150+row[x+2]*77)>>8);
                            Shades[y*Columns+k]=value; min=Math.Min(min,value); max=Math.Max(max,value);
                        }
                        Detailed[y]=max-min>=22;
                    }
                } finally { image.UnlockBits(bits); }
            }
        }

        // Compare only rows with visible detail. Blank margins cannot establish a seam.
        static double Score(Sample prior,Sample next,int shift,int step)
        {
            int matched=0, observed=0, hits=0;
            int overlap=prior.Height-shift, edge=Math.Min(12,overlap/10);
            for (int y=edge;y<overlap-edge;y+=step) {
                if (!next.Detailed[y] && !prior.Detailed[y+shift]) continue;
                int difference=0, a=(y+shift)*Columns, b=y*Columns;
                for (int x=0;x<Columns;x++) difference+=Math.Min(40,Math.Abs(prior.Shades[a+x]-next.Shades[b+x]));
                observed++;
                if (difference<Columns*2) matched++;
                if (difference<Columns*5) hits++;
            }
            if (observed<Math.Max(3,Math.Min(14,overlap/(step*12)))) return -1;
            return (matched*0.7+hits*0.3)/observed;
        }

        static int Offset(Sample prior,Sample next)
        {
            // A stationary selection may be all white (e.g. a margin before article text).
            int changed=0, samples=0;
            for (int i=0;i<prior.Shades.Length;i+=3) {
                if (Math.Abs(prior.Shades[i]-next.Shades[i])>10) changed++;
                samples++;
            }
            if (changed*1000 <= samples) return 0;
            int max=prior.Height-Math.Max(30,prior.Height/6);
            int best=-1; double top=0,runner=0;
            for (int shift=0;shift<=max;shift++) {
                double score=Score(prior,next,shift,Math.Max(3,(prior.Height-shift)/40));
                if (score>=0.78) score=(score+Score(prior,next,shift,3))/2;
                if (score>top) {
                    if (best>=0 && shift-best>3) runner=Math.Max(runner,top);
                    best=shift; top=score;
                } else if (Math.Abs(best-shift)>3) runner=Math.Max(runner,score);
            }
            return top>=0.83 && top-runner>=0.045 ? best : -1;
        }

        static Bitmap Frame(IntPtr target,Rectangle area,Size savedSize)
        {
            if (!CaptureSource.CanReadSelection(target,area)) return null;
            using (var full=new Bitmap(area.Width,area.Height,PixelFormat.Format24bppRgb)) {
                using (var g=Graphics.FromImage(full)) g.CopyFromScreen(area.Location,Point.Empty,area.Size);
                if (!CaptureSource.CanReadSelection(target,area)) return null;
                if (savedSize==area.Size) return full.Clone(new Rectangle(Point.Empty,area.Size),PixelFormat.Format24bppRgb);
                var reduced=new Bitmap(savedSize.Width,savedSize.Height,PixelFormat.Format24bppRgb);
                using (var g=Graphics.FromImage(reduced)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full,new Rectangle(Point.Empty,savedSize));
                }
                return reduced;
            }
        }

        internal static ScrollImageResult Capture(IntPtr target,Rectangle area,CancellationToken cancel,
            Func<bool> finished, Action<int> progress)
        {
            if (area.Width<80 || area.Height<90) throw new ArgumentException("选区太小，请框住文章正文的宽度和至少几行内容。");
            // Match native vertical rows: scaling them before alignment shifts the sampling
            // grid after each wheel movement, particularly on high-DPI source windows.
            double scale=Math.Min(1,1500d/area.Width);
            var size=new Size(Math.Min(1500,area.Width),area.Height);
            int limit=Math.Min(Math.Min(MaxHeight,MaxPixels/size.Width),size.Height*MaxScreens);
            var strips=new List<Bitmap>();
            Sample previous=null;
            string notice=null;
            int height=0;
            var elapsed=Stopwatch.StartNew();
            long unmatchedAt=-1, lastChange=0;
            try {
                while (true) {
                    cancel.ThrowIfCancellationRequested();
                    using (Bitmap current=Frame(target,area,size)) {
                        if (current==null) throw new OperationCanceledException("来源窗口已切换或选区被遮挡，本次长截图已取消。");
                        var sampled=new Sample(current);
                        int delta=previous==null ? size.Height : Offset(previous,sampled);
                        bool finishNow=finished();
                        if (delta<0) {
                            if (unmatchedAt<0) unmatchedAt=elapsed.ElapsedMilliseconds;
                            if (finishNow || elapsed.ElapsedMilliseconds-unmatchedAt>1500) {
                                notice="两屏之间找不到可靠的重叠内容，只保留到上一次接好的位置。"; break;
                            }
                        } else {
                            unmatchedAt=-1;
                            if (delta>0) {
                                int add=Math.Min(delta,limit-height);
                                strips.Add(current.Clone(new Rectangle(0,size.Height-delta,size.Width,add),PixelFormat.Format24bppRgb));
                                height+=add; previous=sampled; lastChange=elapsed.ElapsedMilliseconds;
                                progress((int)Math.Ceiling((double)height/size.Height));
                                if (height>=limit) { notice="已到单次长截图上限，可解读这部分，再从此处继续截取。"; break; }
                            }
                            if (finishNow) break;
                        }
                        if (elapsed.ElapsedMilliseconds-lastChange>120000) {
                            notice="已暂停滚动两分钟，已保留这次采集的内容。"; break;
                        }
                    }
                    if (cancel.WaitHandle.WaitOne(90)) cancel.ThrowIfCancellationRequested();
                }
                cancel.ThrowIfCancellationRequested();
                int outputHeight=Math.Max(1,(int)Math.Round(height*scale));
                using (var result=new Bitmap(size.Width,outputHeight,PixelFormat.Format24bppRgb))
                using (var stream=new MemoryStream()) {
                    using (var g=Graphics.FromImage(result)) {
                        int y=0;
                        g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                        foreach (var strip in strips) {
                            int top=(int)Math.Round(y*scale);
                            y+=strip.Height;
                            if (scale==1) g.DrawImageUnscaled(strip,0,top);
                            else {
                                int bottom=(int)Math.Round(y*scale);
                                if (bottom>top) g.DrawImage(strip,new Rectangle(0,top,size.Width,bottom-top));
                            }
                        }
                    }
                    result.Save(stream,ImageFormat.Png);
                    return new ScrollImageResult {Png=stream.ToArray(), Notice=notice};
                }
            } finally { foreach (var strip in strips) strip.Dispose(); }
        }

        public static IEnumerable<byte[]> SplitForVision(byte[] encoded)
        {
            using (var stream=new MemoryStream(encoded,false))
            using (var source=new Bitmap(stream)) {
                if (source.Height<=TileHeight) { yield return encoded; yield break; }
                for (int y=0;y<source.Height;) {
                    int count=Math.Min(TileHeight,source.Height-y);
                    if (y+count<source.Height) {
                        // Prefer a row between lines, so letters and formulae stay together.
                        int best=count, bestRange=Int32.MaxValue;
                        for (int row=count-1;row>=count-90;row-=2) {
                            int min=255, max=0;
                            for (int x=source.Width/64;x<source.Width;x+=Math.Max(1,source.Width/64)) {
                                Color c=source.GetPixel(x,y+row); int l=(c.R+2*c.G+c.B)/4;
                                min=Math.Min(min,l); max=Math.Max(max,l);
                            }
                            if (max-min<bestRange) { bestRange=max-min; best=row; }
                            if (bestRange<7) break;
                        }
                        count=best;
                    }
                    using (var piece=source.Clone(new Rectangle(0,y,source.Width,count),PixelFormat.Format24bppRgb))
                    using (var output=new MemoryStream()) { piece.Save(output,ImageFormat.Png); yield return output.ToArray(); }
                    y+=count;
                }
            }
        }

        public static async Task<string> ReadTextAsync(byte[] encoded,CancellationToken token)
        {
            // Decode and tile off the UI thread even when local OCR is very fast.
            return await Task.Run(async delegate {
                var text=new StringBuilder();
                foreach (var piece in SplitForVision(encoded)) {
                    token.ThrowIfCancellationRequested();
                    string part=await LocalOcr.ReadAsync(piece,true).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(part)) text.AppendLine(part);
                }
                token.ThrowIfCancellationRequested();
                if (text.Length==0) throw new InvalidOperationException("这张长图里没有识别到文字。可在设置里改用支持图片的模型。");
                return text.ToString().TrimEnd();
            },token).ConfigureAwait(false);
        }
    }
}
