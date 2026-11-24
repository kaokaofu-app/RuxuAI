using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Jiyue
{
    // Work from the frozen pixels. Canvas, remote desktops and scanned pages can all use this path.
    internal sealed class VisualSelection
    {
        readonly byte[] pixels;
        readonly int stride;
        readonly Size size;

        public VisualSelection(Bitmap frame)
        {
            size=frame.Size;
            var bits=frame.LockBits(new Rectangle(Point.Empty,size),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            try {
                stride=size.Width*3;
                pixels=new byte[checked(stride*size.Height)];
                for(int y=0;y<size.Height;y++)
                    Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),pixels,y*stride,stride);
            } finally {frame.UnlockBits(bits);}
        }

        bool Edge(bool vertical,int line,int along)
        {
            int index=vertical?along*stride+line*3:line*stride+along*3;
            int prev=index-(vertical?3:stride);
            return Math.Abs(pixels[index]-pixels[prev])+Math.Abs(pixels[index+1]-pixels[prev+1])+
                Math.Abs(pixels[index+2]-pixels[prev+2])>=40;
        }

        bool Strong(bool vertical,int line,int start,int end)
        {
            int count=0,hits=0,step=Math.Max(1,(end-start)/31);
            for(int i=start;i<end;i+=step) {count++; if(Edge(vertical,line,i)) hits++;}
            return count>0 && hits*10>=count*7;
        }

        List<int> Sides(bool vertical,int start,int stop,int step,int along,int low,int high)
        {
            var lines=new List<int>();
            int a=Math.Max(low,along-9),b=Math.Min(high,along+10);
            for(int i=start;step>0?i<stop:i>stop;i+=step) {
                if(!Edge(vertical,i,along) || !Strong(vertical,i,a,b)) continue;
                lines.Add(i);
                if(lines.Count==4) break;
                i+=step;
            }
            return lines;
        }

        public Rectangle Find(Point point,Rectangle bounds)
        {
            bounds.Intersect(new Rectangle(Point.Empty,size));
            if(!bounds.Contains(point) || bounds.Width<32 || bounds.Height<20) return bounds;
            var left=Sides(true,point.X,bounds.Left,-1,point.Y,bounds.Top,bounds.Bottom);
            var right=Sides(true,point.X+1,bounds.Right,1,point.Y,bounds.Top,bounds.Bottom);
            var top=Sides(false,point.Y,bounds.Top,-1,point.X,bounds.Left,bounds.Right);
            var bottom=Sides(false,point.Y+1,bounds.Bottom,1,point.X,bounds.Left,bounds.Right);
            foreach(int l in left) foreach(int r in right) {
                if(r-l<24) continue;
                foreach(int t in top) foreach(int b in bottom) {
                    if(b-t<14) continue;
                    if(Strong(true,l,t+2,b-2) && Strong(true,r,t+2,b-2) &&
                        Strong(false,t,l+2,r-2) && Strong(false,b,l+2,r-2))
                        return Rectangle.FromLTRB(l,t,r,b);
                }
            }
            return TextLine(point,bounds);
        }

        Rectangle TextLine(Point p,Rectangle bounds)
        {
            // A text line has strokes of different widths between two quiet margins.
            // Photographs, large blank areas and uninterrupted textures fall back to the enclosing region.
            int left=Math.Max(bounds.Left+1,p.X-78), right=Math.Min(bounds.Right,p.X+79);
            int top=p.Y,bottom=p.Y;
            while(top>bounds.Top && p.Y-top<64 && InkNearby(left,right,top)) top--;
            while(bottom<bounds.Bottom && bottom-p.Y<64 && InkNearby(left,right,bottom)) bottom++;
            int h=bottom-top-1;
            if(h<6 || h>56 || top==p.Y || bottom==p.Y) return bounds;
            int x1=p.X,x2=Math.Max(bounds.Left+1,p.X),lastLeft=p.X,lastRight=p.X;
            int gap=Math.Max(10,h);
            while(x1>bounds.Left+1 && lastLeft-x1<=gap) {
                if(InkColumn(x1,top+1,bottom)) lastLeft=x1;
                x1--;
            }
            while(x2<bounds.Right && x2-lastRight<=gap) {
                if(InkColumn(x2,top+1,bottom)) lastRight=x2;
                x2++;
            }
            if(lastRight-lastLeft<Math.Max(24,h) || !InkNearby(Math.Max(1,lastLeft),lastRight,p.Y)) return bounds;
            var line=Rectangle.FromLTRB(lastLeft-3,top-2,lastRight+4,bottom+2);
            return Rectangle.Intersect(line,bounds);
        }
        bool InkNearby(int left,int right,int y)
        {
            int transitions=0;
            for(int x=left;x<right;x++) if(Edge(true,x,y) && ++transitions>=4) return true;
            return false;
        }
        bool InkColumn(int x,int top,int bottom)
        {
            for(int y=top;y<bottom;y++) if(Edge(true,x,y)) return true;
            return false;
        }
    }
}
