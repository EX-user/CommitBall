using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommitBallBallShell;

public sealed class BallSkinCatalog : IBallSkinCatalog
{
    public BallSkinCatalog()
    {
        Skins = new IBallSkin[]
        {
            new SoftEyeSkin(),
            new HaloEyeSkin(),
            new EyeOfCommitSkin(),
            new ClassicSkin()
        };
    }

    public IReadOnlyList<IBallSkin> Skins { get; }

    public IBallSkin Get(string id) => Skins.FirstOrDefault(s => s.Id == id) ?? Skins[0];
}

public sealed class SoftEyeSkin : IBallSkin
{
    public string Id => "soft-eye";
    public string DisplayName => "Soft Eye";

    public BallBubbleStyle GetBubbleStyle(BallRuntimeState state)
    {
        return state.Mode switch
        {
            BallMode.Recording => new BallBubbleStyle(
                Color.FromRgb(48, 24, 33),
                Color.FromArgb(128, 255, 142, 154),
                Colors.White,
                11.0),
            _ => new BallBubbleStyle(
                Color.FromRgb(22, 38, 66),
                Color.FromArgb(120, 134, 185, 255),
                Colors.White,
                11.0)
        };
    }

    public void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame)
    {
        var animated = IsAnimatedEyeState(state);
        var pulse = animated ? frame.Pulse : 0.0;
        var morph = animated ? frame.Morph : 0.0;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.36 * (1.0 + morph * 0.018);
        var fill = state.Mode switch
        {
            BallMode.Recording => Color.FromRgb(237, 74, 84),
            _ => Color.FromRgb(55, 132, 236)
        };

        var glow = 0.42 + pulse * 0.58;
        var shadowBrush = new RadialGradientBrush(
            Color.FromArgb((byte)(95 * glow), fill.R, fill.G, fill.B),
            Colors.Transparent)
        {
            RadiusX = 0.78,
            RadiusY = 0.78,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.45, 0.38)
        };
        dc.DrawEllipse(shadowBrush, null, center, radius * 1.28, radius * 1.28);

        var body = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.32),
            GradientOrigin = new Point(0.30, 0.20),
            RadiusX = 0.72,
            RadiusY = 0.78
        };
        body.GradientStops.Add(new GradientStop(Lift(fill, 46), 0.0));
        body.GradientStops.Add(new GradientStop(fill, 0.62));
        body.GradientStops.Add(new GradientStop(Darken(fill, 42), 1.0));

        var transform = new ScaleTransform(1.0 + pulse * 0.018, 1.0 - morph * 0.055 + pulse * 0.015, center.X, center.Y);
        dc.PushTransform(transform);
        dc.DrawEllipse(body, new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), 1.6), center, radius, radius);

        if (animated)
        {
            RenderEye(dc, center, radius, frame);
        }
        else
        {
            RenderSymbol(dc, center, radius, state);
        }
        dc.Pop();

    }

    private static void RenderEye(DrawingContext dc, Point center, double radius, BallAnimationFrame frame)
    {
        var x = center.X + Math.Sin(frame.EyeYaw) * radius * 0.46;
        var y = center.Y + Math.Sin(frame.EyePitch) * radius * 0.34;
        var w = radius * (0.56 + Math.Cos(frame.EyeYaw) * 0.20);
        var open = 1.0 - Math.Pow(Math.Clamp(frame.Morph, 0.0, 1.0), 0.72) * 0.86;
        var h = radius * (0.70 + Math.Cos(frame.EyePitch) * 0.10) * Math.Max(0.08, open);
        var pupil = new StreamGeometry();
        using (var ctx = pupil.Open())
        {
            ctx.BeginFigure(new Point(x + w * 0.48, y), true, true);
            ctx.LineTo(new Point(x - w * 0.40, y - h * 0.48), true, false);
            ctx.LineTo(new Point(x - w * 0.40, y + h * 0.48), true, false);
        }
        pupil.Freeze();

        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(238, 248, 250, 255)), null, pupil);
        if (open > 0.25)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(82, 255, 255, 255)), null, new Point(x - w * 0.18, y - h * 0.22), 2.8, Math.Max(1.0, 2.4 * open));
        }
    }

    private static void RenderSymbol(DrawingContext dc, Point center, double radius, BallRuntimeState state)
    {
        BasicBallRenderer.RenderCenterSymbol(dc, center, radius, state);
    }

    private static bool IsAnimatedEyeState(BallRuntimeState state)
    {
        return state.Mode == BallMode.Recording && state.EyeEnabled;
    }

    private static Color Lift(Color color, byte amount) =>
        Color.FromRgb((byte)Math.Min(255, color.R + amount), (byte)Math.Min(255, color.G + amount), (byte)Math.Min(255, color.B + amount));

    private static Color Darken(Color color, byte amount) =>
        Color.FromRgb((byte)Math.Max(0, color.R - amount), (byte)Math.Max(0, color.G - amount), (byte)Math.Max(0, color.B - amount));
}

public sealed class HaloEyeSkin : IBallSkin
{
    public string Id => "halo-eye";
    public string DisplayName => "Halo Eye";

    public BallBubbleStyle GetBubbleStyle(BallRuntimeState state)
    {
        return state.Mode switch
        {
            BallMode.Recording => new BallBubbleStyle(
                Color.FromRgb(42, 24, 40),
                Color.FromArgb(132, 255, 112, 156),
                Color.FromRgb(255, 248, 252),
                14.0),
            _ => new BallBubbleStyle(
                Color.FromRgb(24, 32, 56),
                Color.FromArgb(125, 115, 178, 255),
                Color.FromRgb(247, 250, 255),
                14.0)
        };
    }

    public void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame)
    {
        var animated = IsAnimatedEyeState(state);
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.35;
        var baseColor = state.Mode switch
        {
            BallMode.Idle => Color.FromRgb(77, 144, 234),
            _ => Color.FromRgb(238, 70, 87)
        };
        var pulse = animated ? Math.Clamp(frame.Pulse, 0.0, 1.0) : 0.0;
        var morph = animated ? Math.Clamp(frame.Morph, 0.0, 1.0) : 0.0;
        var eyeYaw = animated ? Math.Clamp(frame.EyeYaw, -1.25, 1.25) : 0.0;
        var eyePitch = animated ? Math.Clamp(frame.EyePitch, -1.0, 1.0) : 0.0;

        for (var i = 2; i >= 0; i--)
        {
            var ring = radius * (1.04 + i * 0.18 + pulse * 0.14);
            var alpha = (byte)Math.Clamp(32 + pulse * 62 - i * 8, 8, 96);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)), null, center, ring, ring);
        }

        var body = new RadialGradientBrush
        {
            Center = new Point(0.42 + eyeYaw * 0.04, 0.32 + eyePitch * 0.04),
            GradientOrigin = new Point(0.30 + eyeYaw * 0.05, 0.20 + eyePitch * 0.04),
            RadiusX = 0.74,
            RadiusY = 0.78
        };
        body.GradientStops.Add(new GradientStop(Color.FromRgb(255, 146, 156), 0.0));
        body.GradientStops.Add(new GradientStop(baseColor, 0.58));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(143, 28, 48), 1.0));

        dc.PushTransform(new ScaleTransform(1.0 + pulse * 0.022, 1.0 - morph * 0.040 + pulse * 0.010, center.X, center.Y));
        dc.DrawEllipse(body, new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1.4), center, radius, radius);

        if (animated)
        {
            var gaze = new Point(center.X + eyeYaw * radius * 0.34, center.Y + eyePitch * radius * 0.26);
            var aperture = Math.Max(0.12, 1.0 - morph * 0.78);
            var eyeWidth = radius * (0.42 + Math.Abs(eyeYaw) * 0.08);
            var eyeHeight = radius * 0.50 * aperture;
            var eyeBrush = new RadialGradientBrush(Color.FromArgb(246, 255, 255, 255), Color.FromArgb(216, 214, 229, 255))
            {
                Center = new Point(0.42, 0.34),
                GradientOrigin = new Point(0.28, 0.18)
            };
            dc.DrawEllipse(eyeBrush, null, gaze, eyeWidth, eyeHeight);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Point(gaze.X - eyeWidth * 0.24, gaze.Y - eyeHeight * 0.20), 3.0 + pulse * 1.5, Math.Max(1.0, 2.2 * aperture));
        }
        else
        {
            BasicBallRenderer.RenderCenterSymbol(dc, center, radius, state);
        }
        dc.Pop();

    }

    private static bool IsAnimatedEyeState(BallRuntimeState state)
    {
        return state.Mode == BallMode.Recording && state.EyeEnabled;
    }
}

public sealed class EyeOfCommitSkin : IBallSkin
{
    public string Id => "eye-of-commit";
    public string DisplayName => "Eye of Commit";

    public BallBubbleStyle GetBubbleStyle(BallRuntimeState state)
    {
        return state.Mode switch
        {
            BallMode.Recording => new BallBubbleStyle(
                Color.FromRgb(43, 20, 25),
                Color.FromArgb(136, 203, 63, 72),
                Color.FromRgb(255, 242, 238),
                10.0),
            _ => new BallBubbleStyle(
                Color.FromRgb(32, 28, 38),
                Color.FromArgb(116, 143, 92, 112),
                Color.FromRgb(247, 241, 244),
                10.0)
        };
    }

    public void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame)
    {
        var animated = state.Mode == BallMode.Recording && state.EyeEnabled;
        var pulse = animated ? Math.Clamp(frame.Pulse, 0.0, 1.0) : 0.0;
        var morph = animated ? Math.Clamp(frame.Morph, 0.0, 1.0) : 0.0;
        var eyeYaw = animated ? Math.Clamp(frame.EyeYaw, -1.25, 1.25) : 0.0;
        var eyePitch = animated ? Math.Clamp(frame.EyePitch, -1.0, 1.0) : 0.0;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.34;
        var assets = EyeOfCommitAssets.Instance;
        RenderImageBased(dc, center, radius, state, assets, assets.Config, animated, pulse, eyeYaw, eyePitch, morph);
    }

    private static void RenderImageBased(
        DrawingContext dc,
        Point center,
        double radius,
        BallRuntimeState state,
        EyeOfCommitAssets assets,
        EyeOfCommitConfig config,
        bool animated,
        double pulse,
        double eyeYaw,
        double eyePitch,
        double morph)
    {
        var tentacleFrame = assets.TentacleFrames[(int)Math.Floor(pulse * assets.TentacleFrames.Count) % assets.TentacleFrames.Count];
        dc.DrawImage(tentacleFrame, CenterRect(
            new Point(center.X + radius * config.TentacleOffsetX, center.Y + radius * config.TentacleOffsetY),
            radius * config.TentacleScale,
            radius * config.TentacleScale));

        var projection = EyeProjection.FromFrame(eyeYaw, eyePitch);

        var bodyCenter = new Point(center.X + radius * config.BodyOffsetX, center.Y + radius * config.BodyOffsetY);
        var bodyRect = CenterRect(bodyCenter, radius * config.BodyScaleX, radius * config.BodyScaleY);
        dc.DrawImage(assets.Body, bodyRect);

        var gaze = new Point(
            center.X + projection.Offset.X * radius * config.GazeOffsetX + radius * config.IrisOffsetX,
            center.Y + projection.Offset.Y * radius * config.GazeOffsetY + radius * config.IrisOffsetY);
        dc.PushClip(new EllipseGeometry(bodyCenter, radius * config.ClipRadiusX, radius * config.ClipRadiusY));
        DrawSphericalImage(
            dc,
            assets.Iris,
            CenterRect(gaze, radius * config.IrisScaleX, radius * config.IrisScaleY),
            gaze,
            projection,
            config.IrisProjectionStrength,
            1.0,
            1.0);
        dc.Pop();

        var pupilScaleX = animated ? 1.0 + morph * config.PupilMorphScaleX : 1.0;
        var pupilScaleY = animated ? Math.Max(config.PupilMorphMinY, 1.0 - morph * config.PupilMorphScaleY) : 1.0;
        var pupilCenter = new Point(gaze.X + radius * config.PupilOffsetX, gaze.Y + radius * config.PupilOffsetY);
        DrawSphericalImage(
            dc,
            assets.Pupil,
            CenterRect(pupilCenter, radius * config.PupilScaleX, radius * config.PupilScaleY),
            pupilCenter,
            projection,
            config.PupilProjectionStrength,
            pupilScaleX,
            pupilScaleY);

        if (state.Mode == BallMode.Idle)
        {
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(44, 50, 28, 42)),
                null,
                center,
                radius * 0.92,
                radius * 0.80);
        }

        if (!animated)
        {
            BasicBallRenderer.RenderCenterSymbol(dc, center, radius * 0.72, state);
        }
    }

    private static Rect CenterRect(Point center, double width, double height)
    {
        return new Rect(center.X - width / 2.0, center.Y - height / 2.0, width, height);
    }

    private static Rect OffsetRect(Rect rect, Vector offset)
    {
        return new Rect(rect.X + offset.X, rect.Y + offset.Y, rect.Width, rect.Height);
    }

    private static void DrawSphericalImage(
        DrawingContext dc,
        ImageSource image,
        Rect rect,
        Point pivot,
        EyeProjection projection,
        double strength,
        double extraScaleX,
        double extraScaleY)
    {
        var transform = new TransformGroup();
        var surfaceScale = 1.0 - (1.0 - projection.Foreshortening) * Math.Clamp(strength, 0.0, 1.0);
        transform.Children.Add(new RotateTransform(-projection.DirectionDegrees, pivot.X, pivot.Y));
        transform.Children.Add(new ScaleTransform(
            Math.Max(0.08, surfaceScale),
            1.0,
            pivot.X,
            pivot.Y));
        transform.Children.Add(new RotateTransform(projection.DirectionDegrees, pivot.X, pivot.Y));
        transform.Children.Add(new ScaleTransform(
            Math.Max(0.08, extraScaleX),
            Math.Max(0.08, extraScaleY),
            pivot.X,
            pivot.Y));
        dc.PushTransform(transform);
        dc.DrawImage(image, rect);
        dc.Pop();
    }

    private readonly record struct EyeProjection(Vector Offset, double Foreshortening, double DirectionDegrees)
    {
        public static EyeProjection FromFrame(double eyeYaw, double eyePitch)
        {
            var yawAngle = Math.Clamp(eyeYaw, -1.15, 1.15) * 0.58;
            var pitchAngle = Math.Clamp(eyePitch, -0.92, 0.92) * 0.58;
            var x = Math.Sin(yawAngle) * Math.Cos(pitchAngle);
            var y = Math.Sin(pitchAngle);
            var tilt = Math.Clamp(Math.Sqrt(yawAngle * yawAngle + pitchAngle * pitchAngle), 0.0, 1.05);
            var foreshortening = 0.72 + 0.28 * Math.Cos(tilt);
            var direction = Math.Sqrt(x * x + y * y) < 0.001
                ? 0.0
                : Math.Atan2(y, x) * 180.0 / Math.PI;
            return new EyeProjection(new Vector(x, y), foreshortening, direction);
        }
    }

    private sealed class EyeOfCommitAssets
    {
        private static readonly Lazy<EyeOfCommitAssets> LazyInstance = new(() => new EyeOfCommitAssets());
        private readonly string _configPath;
        private DateTime _configLastWriteUtc = DateTime.MinValue;
        private EyeOfCommitConfig _config = EyeOfCommitConfig.Default;

        private EyeOfCommitAssets()
        {
            var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Skins", "eye-of-commit");
            _configPath = Path.Combine(assetDir, "skin.json");
            Body = LoadPng(Path.Combine(assetDir, "body.png"));
            Iris = LoadPng(Path.Combine(assetDir, "iris.png"));
            Pupil = LoadPng(Path.Combine(assetDir, "pupil.png"));
            TentacleFrames = LoadGif(Path.Combine(assetDir, "tentacles.gif"));
            ReloadConfigIfNeeded();
        }

        public static EyeOfCommitAssets Instance => LazyInstance.Value;
        public ImageSource Body { get; }
        public ImageSource Iris { get; }
        public ImageSource Pupil { get; }
        public IReadOnlyList<ImageSource> TentacleFrames { get; }
        public EyeOfCommitConfig Config
        {
            get
            {
                ReloadConfigIfNeeded();
                return _config;
            }
        }

        private void ReloadConfigIfNeeded()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _config = EyeOfCommitConfig.Default;
                    return;
                }

                var lastWrite = File.GetLastWriteTimeUtc(_configPath);
                if (lastWrite == _configLastWriteUtc)
                {
                    return;
                }

                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<EyeOfCommitConfig>(json) ?? EyeOfCommitConfig.Default;
                _config = _config.Sanitize();
                _configLastWriteUtc = lastWrite;
            }
            catch
            {
                _config = EyeOfCommitConfig.Default;
            }
        }

        private static ImageSource LoadPng(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Eye of Commit skin asset is missing: {path}", path);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static IReadOnlyList<ImageSource> LoadGif(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Eye of Commit skin asset is missing: {path}", path);
            }

            var decoder = new GifBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frames = new List<ImageSource>();
            foreach (var frame in decoder.Frames)
            {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                converted.Freeze();
                frames.Add(converted);
            }
            if (frames.Count == 0)
            {
                throw new InvalidDataException($"Eye of Commit tentacle GIF has no frames: {path}");
            }
            return frames;
        }
    }

    private sealed record EyeOfCommitConfig
    {
        public double TentacleScale { get; init; } = 3.18;
        public double TentacleOffsetX { get; init; } = 0.0;
        public double TentacleOffsetY { get; init; } = 0.0;
        public double BodyScaleX { get; init; } = 2.46;
        public double BodyScaleY { get; init; } = 2.46;
        public double BodyOffsetX { get; init; } = 0.0;
        public double BodyOffsetY { get; init; } = 0.0;
        public double ClipRadiusX { get; init; } = 1.06;
        public double ClipRadiusY { get; init; } = 0.96;
        public double GazeOffsetX { get; init; } = 0.30;
        public double GazeOffsetY { get; init; } = 0.23;
        public double IrisScaleX { get; init; } = 2.18;
        public double IrisScaleY { get; init; } = 2.08;
        public double IrisOffsetX { get; init; } = 0.0;
        public double IrisOffsetY { get; init; } = 0.0;
        public double IrisProjectionStrength { get; init; } = 1.0;
        public double PupilScaleX { get; init; } = 0.54;
        public double PupilScaleY { get; init; } = 0.54;
        public double PupilOffsetX { get; init; } = 0.012;
        public double PupilOffsetY { get; init; } = -0.018;
        public double PupilProjectionStrength { get; init; } = 1.0;
        public double PupilMorphScaleX { get; init; } = 0.20;
        public double PupilMorphScaleY { get; init; } = 0.76;
        public double PupilMorphMinY { get; init; } = 0.20;

        public static EyeOfCommitConfig Default { get; } = new();

        public EyeOfCommitConfig Sanitize()
        {
            return this with
            {
                TentacleScale = Clamp(TentacleScale, 0.4, 8.0),
                BodyScaleX = Clamp(BodyScaleX, 0.4, 5.0),
                BodyScaleY = Clamp(BodyScaleY, 0.4, 5.0),
                ClipRadiusX = Clamp(ClipRadiusX, 0.2, 3.0),
                ClipRadiusY = Clamp(ClipRadiusY, 0.2, 3.0),
                IrisScaleX = Clamp(IrisScaleX, 0.2, 5.0),
                IrisScaleY = Clamp(IrisScaleY, 0.2, 5.0),
                IrisProjectionStrength = Clamp(IrisProjectionStrength, 0.0, 2.0),
                PupilScaleX = Clamp(PupilScaleX, 0.05, 3.0),
                PupilScaleY = Clamp(PupilScaleY, 0.05, 3.0),
                PupilProjectionStrength = Clamp(PupilProjectionStrength, 0.0, 2.0),
                PupilMorphScaleX = Clamp(PupilMorphScaleX, 0.0, 2.0),
                PupilMorphScaleY = Clamp(PupilMorphScaleY, 0.0, 2.0),
                PupilMorphMinY = Clamp(PupilMorphMinY, 0.05, 1.0)
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return min;
            return Math.Clamp(value, min, max);
        }
    }
}

public sealed class ClassicSkin : IBallSkin
{
    public string Id => "classic";
    public string DisplayName => "Classic";

    public BallBubbleStyle GetBubbleStyle(BallRuntimeState state)
    {
        return state.Mode switch
        {
            BallMode.Recording => new BallBubbleStyle(
                Color.FromRgb(47, 27, 31),
                Color.FromArgb(92, 255, 255, 255),
                Colors.White,
                9.0),
            _ => BasicBallRenderer.DefaultBubbleStyle
        };
    }

    public void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame)
    {
        var animated = state.Mode == BallMode.Recording && state.EyeEnabled;
        var pulse = animated ? Math.Clamp(frame.Pulse, 0.0, 1.0) : 0.0;
        var morph = animated ? Math.Clamp(frame.Morph, 0.0, 1.0) : 0.0;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.34;
        var color = state.Mode switch
        {
            BallMode.Recording => Color.FromRgb(239, 68, 68),
            _ => Color.FromRgb(59, 130, 246)
        };

        var glowAlpha = (byte)Math.Clamp(18 + pulse * 70, 18, 88);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(glowAlpha, color.R, color.G, color.B)), null, center, radius * (1.12 + pulse * 0.16), radius * (1.12 + pulse * 0.16));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, new Point(center.X + 2, center.Y + 5), radius * 1.04, radius * 1.04);

        dc.PushTransform(new ScaleTransform(1.0 + pulse * 0.020, 1.0 - morph * 0.040 + pulse * 0.010, center.X, center.Y));
        dc.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 2), center, radius, radius);
        BasicBallRenderer.RenderCenterSymbol(dc, center, radius, state);
        dc.Pop();

    }
}
