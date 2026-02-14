using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmodAddonManager.Core.Models;
using GmodAddonManager.UI.Services;
using System;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetImageEditDialog : Window
{
    private Bitmap? sourceBitmap;
    private MatrixTransform? imageTransform;

    private bool isDragging;
    private Point dragStart;
    private double dragStartOffsetX;
    private double dragStartOffsetY;
    private double offsetX;
    private double offsetY;
    private double zoom = 1;
    private double baseScale = 1;
    private double pixelPerDipX = 1;
    private double pixelPerDipY = 1;
    private bool pendingInitialTransform;
    private bool suppressZoomChange;
    private bool isClosed;

    private const double WheelZoomStep = 0.12;

    public AssetImageEditDialog()
    {
        InitializeComponent();
        InitializeDialog();
    }

    public AssetImageEditDialog(string imagePath)
    {
        InitializeComponent();
        InitializeDialog();
        _ = LoadImageAsync(imagePath);
    }

    private void InitializeDialog()
    {
        ResolveImageTransforms();

        ZoomSlider.PropertyChanged += OnZoomChanged;
        CropViewport.AttachedToVisualTree += OnCropViewportAttachedToVisualTree;
        CropViewport.SizeChanged += OnCropViewportSizeChanged;
        CropViewport.LayoutUpdated += OnCropViewportLayoutUpdated;

        UpdateImageControls();
    }

    private void ResolveImageTransforms()
    {
        if (CropImage.RenderTransform is MatrixTransform matrixTransform)
        {
            imageTransform = matrixTransform;
            return;
        }

        imageTransform = new MatrixTransform();
        CropImage.RenderTransform = imageTransform;
    }

    private void UpdateImageControls()
    {
        var hasImage = sourceBitmap != null;
        CropImage.Source = sourceBitmap;
        CropImage.IsVisible = hasImage;
        NoImageText.IsVisible = !hasImage;
        ZoomSlider.IsEnabled = hasImage;
        ResetButton.IsEnabled = hasImage;
        SaveButton.IsEnabled = hasImage;
    }

    private async Task LoadImageAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() => new Bitmap(path));
            if (isClosed)
            {
                bitmap.Dispose();
                return;
            }

            sourceBitmap?.Dispose();
            sourceBitmap = bitmap;

            UpdatePixelScale();

            offsetX = 0;
            offsetY = 0;
            SetZoom(1);
            UpdateImageControls();
            ScheduleInitialTransform();
        }
        catch (Exception ex)
        {
            if (isClosed)
            {
                return;
            }

            SafeFileLogger.TryLogException("AssetImageEditDialog.LoadImageAsync", ex);
            var dialogService = new DialogService();
            await dialogService.ShowErrorAsync(
                L.Get("Error.Title"),
                L.Get("Error.AssetImageLoadFailed"));
        }
    }

    private void ResetView()
    {
        offsetX = 0;
        offsetY = 0;
        SetZoom(1);
        UpdateImageTransform();
    }

    private void ScheduleInitialTransform()
    {
        pendingInitialTransform = true;
        CropImage.InvalidateMeasure();
        CropViewport.InvalidateMeasure();
        CropViewport.InvalidateVisual();
        Dispatcher.UIThread.Post(ApplyInitialTransform, DispatcherPriority.Render);
    }

    private void ApplyInitialTransform()
    {
        if (!pendingInitialTransform || sourceBitmap == null)
        {
            return;
        }

        if (!TryGetViewportMetrics(out _, out _, out _))
        {
            Dispatcher.UIThread.Post(ApplyInitialTransform, DispatcherPriority.Render);
            return;
        }

        pendingInitialTransform = false;
        ResetView();
    }

    private void UpdateImageTransform()
    {
        if (!TryGetViewportMetrics(out var viewportSize, out var imageWidthDip, out var imageHeightDip))
        {
            return;
        }

        var scaleX = viewportSize / imageWidthDip;
        var scaleY = viewportSize / imageHeightDip;
        baseScale = Math.Min(scaleX, scaleY);
        var scale = baseScale * zoom;

        var scaledWidth = imageWidthDip * scale;
        var scaledHeight = imageHeightDip * scale;
        var centerX = (viewportSize - scaledWidth) / 2;
        var centerY = (viewportSize - scaledHeight) / 2;

        ClampOffsets(viewportSize, scaledWidth, scaledHeight);

        if (imageTransform == null)
        {
            return;
        }

        imageTransform.Matrix = new Matrix(scale, 0, 0, scale, centerX + offsetX, centerY + offsetY);
    }

    private void ClampOffsets(double viewportSize, double scaledWidth, double scaledHeight)
    {
        var maxOffsetX = Math.Max(0, (scaledWidth - viewportSize) / 2);
        var maxOffsetY = Math.Max(0, (scaledHeight - viewportSize) / 2);
        offsetX = Math.Clamp(offsetX, -maxOffsetX, maxOffsetX);
        offsetY = Math.Clamp(offsetY, -maxOffsetY, maxOffsetY);
    }

    private AssetImageCrop? BuildCrop()
    {
        if (sourceBitmap == null)
        {
            return null;
        }

        if (!TryGetViewportMetrics(out var viewportSize, out var imageWidthDip, out var imageHeightDip))
        {
            return null;
        }

        var scale = baseScale * zoom;
        var scaledWidth = imageWidthDip * scale;
        var scaledHeight = imageHeightDip * scale;
        var centerX = (viewportSize - scaledWidth) / 2 + offsetX;
        var centerY = (viewportSize - scaledHeight) / 2 + offsetY;

        var xDip = (0 - centerX) / scale;
        var yDip = (0 - centerY) / scale;
        var cropSizeDip = viewportSize / scale;

        var xPx = xDip * pixelPerDipX;
        var yPx = yDip * pixelPerDipY;
        var sizePxX = cropSizeDip * pixelPerDipX;
        var sizePxY = cropSizeDip * pixelPerDipY;
        var sizePx = Math.Min(sizePxX, sizePxY);

        var widthPx = sourceBitmap.PixelSize.Width;
        var heightPx = sourceBitmap.PixelSize.Height;

        if (sizePx <= 0)
        {
            return null;
        }

        if (xPx < 0) xPx = 0;
        if (yPx < 0) yPx = 0;
        if (xPx + sizePx > widthPx) xPx = widthPx - sizePx;
        if (yPx + sizePx > heightPx) yPx = heightPx - sizePx;

        var xNorm = widthPx > 0 ? xPx / widthPx : 0;
        var yNorm = heightPx > 0 ? yPx / heightPx : 0;
        var wNorm = widthPx > 0 ? sizePx / widthPx : 1;
        var hNorm = heightPx > 0 ? sizePx / heightPx : 1;

        return new AssetImageCrop
        {
            X = Math.Clamp(xNorm, 0, 1),
            Y = Math.Clamp(yNorm, 0, 1),
            Width = Math.Clamp(wNorm, 0, 1),
            Height = Math.Clamp(hNorm, 0, 1)
        };
    }

    private void OnZoomChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || suppressZoomChange)
        {
            return;
        }

        var newZoom = ZoomSlider.Value;
        if (Math.Abs(newZoom - zoom) < double.Epsilon)
        {
            return;
        }

        if (!TryGetViewportMetrics(out var viewportSize, out var imageWidthDip, out var imageHeightDip))
        {
            zoom = newZoom;
            return;
        }

        var scaleX = viewportSize / imageWidthDip;
        var scaleY = viewportSize / imageHeightDip;
        var currentBaseScale = Math.Min(scaleX, scaleY);
        var oldScale = currentBaseScale * zoom;
        var newScale = currentBaseScale * newZoom;

        if (oldScale <= 0 || newScale <= 0)
        {
            zoom = newZoom;
            UpdateImageTransform();
            return;
        }

        var scaledWidth = imageWidthDip * oldScale;
        var scaledHeight = imageHeightDip * oldScale;
        var centerX = (viewportSize - scaledWidth) / 2;
        var centerY = (viewportSize - scaledHeight) / 2;
        var viewportCenter = viewportSize / 2;

        var focusX = (viewportCenter - centerX - offsetX) / oldScale;
        var focusY = (viewportCenter - centerY - offsetY) / oldScale;

        zoom = newZoom;

        var newScaledWidth = imageWidthDip * newScale;
        var newScaledHeight = imageHeightDip * newScale;
        var newCenterX = (viewportSize - newScaledWidth) / 2;
        var newCenterY = (viewportSize - newScaledHeight) / 2;

        offsetX = viewportCenter - newCenterX - (focusX * newScale);
        offsetY = viewportCenter - newCenterY - (focusY * newScale);

        UpdateImageTransform();
    }

    private void OnCropViewportLayoutUpdated(object? sender, EventArgs e)
    {
        ApplyInitialTransform();
    }

    private void OnCropViewportAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UpdateImageTransform();
    }

    private void OnCropViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateImageTransform();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void OnCropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sourceBitmap == null)
        {
            return;
        }

        isDragging = true;
        dragStart = e.GetPosition(CropViewport);
        dragStartOffsetX = offsetX;
        dragStartOffsetY = offsetY;
        e.Pointer.Capture(CropViewport);
    }

    private void OnCropPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDragging || sourceBitmap == null)
        {
            return;
        }

        var current = e.GetPosition(CropViewport);
        offsetX = dragStartOffsetX + (current.X - dragStart.X);
        offsetY = dragStartOffsetY + (current.Y - dragStart.Y);
        UpdateImageTransform();
    }

    private void OnCropPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        isDragging = false;
        e.Pointer.Capture(null);
    }

    private void OnCropPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sourceBitmap == null)
        {
            return;
        }

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        var target = ZoomSlider.Value + (delta * WheelZoomStep);
        var clamped = Math.Clamp(target, ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(clamped - ZoomSlider.Value) > double.Epsilon)
        {
            ZoomSlider.Value = clamped;
        }

        e.Handled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var result = new ImageEditResult
        {
            IsSaved = true,
            Crop = BuildCrop()
        };

        Close(result);
    }

    protected override void OnClosed(EventArgs e)
    {
        isClosed = true;
        ZoomSlider.PropertyChanged -= OnZoomChanged;
        CropViewport.AttachedToVisualTree -= OnCropViewportAttachedToVisualTree;
        CropViewport.SizeChanged -= OnCropViewportSizeChanged;
        CropViewport.LayoutUpdated -= OnCropViewportLayoutUpdated;
        pendingInitialTransform = false;
        sourceBitmap?.Dispose();
        sourceBitmap = null;
        base.OnClosed(e);
    }

    private void SetZoom(double value)
    {
        suppressZoomChange = true;
        zoom = value;
        ZoomSlider.Value = value;
        suppressZoomChange = false;
    }

    private bool TryGetViewportMetrics(out double viewportSize, out double imageWidthDip, out double imageHeightDip)
    {
        viewportSize = GetViewportSize();
        imageWidthDip = 0;
        imageHeightDip = 0;

        if (sourceBitmap == null || viewportSize <= 0)
        {
            return false;
        }

        var bitmapSize = sourceBitmap.Size;
        if (bitmapSize.Width > 0 && bitmapSize.Height > 0)
        {
            imageWidthDip = bitmapSize.Width;
            imageHeightDip = bitmapSize.Height;
        }
        else
        {
            imageWidthDip = sourceBitmap.PixelSize.Width / pixelPerDipX;
            imageHeightDip = sourceBitmap.PixelSize.Height / pixelPerDipY;
        }

        return imageWidthDip > 0 && imageHeightDip > 0;
    }

    private void UpdatePixelScale()
    {
        if (sourceBitmap == null)
        {
            pixelPerDipX = 1;
            pixelPerDipY = 1;
            return;
        }

        var size = sourceBitmap.Size;
        if (size.Width > 0 && size.Height > 0)
        {
            pixelPerDipX = sourceBitmap.PixelSize.Width / size.Width;
            pixelPerDipY = sourceBitmap.PixelSize.Height / size.Height;
            if (pixelPerDipX <= 0) pixelPerDipX = 1;
            if (pixelPerDipY <= 0) pixelPerDipY = 1;
            return;
        }

        var dpiX = sourceBitmap.Dpi.X / 96.0;
        var dpiY = sourceBitmap.Dpi.Y / 96.0;
        pixelPerDipX = dpiX > 0 ? dpiX : 1;
        pixelPerDipY = dpiY > 0 ? dpiY : 1;
    }

    private double GetViewportSize()
    {
        var width = CropViewport.Bounds.Width;
        var height = CropViewport.Bounds.Height;
        var border = CropViewport.BorderThickness;
        var innerWidth = Math.Max(0, width - border.Left - border.Right);
        var innerHeight = Math.Max(0, height - border.Top - border.Bottom);
        return Math.Min(innerWidth, innerHeight);
    }
}

public class ImageEditResult
{
    public bool IsSaved { get; set; }
    public AssetImageCrop? Crop { get; set; }
}
