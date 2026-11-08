using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerPotion.BrandGen;

/// <summary>
/// Generates the PowerPotion brand assets (SVG + PNG + ICO + Inno Setup wizard bitmaps) from a small
/// vector scene description, so the SVG and every raster size stay in sync.
/// Run from the repository root:  dotnet run --project tools/BrandGen
/// </summary>
public static class Program
{
    private const double LogoSize = 512;
    private const double IconSize = 32;

    private sealed class Shape
    {
        public string Kind = "path";
        public double[] Points = Array.Empty<double>();
        public double X, Y, W, H, Rx, Ry, Cx, Cy;
        public string? D;
        public string Fill = "#000000";
        public double FillOpacity = 1;
        public string? Stroke;
        public double StrokeWidth;
        public double Scale = 1;
        public double ScaleCx, ScaleCy;
        public double Angle;
    }

    private sealed class Scene
    {
        public double Size;
        public readonly List<Shape> Shapes = new();
    }

    public static int Main()
    {
        var root = FindRepoRoot();
        Console.WriteLine("repo: " + root);

        var logo = BuildLogo(badge: false);
        var badge = BuildLogo(badge: true);

        var brandDir = Path.Combine(root, "assets", "branding");
        var iconDir = Path.Combine(brandDir, "icons");
        var revitIconDir = Path.Combine(root, "src", "PowerPotion.Revit", "Icons");
        var hostAssetDir = Path.Combine(root, "src", "PowerPotion.Host", "Assets");
        var installerDir = Path.Combine(root, "installer");
        foreach (var dir in new[] { brandDir, iconDir, revitIconDir, hostAssetDir })
            Directory.CreateDirectory(dir);

        // Logo
        File.WriteAllText(Path.Combine(brandDir, "logo.svg"), ToSvg(logo));
        File.WriteAllText(Path.Combine(brandDir, "logo-badge.svg"), ToSvg(badge));
        RenderPng(logo, 512, Path.Combine(brandDir, "logo.png"));
        RenderPng(badge, 512, Path.Combine(brandDir, "logo-badge.png"));
        RenderPng(badge, 256, Path.Combine(brandDir, "logo-badge-256.png"));
        RenderPng(logo, 256, Path.Combine(brandDir, "logo-256.png"));
        RenderPng(logo, 32, Path.Combine(brandDir, "logo-32.png"));
        RenderPng(logo, 16, Path.Combine(brandDir, "logo-16.png"));

        // Application icon (dark badge reads better on the taskbar)
        var icoPngs = new List<(int, byte[])>();
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
            icoPngs.Add((size, RenderPngBytes(badge, size)));
        WriteIco(Path.Combine(brandDir, "powerpotion.ico"), icoPngs);
        File.WriteAllBytes(Path.Combine(hostAssetDir, "powerpotion.ico"),
            File.ReadAllBytes(Path.Combine(brandDir, "powerpotion.ico")));
        File.WriteAllBytes(Path.Combine(hostAssetDir, "logo32.png"),
            File.ReadAllBytes(Path.Combine(brandDir, "logo-32.png")));
        File.WriteAllBytes(Path.Combine(hostAssetDir, "logo256.png"),
            File.ReadAllBytes(Path.Combine(brandDir, "logo-badge-256.png")));

        // Ribbon icons: 16 + 32 px PNG (Revit rules) and SVG sources
        var icons = new (string Name, Action<Scene> Build)[]
        {
            ("start", s => Glyph(s, PlayGlyph)),
            ("stop", s => Glyph(s, StopGlyph)),
            ("pause", s => Glyph(s, PauseGlyph)),
            ("capture", s => Glyph(s, CameraGlyph)),
            ("panorama", s => Glyph(s, PanoramaGlyph)),
            ("material", s => Glyph(s, MaterialGlyph)),
            ("sun", s => Glyph(s, SunGlyph)),
            ("refresh", s => Glyph(s, RefreshGlyph)),
            ("about", s => Glyph(s, AboutGlyph)),
        };
        foreach (var (name, build) in icons)
        {
            var scene = new Scene { Size = IconSize };
            build(scene);
            File.WriteAllText(Path.Combine(iconDir, name + ".svg"), ToSvg(scene));
            RenderPng(scene, 16, Path.Combine(revitIconDir, name + "16.png"));
            RenderPng(scene, 32, Path.Combine(revitIconDir, name + "32.png"));
        }
        RenderIconSheet(icons, Path.Combine(brandDir, "ribbon-icons.png"));

        // Inno Setup wizard artwork (dark purple, logo + wordmark)
        RenderWizard(logo, badge, installerDir);

        Console.WriteLine("logo: 512/256/32/16 png + svg + ico");
        Console.WriteLine("ribbon icons: 8 x (16, 32) png + svg");
        Console.WriteLine("installer: wizard.bmp, wizard-small.bmp, setup icon");
        return 0;
    }

    // ------------------------------------------------------------------
    // Logo artwork (512 x 512 view box)
    // ------------------------------------------------------------------

    private static Scene BuildLogo(bool badge)
    {
        var s = new Scene { Size = LogoSize };
        if (badge)
        {
            R(s, "#1A0F2B", 8, 8, 496, 496, 96);
            R(s, "#241536", 8, 8, 496, 496, 96, stroke: "#6D4EA6", strokeWidth: 5);
            R(s, "#000000", 22, 22, 468, 468, 84, opacity: 0.25, stroke: "#A56BFF", strokeWidth: 2);
        }

        var scale = badge ? 0.82 : 1.0;
        var scx = 256.0;
        var scy = badge ? 272.0 : 286.0;
        void Add(Shape shape)
        {
            shape.Scale = scale;
            shape.ScaleCx = scx;
            shape.ScaleCy = scy;
            s.Shapes.Add(shape);
        }

        // soft shadow under the bottle
        var shadow = Ellipse("#000000", 256, 486, 118, 16, opacity: badge ? 0.35 : 0.22);
        Add(shadow);

        // ---- bottle body (faceted, isometric) ----
        // silhouette: A(126,250) B(256,178) C(386,250) D(386,418) E(256,496) F(126,418)
        Add(Poly("#2E2350", 126, 250, 256, 178, 386, 250, 256, 322, 126, 250, 386, 250, 386, 418, 256, 496, 126, 418));
        Add(Poly("#A78BFA", 126, 250, 256, 178, 386, 250, 256, 322, opacity: 0.85)); // glass top
        Add(Poly("#7C3AED", 126, 250, 256, 322, 256, 496, 126, 418));               // left liquid
        Add(Poly("#6D2FE0", 386, 250, 386, 418, 256, 496, 256, 322));               // right liquid
        Add(Poly("#B794FF", 160, 268, 256, 218, 352, 268, 256, 318, opacity: 0.85)); // liquid surface
        Add(Poly("#FFFFFF", 142, 266, 156, 272, 156, 388, 142, 380, opacity: 0.16)); // glass highlight
        Add(Poly("#FFFFFF", 256, 322, 372, 256, 374, 262, 262, 330, opacity: 0.10)); // top sheen

        // ---- neck + cork ----
        Add(Poly("#C7B3F5", 226, 146, 286, 146, 298, 210, 214, 210, opacity: 0.95));
        Add(Poly("#7C3AED", 234, 162, 278, 162, 288, 206, 224, 206));
        Add(R("#D9B77E", 218, 76, 76, 82, 10));
        Add(Ellipse("#EAD2A3", 256, 80, 34, 10));
        Add(R("#C49A63", 218, 142, 76, 14, 6));
        Add(R("#C49A63", 218, 104, 76, 4, 2, opacity: 0.55));
        Add(R("#C49A63", 218, 120, 76, 3, 1.5, opacity: 0.45));

        // ---- rope around the neck ----
        Add(Ellipse(null, 256, 208, 54, 14, stroke: "#8B5A2B", strokeWidth: 10));
        Add(Ellipse("#7A4B24", 302, 212, 11, 9));
        Add(PathShape("#8B5A2B", "M 302 220 C 318 232 320 248 306 262", stroke: "#8B5A2B", strokeWidth: 9));
        Add(PathShape("#8B5A2B", "M 292 220 C 284 236 288 250 300 258", stroke: "#6B4423", strokeWidth: 7));

        // ---- tag with lightning bolt ----
        Add(R("#2B2338", 310, 274, 112, 112, 16, stroke: "#6D5B8F", strokeWidth: 4, angle: -16, acx: 366, acy: 330));
        Add(PathShape("#F5B544", "M 366 286 L 330 344 L 356 344 L 340 392 L 392 326 L 364 326 Z",
            stroke: "#C98A1E", strokeWidth: 3, angle: -16, acx: 366, acy: 330));
        Add(PathShape("#FFE29A", "M 362 298 L 343 335 L 360 335 L 350 363 L 375 322 L 360 322 Z",
            opacity: 0.75, angle: -16, acx: 366, acy: 330));

        return s;
    }

    // ------------------------------------------------------------------
    // Ribbon glyphs (32 x 32 view box)
    // ------------------------------------------------------------------

    private static void Glyph(Scene s, Action<Scene> glyph)
    {
        R(s, "#2A1840", 0.5, 0.5, 31, 31, 7);
        R(s, "#241536", 0.5, 0.5, 31, 31, 7, stroke: "#6D4EA6", strokeWidth: 1.4);
        glyph(s);
    }

    private static void PlayGlyph(Scene s) =>
        s.Shapes.Add(Poly("#F5B544", 11, 8, 24, 16, 11, 24));

    private static void StopGlyph(Scene s) =>
        R(s, "#F5B544", 10, 10, 12, 12, 2);

    private static void PauseGlyph(Scene s)
    {
        R(s, "#F5B544", 9.5, 9, 4.5, 14, 1.5);
        R(s, "#F5B544", 18, 9, 4.5, 14, 1.5);
    }

    private static void CameraGlyph(Scene s)
    {
        R(s, "#F3EDFF", 6.5, 10.5, 19, 13, 2.5);
        R(s, "#F3EDFF", 12, 7, 8, 4, 1.5);
        Ellipse(s, "#2A1840", 16, 17, 4.4, 4.4, stroke: "#F5B544", strokeWidth: 1.8);
    }

    private static void PanoramaGlyph(Scene s)
    {
        Ellipse(s, null, 16, 16, 8.6, 8.6, stroke: "#F3EDFF", strokeWidth: 1.8);
        Ellipse(s, null, 16, 16, 3.6, 8.6, stroke: "#F3EDFF", strokeWidth: 1.3);
        PathScene(s, null, "M 7.4 16 H 24.6", stroke: "#F3EDFF", strokeWidth: 1.3);
        PathScene(s, null, "M 9 11.4 C 12 10.2 20 10.2 23 11.4", stroke: "#F5B544", strokeWidth: 1.4);
    }

    private static void MaterialGlyph(Scene s)
    {
        PathScene(s, "#F5B544", "M 16 6.5 C 16 12 10 14.8 10 19.8 A 6 6 0 0 0 22 19.8 C 22 14.8 16 12 16 6.5 Z",
            stroke: "#C98A1E", strokeWidth: 1.2);
        Ellipse(s, "#FFE9B8", 13.6, 17.6, 1.5, 2.6, opacity: 0.8);
    }

    private static void SunGlyph(Scene s)
    {
        Ellipse(s, "#F5B544", 16, 13.5, 4.4, 4.4);
        foreach (var (x1, y1, x2, y2) in new (double, double, double, double)[]
                 {
                     (16, 5.5, 16, 7.6), (16, 19.4, 16, 21.5),
                     (8, 13.5, 9.9, 13.5), (22.1, 13.5, 24, 13.5),
                     (10.3, 7.8, 11.7, 9.2), (20.3, 17.8, 21.7, 19.2),
                     (21.7, 7.8, 20.3, 9.2), (11.7, 17.8, 10.3, 19.2),
                 })
            PathScene(s, null, $"M {F(x1)} {F(y1)} L {F(x2)} {F(y2)}", stroke: "#F5B544", strokeWidth: 1.7);
        PathScene(s, null, "M 6.5 25.5 H 25.5", stroke: "#F3EDFF", strokeWidth: 1.4);
    }

    private static void AboutGlyph(Scene s)
    {
        Ellipse(s, null, 16, 16, 9, 9, stroke: "#F5B544", strokeWidth: 1.9);
        Ellipse(s, "#F3EDFF", 16, 10.8, 1.5, 1.5);
        R(s, "#F3EDFF", 15, 14, 2, 7, 1);
    }

    private static void RefreshGlyph(Scene s)
    {
        PathScene(s, null, "M 24.5 16 A 8.5 8.5 0 1 1 21.6 9.4", stroke: "#F3EDFF", strokeWidth: 2.2);
        s.Shapes.Add(Poly("#F5B544", 18.6, 6.0, 24.6, 9.2, 18.2, 13.0));
    }

    // ------------------------------------------------------------------
    // Scene helpers
    // ------------------------------------------------------------------

    private static Shape Poly(string? fill, params double[] points) => new()
    {
        Kind = "polygon", Points = points, Fill = fill ?? "none", FillOpacity = 1,
    };

    private static Shape Poly(string fill, double a, double b, double c, double d, double e, double f,
        double g, double h, double i, double j, double k, double l, double m, double n, double o, double p, double q,
        double r, double opacity = 1) => new()
    {
        Kind = "polygon",
        Points = new[] { a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r },
        Fill = fill, FillOpacity = opacity,
    };

    private static Shape Poly(string fill, double a, double b, double c, double d, double e, double f,
        double g, double h, double opacity = 1) => new()
    {
        Kind = "polygon", Points = new[] { a, b, c, d, e, f, g, h }, Fill = fill, FillOpacity = opacity,
    };

    private static Shape Ellipse(string? fill, double cx, double cy, double rx, double ry, double opacity = 1,
        string? stroke = null, double strokeWidth = 0) => new()
    {
        Kind = "ellipse", Cx = cx, Cy = cy, Rx = rx, Ry = ry,
        Fill = fill ?? "none", FillOpacity = opacity, Stroke = stroke, StrokeWidth = strokeWidth,
    };

    private static Shape R(string fill, double x, double y, double w, double h, double rx, double opacity = 1,
        string? stroke = null, double strokeWidth = 0, double angle = 0, double acx = 0, double acy = 0) => new()
    {
        Kind = "rect", X = x, Y = y, W = w, H = h, Rx = rx,
        Fill = fill, FillOpacity = opacity, Stroke = stroke, StrokeWidth = strokeWidth,
        Angle = angle, Cx = acx, Cy = acy,
    };

    private static Shape PathShape(string? fill, string d, double opacity = 1, string? stroke = null,
        double strokeWidth = 0, double angle = 0, double acx = 0, double acy = 0) => new()
    {
        Kind = "path", D = d, Fill = fill ?? "none", FillOpacity = opacity,
        Stroke = stroke, StrokeWidth = strokeWidth, Angle = angle, Cx = acx, Cy = acy,
    };

    private static void Ellipse(Scene s, string? fill, double cx, double cy, double rx, double ry,
        double opacity = 1, string? stroke = null, double strokeWidth = 0) =>
        s.Shapes.Add(Ellipse(fill, cx, cy, rx, ry, opacity, stroke, strokeWidth));

    private static void R(Scene s, string fill, double x, double y, double w, double h, double rx,
        double opacity = 1, string? stroke = null, double strokeWidth = 0) =>
        s.Shapes.Add(R(fill, x, y, w, h, rx, opacity, stroke, strokeWidth));

    private static void PathScene(Scene s, string? fill, string d, double opacity = 1, string? stroke = null,
        double strokeWidth = 0) => s.Shapes.Add(PathShape(fill, d, opacity, stroke, strokeWidth));

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private static void RenderPng(Scene scene, int size, string path) =>
        File.WriteAllBytes(path, RenderPngBytes(scene, size));

    private static byte[] RenderPngBytes(Scene scene, int size)
    {
        var bitmap = Render(scene, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static RenderTargetBitmap Render(Scene scene, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            DrawScene(dc, scene, size / scene.Size, 0, 0);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void DrawScene(DrawingContext dc, Scene scene, double scale, double ox, double oy)
    {
        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(scale, scale));
        foreach (var shape in scene.Shapes)
            DrawShape(dc, shape);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawShape(DrawingContext dc, Shape shape)
    {
        var geometry = BuildGeometry(shape);
        var transforms = new TransformGroup();
        if (shape.Scale != 1)
            transforms.Children.Add(new ScaleTransform(shape.Scale, shape.Scale, shape.ScaleCx, shape.ScaleCy));
        if (shape.Angle != 0)
            transforms.Children.Add(new RotateTransform(shape.Angle, shape.Cx, shape.Cy));

        var fill = shape.Fill == "none" ? null : Brush(shape.Fill, shape.FillOpacity);
        var pen = shape.Stroke != null && shape.StrokeWidth > 0
            ? new Pen(Brush(shape.Stroke, 1), shape.StrokeWidth)
            : null;
        if (pen != null) pen.Freeze();

        var pushed = transforms.Children.Count > 0;
        if (pushed) dc.PushTransform(transforms);
        dc.DrawGeometry(fill, pen, geometry);
        if (pushed) dc.Pop();
    }

    private static Geometry BuildGeometry(Shape shape)
    {
        switch (shape.Kind)
        {
            case "polygon":
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(shape.Points[0], shape.Points[1]), true, true);
                    for (int i = 2; i < shape.Points.Length; i += 2)
                        ctx.LineTo(new Point(shape.Points[i], shape.Points[i + 1]), true, false);
                }
                g.Freeze();
                return g;
            }
            case "ellipse":
                return new EllipseGeometry(new Point(shape.Cx, shape.Cy), shape.Rx, shape.Ry);
            case "rect":
                return new RectangleGeometry(new Rect(shape.X, shape.Y, shape.W, shape.H), shape.Rx, shape.Rx);
            default:
                return Geometry.Parse(shape.D!);
        }
    }

    private static SolidColorBrush Brush(string hex, double opacity)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Opacity = opacity;
        brush.Freeze();
        return brush;
    }

    private static void RenderIconSheet((string Name, Action<Scene> Build)[] icons, string path)
    {
        const int cols = 4, cellW = 160, cellH = 92;
        const int w = cols * cellW, h = 2 * cellH;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush("#140B22", 1), null, new Rect(0, 0, w, h));
            for (int i = 0; i < icons.Length; i++)
            {
                int x = (i % cols) * cellW;
                int y = (i / cols) * cellH;
                var scene = new Scene { Size = IconSize };
                icons[i].Build(scene);
                // 16 px preview (actual Revit small size) and 32 px large size
                DrawScene(dc, scene, 0.5, x + 10, y + 16);
                DrawScene(dc, scene, 1.0, x + 46, y + 8);
                var label = new FormattedText(icons[i].Name, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brush("#D8CCF0", 1), 1.0);
                dc.DrawText(label, new Point(x + 88, y + 40));
            }
        }
        SavePng(visual, w, h, path);
    }

    private static void SavePng(DrawingVisual visual, int w, int h, string path)
    {
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void RenderWizard(Scene logo, Scene badge, string installerDir)
    {
        // 164 x 314 wizard image
        const int w = 164, h = 314;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush("#1A0F2B", 1), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(Brush("#241536", 1), null, new Rect(0, 0, w, 6));
            DrawScene(dc, badge, 130.0 / badge.Size, 17, 52);

            var title = new FormattedText("PowerPotion", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 20,
                Brush("#EDE6FB", 1), 1.0);
            dc.DrawText(title, new Point((w - title.Width) / 2, 208));

            var sub = new FormattedText("for Revit", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                Brush("#B9A9D6", 1), 1.0);
            dc.DrawText(sub, new Point((w - sub.Width) / 2, 234));

            dc.DrawRectangle(Brush("#6D4EA6", 1), null, new Rect(30, 264, 104, 2));
        }
        SaveBmp(visual, w, h, Path.Combine(installerDir, "wizard.bmp"));

        // 55 x 55 small image
        var small = new DrawingVisual();
        using (var dc = small.RenderOpen())
        {
            dc.DrawRectangle(Brush("#1A0F2B", 1), null, new Rect(0, 0, 55, 55));
            DrawScene(dc, badge, 47.0 / badge.Size, 4, 4);
        }
        SaveBmp(small, 55, 55, Path.Combine(installerDir, "wizard-small.bmp"));
    }

    private static void SaveBmp(DrawingVisual visual, int w, int h, string path)
    {
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(png.Length);
            bw.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) bw.Write(png);
    }

    // ------------------------------------------------------------------
    // SVG
    // ------------------------------------------------------------------

    private static string ToSvg(Scene scene)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {0} {0}\" width=\"{0}\" height=\"{0}\">\n",
            F(scene.Size));
        foreach (var shape in scene.Shapes)
        {
            var style = new StringBuilder();
            AppendPaint(style, "fill", shape.Fill, shape.FillOpacity);
            if (shape.Stroke != null && shape.StrokeWidth > 0)
            {
                style.AppendFormat(CultureInfo.InvariantCulture, " stroke=\"{0}\" stroke-width=\"{1}\"",
                    shape.Stroke, F(shape.StrokeWidth));
            }
            var transform = new StringBuilder();
            if (shape.Scale != 1)
            {
                transform.AppendFormat(CultureInfo.InvariantCulture,
                    "translate({0} {1}) scale({2}) translate({3} {4})",
                    F(shape.ScaleCx), F(shape.ScaleCy), F(shape.Scale), F(-shape.ScaleCx), F(-shape.ScaleCy));
            }
            if (shape.Angle != 0)
            {
                if (transform.Length > 0) transform.Append(' ');
                transform.AppendFormat(CultureInfo.InvariantCulture, "rotate({0} {1} {2})",
                    F(shape.Angle), F(shape.Cx), F(shape.Cy));
            }
            var tf = transform.Length > 0 ? $" transform=\"{transform}\"" : "";

            switch (shape.Kind)
            {
                case "polygon":
                {
                    if (shape.Points.Length % 2 != 0)
                        throw new InvalidOperationException($"odd polygon point count: {shape.Points.Length}");
                    var pts = new StringBuilder();
                    for (int i = 0; i < shape.Points.Length; i += 2)
                    {
                        if (i > 0) pts.Append(' ');
                        pts.AppendFormat(CultureInfo.InvariantCulture, "{0},{1}",
                            F(shape.Points[i]), F(shape.Points[i + 1]));
                    }
                    sb.AppendFormat("<polygon points=\"{0}\" {1}{2}/>\n", pts, style, tf);
                    break;
                }
                case "ellipse":
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "<ellipse cx=\"{0}\" cy=\"{1}\" rx=\"{2}\" ry=\"{3}\" {4}{5}/>\n",
                        F(shape.Cx), F(shape.Cy), F(shape.Rx), F(shape.Ry), style, tf);
                    break;
                case "rect":
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"{4}\" {5}{6}/>\n",
                        F(shape.X), F(shape.Y), F(shape.W), F(shape.H), F(shape.Rx), style, tf);
                    break;
                default:
                    sb.AppendFormat("<path d=\"{0}\" {1}{2}/>\n", shape.D, style, tf);
                    break;
            }
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void AppendPaint(StringBuilder sb, string attribute, string color, double opacity)
    {
        if (color == "none")
        {
            sb.AppendFormat(" {0}=\"none\"", attribute);
            return;
        }
        sb.AppendFormat(CultureInfo.InvariantCulture, " {0}=\"{1}\"", attribute, color);
        if (opacity < 1)
            sb.AppendFormat(CultureInfo.InvariantCulture, " {0}-opacity=\"{1}\"", attribute, F(opacity));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
