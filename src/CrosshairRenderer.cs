using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CrosshairMarker;

internal static class CrosshairRenderer
{
    private static readonly object ImageCacheLock = new();
    private static string? cachedImagePath;
    private static DateTime cachedImageWriteTimeUtc;
    private static long cachedImageLength;
    private static Bitmap? cachedImage;

    public static Bitmap RenderBitmap(Size size, CrosshairProfile profile)
    {
        var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var center = new PointF(size.Width / 2f, size.Height / 2f);
        DrawImageLayer(graphics, center, profile.ImageLayer);

        if (!profile.ProceduralEnabled)
        {
            return bitmap;
        }

        if (profile.OutlineEnabled)
        {
            DrawOutline(graphics, center, profile);
        }

        if (profile.CrosshairEnabled)
        {
            using var crosshair = BuildShapePath(center, profile, includeDot: false);
            using var crosshairBrush = new SolidBrush(profile.Color.ToColor());
            graphics.FillPath(crosshairBrush, crosshair);
        }

        if (profile.DotEnabled)
        {
            using var dot = BuildShapePath(center, profile, includeCrosshair: false);
            using var dotBrush = new SolidBrush(Color.FromArgb(
                Math.Clamp(profile.DotOpacity, 0, 255),
                profile.DotColor.R,
                profile.DotColor.G,
                profile.DotColor.B));
            graphics.FillPath(dotBrush, dot);
        }
        return bitmap;
    }

    private static void DrawImageLayer(Graphics graphics, PointF center, ImageLayer layer)
    {
        if (!layer.Enabled || !layer.HasImage)
        {
            return;
        }

        try
        {
            var image = GetCachedImage(layer.Path!);
            var scale = Math.Clamp(layer.ScalePercent, 1, 400) / 100f;
            var width = image.Width * scale;
            var height = image.Height * scale;
            var anchorX = Math.Clamp(layer.AnchorX ?? image.Width / 2, 0, image.Width);
            var anchorY = Math.Clamp(layer.AnchorY ?? image.Height / 2, 0, image.Height);
            var origin = new PointF(center.X + layer.OffsetX, center.Y + layer.OffsetY);
            var destination = new RectangleF(-anchorX * scale, -anchorY * scale, width, height);

            using var attributes = new ImageAttributes();
            var alpha = Math.Clamp(layer.Opacity, 0, 255) / 255f;
            var matrix = new ColorMatrix
            {
                Matrix00 = 1f,
                Matrix11 = 1f,
                Matrix22 = 1f,
                Matrix33 = alpha,
                Matrix44 = 1f
            };
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

            var state = graphics.Save();
            graphics.TranslateTransform(origin.X, origin.Y);
            graphics.RotateTransform(NormalizeAngle(layer.Rotation));
            graphics.DrawImage(
                image,
                Rectangle.Round(destination),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
            graphics.Restore(state);
        }
        catch
        {
            // Broken or removed image assets should not break overlay rendering.
        }
    }

    private static Bitmap LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static Bitmap GetCachedImage(string path)
    {
        var info = new FileInfo(path);
        lock (ImageCacheLock)
        {
            if (cachedImage is not null
                && string.Equals(cachedImagePath, path, StringComparison.OrdinalIgnoreCase)
                && cachedImageWriteTimeUtc == info.LastWriteTimeUtc
                && cachedImageLength == info.Length)
            {
                return cachedImage;
            }

            cachedImage?.Dispose();
            cachedImage = LoadImage(path);
            cachedImagePath = path;
            cachedImageWriteTimeUtc = info.LastWriteTimeUtc;
            cachedImageLength = info.Length;
            return cachedImage;
        }
    }

    private static void DrawOutline(Graphics graphics, PointF center, CrosshairProfile profile)
    {
        var outline = Math.Max(1, profile.OutlineThickness);
        using var outer = BuildShapePath(center, profile, outline);
        using var inner = BuildShapePath(center, profile);
        using var region = new Region(outer);
        using var brush = new SolidBrush(profile.OutlineColor.ToColor());

        region.Exclude(inner);
        graphics.FillRegion(brush, region);
    }

    private static GraphicsPath BuildShapePath(
        PointF center,
        CrosshairProfile profile,
        int inflate = 0,
        bool includeCrosshair = true,
        bool includeDot = true)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var length = Math.Max(1, profile.Length);
        var gap = Math.Max(0, profile.Gap);
        var safeThickness = Math.Max(1, profile.Thickness);

        if (includeCrosshair && profile.CrosshairEnabled)
        {
            AddCrosshairShape(path, center, profile, length, gap, safeThickness, inflate);
        }

        if (includeDot && profile.DotEnabled)
        {
            var size = Math.Max(1, profile.DotSize);
            AddDotShape(path, center, size + inflate * 2, profile.DotShape);
        }

        if (profile.CrosshairRotation != 0)
        {
            using var matrix = new Matrix();
            matrix.RotateAt(NormalizeAngle(profile.CrosshairRotation), center);
            path.Transform(matrix);
        }

        return path;
    }

    private static void AddDotShape(GraphicsPath path, PointF center, float size, DotShape shape)
    {
        var half = size / 2f;
        switch (shape)
        {
            case DotShape.Square:
                path.AddRectangle(new RectangleF(center.X - half, center.Y - half, size, size));
                break;
            case DotShape.Diamond:
                path.AddPolygon([
                    new PointF(center.X, center.Y - half),
                    new PointF(center.X + half, center.Y),
                    new PointF(center.X, center.Y + half),
                    new PointF(center.X - half, center.Y)
                ]);
                break;
            case DotShape.Triangle:
                path.AddPolygon([
                    new PointF(center.X, center.Y - half),
                    new PointF(center.X + half, center.Y + half),
                    new PointF(center.X - half, center.Y + half)
                ]);
                break;
            case DotShape.Heart:
                AddHeart(path, center, size);
                break;
            case DotShape.Cross:
                var arm = Math.Max(1f, size / 3f);
                path.AddRectangle(new RectangleF(center.X - arm / 2f, center.Y - half, arm, size));
                path.AddRectangle(new RectangleF(center.X - half, center.Y - arm / 2f, size, arm));
                break;
            case DotShape.Star:
                path.AddPolygon(BuildRegularPolygon(center, 5, half, half * .45f, -90));
                break;
            case DotShape.Hexagon:
                path.AddPolygon(BuildRegularPolygon(center, 6, half, half, -30));
                break;
            case DotShape.Pentagon:
                path.AddPolygon(BuildRegularPolygon(center, 5, half, half, -90));
                break;
            case DotShape.X:
                AddRotatedRectangle(path, center, new SizeF(Math.Max(1f, size / 3f), size), 45);
                AddRotatedRectangle(path, center, new SizeF(Math.Max(1f, size / 3f), size), -45);
                break;
            case DotShape.Paw:
                AddPaw(path, center, size);
                break;
            default:
                path.AddEllipse(center.X - half, center.Y - half, size, size);
                break;
        }
    }

    private static void AddCrosshairShape(
        GraphicsPath path,
        PointF center,
        CrosshairProfile profile,
        int length,
        int gap,
        int thickness,
        int inflate)
    {
        var halfThickness = thickness / 2f;
        var shape = profile.CrosshairShape;

        if (profile.TShape && shape is CrosshairShape.Classic or CrosshairShape.TightPlus or CrosshairShape.OpenPlus)
        {
            shape = CrosshairShape.Classic;
        }

        switch (shape)
        {
            case CrosshairShape.TightPlus:
                AddClassicArms(path, center, false, length, Math.Max(0, gap / 2), thickness, inflate);
                break;
            case CrosshairShape.OpenPlus:
                path.AddRectangle(Inflate(new RectangleF(center.X - halfThickness, center.Y - length, thickness, length * 2), inflate));
                path.AddRectangle(Inflate(new RectangleF(center.X - length, center.Y - halfThickness, length * 2, thickness), inflate));
                break;
            case CrosshairShape.Corners:
                AddCornerShape(path, center, length, gap, thickness, inflate);
                break;
            case CrosshairShape.Box:
                AddBoxShape(path, center, length, gap, thickness, inflate);
                break;
            case CrosshairShape.Circle:
                AddRingShape(path, center, gap + length, thickness, inflate);
                break;
            case CrosshairShape.Arcs:
                AddArcSegmentShape(path, center, gap + length, Math.Max(14, length), thickness, inflate);
                break;
            case CrosshairShape.Chevron:
                AddChevronShape(path, center, length, gap, thickness, inflate);
                break;
            case CrosshairShape.Brackets:
                AddBracketShape(path, center, length, gap, thickness, inflate);
                break;
            case CrosshairShape.DotCorners:
                AddDotCornerShape(path, center, length, gap, thickness, inflate);
                break;
            case CrosshairShape.Sniper:
                AddRingShape(path, center, gap + length, thickness, inflate);
                AddClassicArms(path, center, profile.TShape, Math.Max(8, length / 2), gap + length + Math.Max(4, thickness * 2), thickness, inflate);
                break;
            default:
                AddClassicArms(path, center, profile.TShape, length, gap, thickness, inflate);
                break;
        }
    }

    private static void AddClassicArms(
        GraphicsPath path,
        PointF center,
        bool tShape,
        int length,
        int gap,
        int thickness,
        int inflate)
    {
        var halfThickness = thickness / 2f;
        if (!tShape)
        {
            path.AddRectangle(Inflate(new RectangleF(center.X - halfThickness, center.Y - gap - length, thickness, length), inflate));
        }

        path.AddRectangle(Inflate(new RectangleF(center.X - halfThickness, center.Y + gap, thickness, length), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X - gap - length, center.Y - halfThickness, length, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X + gap, center.Y - halfThickness, length, thickness), inflate));
    }

    private static void AddRingShape(GraphicsPath path, PointF center, int radius, int thickness, int inflate)
    {
        AddArcBars(path, center, Math.Max(8, radius), 0, 360, Math.Max(2, thickness), inflate, 28);
    }

    private static void AddArcSegmentShape(
        GraphicsPath path,
        PointF center,
        int radius,
        int arcLength,
        int thickness,
        int inflate)
    {
        var sweep = Math.Clamp(arcLength * 2, 28, 74);
        AddArcBars(path, center, radius, -sweep / 2f, sweep, thickness, inflate, 6);
        AddArcBars(path, center, radius, 90 - sweep / 2f, sweep, thickness, inflate, 6);
        AddArcBars(path, center, radius, 180 - sweep / 2f, sweep, thickness, inflate, 6);
        AddArcBars(path, center, radius, 270 - sweep / 2f, sweep, thickness, inflate, 6);
    }

    private static void AddArcBars(
        GraphicsPath path,
        PointF center,
        int radius,
        float startDegrees,
        float sweepDegrees,
        int thickness,
        int inflate,
        int segments)
    {
        var safeSegments = Math.Max(2, segments);
        var barLength = Math.Max(thickness * 2f, radius * MathF.Abs(sweepDegrees) * MathF.PI / 180f / safeSegments * 1.15f);
        for (var index = 0; index < safeSegments; index++)
        {
            var progress = safeSegments == 1 ? .5f : index / (float)(safeSegments - 1);
            var degrees = startDegrees + sweepDegrees * progress;
            var radians = degrees * MathF.PI / 180f;
            var point = new PointF(
                center.X + MathF.Cos(radians) * radius,
                center.Y + MathF.Sin(radians) * radius);
            AddRotatedRectangle(path, point, new SizeF(barLength + inflate * 2, thickness + inflate * 2), degrees + 90);
        }
    }

    private static void AddChevronShape(GraphicsPath path, PointF center, int length, int gap, int thickness, int inflate)
    {
        var y = center.Y + gap + length / 2f;
        var half = Math.Max(6, length / 2f);
        AddRotatedRectangle(path, new PointF(center.X - half / 2f, y), new SizeF(thickness + inflate * 2, half * 1.7f), -45);
        AddRotatedRectangle(path, new PointF(center.X + half / 2f, y), new SizeF(thickness + inflate * 2, half * 1.7f), 45);
    }

    private static void AddBracketShape(GraphicsPath path, PointF center, int length, int gap, int thickness, int inflate)
    {
        var height = Math.Max(12, length);
        var width = Math.Max(6, length / 3f);
        var left = center.X - gap - width - thickness;
        var right = center.X + gap;
        var top = center.Y - height / 2f;
        path.AddRectangle(Inflate(new RectangleF(left, top, thickness, height), inflate));
        path.AddRectangle(Inflate(new RectangleF(left, top, width, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(left, top + height - thickness, width, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(right + width - thickness, top, thickness, height), inflate));
        path.AddRectangle(Inflate(new RectangleF(right, top, width, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(right, top + height - thickness, width, thickness), inflate));
    }

    private static void AddDotCornerShape(GraphicsPath path, PointF center, int length, int gap, int thickness, int inflate)
    {
        var size = Math.Max(3, thickness * 2);
        var offset = gap + Math.Max(6, length / 2f);
        path.AddRectangle(Inflate(new RectangleF(center.X - offset - size / 2f, center.Y - offset - size / 2f, size, size), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X + offset - size / 2f, center.Y - offset - size / 2f, size, size), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X - offset - size / 2f, center.Y + offset - size / 2f, size, size), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X + offset - size / 2f, center.Y + offset - size / 2f, size, size), inflate));
    }

    private static void AddCornerShape(GraphicsPath path, PointF center, int length, int gap, int thickness, int inflate)
    {
        var outer = gap + length;
        var inner = gap;
        AddCorner(path, center.X - outer, center.Y - outer, inner, outer, thickness, inflate, 1, 1);
        AddCorner(path, center.X + inner, center.Y - outer, inner, outer, thickness, inflate, -1, 1);
        AddCorner(path, center.X - outer, center.Y + inner, inner, outer, thickness, inflate, 1, -1);
        AddCorner(path, center.X + inner, center.Y + inner, inner, outer, thickness, inflate, -1, -1);
    }

    private static void AddCorner(
        GraphicsPath path,
        float x,
        float y,
        int inner,
        int outer,
        int thickness,
        int inflate,
        int horizontalDirection,
        int verticalDirection)
    {
        var length = outer - inner;
        path.AddRectangle(Inflate(new RectangleF(x, y, length, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(x, y, thickness, length), inflate));
    }

    private static void AddBoxShape(GraphicsPath path, PointF center, int length, int gap, int thickness, int inflate)
    {
        var half = gap + length;
        path.AddRectangle(Inflate(new RectangleF(center.X - half, center.Y - half, half * 2, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X - half, center.Y + half - thickness, half * 2, thickness), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X - half, center.Y - half, thickness, half * 2), inflate));
        path.AddRectangle(Inflate(new RectangleF(center.X + half - thickness, center.Y - half, thickness, half * 2), inflate));
    }

    private static void AddHeart(GraphicsPath path, PointF center, float size)
    {
        var scale = size / 32f;
        PointF Map(float x, float y) => new(center.X + x * scale, center.Y + y * scale);

        path.StartFigure();
        path.AddBezier(Map(0, 11), Map(-18, -1), Map(-13, -17), Map(0, -8));
        path.AddBezier(Map(0, -8), Map(13, -17), Map(18, -1), Map(0, 11));
        path.CloseFigure();
    }

    private static void AddPaw(GraphicsPath path, PointF center, float size)
    {
        var padWidth = size * .56f;
        var padHeight = size * .42f;
        path.AddEllipse(
            center.X - padWidth / 2f,
            center.Y - padHeight * .05f,
            padWidth,
            padHeight);

        var toe = size * .22f;
        var top = center.Y - size * .42f;
        path.AddEllipse(center.X - size * .36f - toe / 2f, top + size * .12f, toe, toe);
        path.AddEllipse(center.X - size * .12f - toe / 2f, top, toe, toe);
        path.AddEllipse(center.X + size * .12f - toe / 2f, top, toe, toe);
        path.AddEllipse(center.X + size * .36f - toe / 2f, top + size * .12f, toe, toe);
    }

    private static PointF[] BuildRegularPolygon(
        PointF center,
        int points,
        float outerRadius,
        float innerRadius,
        float rotationDegrees)
    {
        var total = innerRadius == outerRadius ? points : points * 2;
        var result = new PointF[total];
        var rotation = rotationDegrees * MathF.PI / 180f;
        for (var index = 0; index < total; index++)
        {
            var radius = index % 2 == 0 ? outerRadius : innerRadius;
            var angle = rotation + index * MathF.PI * 2f / total;
            result[index] = new PointF(
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius);
        }

        return result;
    }

    private static void AddRotatedRectangle(GraphicsPath path, PointF center, SizeF size, float angle)
    {
        using var rectangle = new GraphicsPath();
        rectangle.AddRectangle(new RectangleF(
            center.X - size.Width / 2f,
            center.Y - size.Height / 2f,
            size.Width,
            size.Height));
        using var matrix = new Matrix();
        matrix.RotateAt(angle, center);
        rectangle.Transform(matrix);
        path.AddPath(rectangle, false);
    }

    private static float NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static RectangleF Inflate(RectangleF rectangle, float amount)
    {
        rectangle.Inflate(amount, amount);
        return rectangle;
    }
}
