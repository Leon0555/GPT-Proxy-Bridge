using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GPTProxyBridge.App;

// A compact continuous corner: each corner uses two cubic curves whose
// curvature eases into the adjoining straight edges. Border's existing
// layout, padding, dependency properties and child behavior are preserved.
public sealed class SmoothBorder : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var thickness=BorderThickness;
        var corners=CornerRadius;
        if(thickness.Left!=thickness.Top || thickness.Left!=thickness.Right || thickness.Left!=thickness.Bottom ||
           corners.TopLeft!=corners.TopRight || corners.TopLeft!=corners.BottomLeft || corners.TopLeft!=corners.BottomRight)
        {
            base.OnRender(dc);
            return;
        }
        double line=Math.Max(0,thickness.Left), half=line/2;
        double width=Math.Max(0,ActualWidth-line), height=Math.Max(0,ActualHeight-line);
        if(width<=0 || height<=0)return;
        double radius=Math.Min(Math.Max(0,corners.TopLeft-half),Math.Min(width,height)/2);
        var geometry=new StreamGeometry();
        using(var c=geometry.Open())
        {
            double left=half,top=half,right=half+width,bottom=half+height,r=radius;
            c.BeginFigure(new Point(left+r,top),true,true);
            c.LineTo(new Point(right-r,top),true,false);
            Corner(c,right-r,top,r,0,0,r);
            c.LineTo(new Point(right,bottom-r),true,false);
            Corner(c,right,bottom-r,0,r,-r,0);
            c.LineTo(new Point(left+r,bottom),true,false);
            Corner(c,left+r,bottom,-r,0,0,-r);
            c.LineTo(new Point(left,top+r),true,false);
            Corner(c,left,top+r,0,-r,r,0);
        }
        geometry.Freeze();
        Pen? pen=line>0 && BorderBrush!=null?new Pen(BorderBrush,line):null;
        dc.DrawGeometry(Background,pen,geometry);
    }
    static void Corner(StreamGeometryContext c,double x,double y,double ax,double ay,double bx,double by)
    {
        Point P(double u,double v)=>new(x+u*ax+v*bx,y+u*ay+v*by);
        c.BezierTo(P(.25,0),P(.56,0),P(.78,.22),true,false);
        c.BezierTo(P(1,.44),P(1,.75),P(1,1),true,false);
    }
}
