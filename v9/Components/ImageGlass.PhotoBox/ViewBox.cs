﻿/*
ImageGlass Project - Image viewer for Windows
Copyright (C) 2010 - 2022 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using ImageGlass.Base.HybridGraphics;
using ImageGlass.Base.PhotoBox;
using ImageGlass.Base.WinApi;
using ImageGlass.PhotoBox.Animator;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using unvell.D2DLib;

namespace ImageGlass.PhotoBox;


/// <summary>
/// Modern photo view box with hardware-accelerated
/// </summary>
public partial class ViewBox : HybridControl
{
    private Bitmap? _gdiBitmap;
    private D2DBitmap? _d2dBitmap;
    private CancellationTokenSource? _msgTokenSrc;


    /// <summary>
    /// Gets the area of the image content to draw
    /// </summary>
    private D2DRect _srcRect = new(0, 0, 0, 0);

    /// <summary>
    /// Image viewport
    /// </summary>
    private D2DRect _destRect = new(0, 0, 0, 0);

    private Vector2 _panHostPoint;
    private Vector2 _panSpeed;
    private Vector2 _panHostStartPoint;

    private bool _xOut = false;
    private bool _yOut = false;
    private bool _isMouseDown = false;
    private Vector2 _drawPoint = new();

    // current zoom, minimum zoom, maximum zoom, previous zoom (bigger means zoom in)
    private float _zoomFactor = 1f;
    private float _oldZoomFactor = 1f;
    private bool _isManualZoom = false;
    private ZoomMode _zoomMode = ZoomMode.AutoZoom;
    private Base.PhotoBox.InterpolationMode _interpolationMode = Base.PhotoBox.InterpolationMode.NearestNeighbor;

    private CheckerboardMode _checkerboardMode = CheckerboardMode.None;
    private IAnimator _animator;
    private AnimationSource _animationSource = AnimationSource.Default;
    private bool _useHardwareAccelerationBackup = true;
    private bool _shouldRecalculateDrawingRegion = true;

    // Navigation buttons
    private const float NAV_PADDING = 20f;
    private bool _isNavLeftHovered = false;
    private bool _isNavLeftPressed = false;
    private bool _isNavRightHovered = false;
    private bool _isNavRightPressed = false;
    private PointF _navLeftPos => new(NavButtonRadius + NAV_PADDING, Height / 2);
    private PointF _navRightPos => new(Width - NavButtonRadius - NAV_PADDING, Height / 2);
    private NavButtonDisplay _navDisplay = NavButtonDisplay.None;
    private bool _isNavVisible = false; // to reduce drawing Nav when the cursor is out of nav regions



    #region Public properties

    /// <summary>
    /// Checks if the bitmap image has alpha pixels
    /// </summary>
    [Browsable(false)]
    public bool HasAlphaPixels => _gdiBitmap is not null && _gdiBitmap.PixelFormat.HasFlag(PixelFormat.Alpha);


    /// <summary>
    /// Gets image viewport
    /// </summary>
    [Browsable(false)]
    public RectangleF ImageViewport => new(_destRect.Location, _destRect.Size);


    /// <summary>
    /// Gets the center point of image viewport
    /// </summary>
    [Browsable(false)]
    public PointF ImageViewportCenterPoint => new()
    {
        X = ImageViewport.X + ImageViewport.Width / 2,
        Y = ImageViewport.Y + ImageViewport.Height / 2,
    };


    // Animation
    #region Animation

    /// <summary>
    /// Gets, sets the value indicates whether it's animating
    /// </summary>
    [Browsable(false)]
    public bool IsAnimating { get; protected set; } = false;

    /// <summary>
    /// Gets, sets the value indicates whether the image can animate
    /// </summary>
    [Browsable(false)]
    public bool CanAnimate
    {
        get
        {
            if (_gdiBitmap is null) return false;

            return _animator.CanAnimate(_gdiBitmap);
        }
    }

    #endregion


    // Zooming
    #region Zooming

    /// <summary>
    /// Gets, sets the minimum zoom factor (<c>100% = 1.0f</c>)
    /// </summary>
    [Category("Zooming")]
    [DefaultValue(0.01f)]
    public float MinZoom { get; set; } = 0.01f;

    /// <summary>
    /// Gets, sets the maximum zoom factor (<c>100% = 1.0f</c>)
    /// </summary>
    [Category("Zooming")]
    [DefaultValue(40.0f)]
    public float MaxZoom { get; set; } = 40f;

    /// <summary>
    /// Gets, sets current zoom factor (<c>100% = 1.0f</c>)
    /// </summary>
    [Category("Zooming")]
    [DefaultValue(1.0f)]
    public float ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            if (_zoomFactor != value)
            {
                _zoomFactor = value;
                _isManualZoom = true;

                Invalidate();
            }
        }
    }

    /// <summary>
    /// Gets, sets zoom mode
    /// </summary>
    [Category("Zooming")]
    [DefaultValue(ZoomMode.AutoZoom)]
    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set
        {
            if (_zoomMode != value)
            {
                _zoomMode = value;
                Refresh();
            }
        }
    }

    /// <summary>
    /// Gets, sets interpolation mode
    /// </summary>
    [Category("Zooming")]
    [DefaultValue(Base.PhotoBox.InterpolationMode.NearestNeighbor)]
    public Base.PhotoBox.InterpolationMode InterpolationMode
    {
        get => _interpolationMode;
        set
        {
            if (_interpolationMode != value)
            {
                _interpolationMode = value;
                Invalidate();
            }
        }
    }


    /// <summary>
    /// Occurs when <see cref="ZoomFactor"/> value changes.
    /// </summary>
    [Category("NavigationButtons")]
    public event ZoomChangedEventHandler? OnZoomChanged = null;
    public delegate void ZoomChangedEventHandler(ZoomEventArgs e);

    #endregion


    // Checkerboard
    #region Checkerboard

    [Category("Checkerboard")]
    [DefaultValue(CheckerboardMode.None)]
    public CheckerboardMode CheckerboardMode
    {
        get => _checkerboardMode;
        set
        {
            if (_checkerboardMode != value)
            {
                _checkerboardMode = value;
                Invalidate();
            }
        }
    }

    [Category("Checkerboard")]
    [DefaultValue(typeof(float), "12")]
    public float CheckerboardCellSize { get; set; } = 12f;

    [Category("Checkerboard")]
    [DefaultValue(typeof(Color), "25, 0, 0, 0")]
    public Color CheckerboardColor1 { get; set; } = Color.FromArgb(25, Color.Black);

    [Category("Checkerboard")]
    [DefaultValue(typeof(Color), "25, 255, 255, 255")]
    public Color CheckerboardColor2 { get; set; } = Color.FromArgb(25, Color.White);

    #endregion


    // Navigation Buttons
    #region Navigation Buttons

    [Category("NavigationButtons")]
    [DefaultValue(NavButtonDisplay.None)]
    public NavButtonDisplay NavDisplay
    {
        get => _navDisplay;
        set
        {
            if (_navDisplay != value)
            {
                _navDisplay = value;
                Invalidate();
            }
        }
    }

    [Category("NavigationButtons")]
    [DefaultValue(50f)]
    public float NavButtonRadius { get; set; } = 50f;

    [Category("NavigationButtons")]
    [DefaultValue(typeof(Color), "150, 0, 0, 0")]
    public Color NavHoveredColor { get; set; } = Color.FromArgb(150, Color.Black);

    [Category("NavigationButtons")]
    [DefaultValue(typeof(Color), "120, 0, 0, 0")]
    public Color NavPressedColor { get; set; } = Color.FromArgb(120, Color.Black);

    // Left button
    [Category("NavigationButtons")]
    [DefaultValue(typeof(Bitmap), null)]
    public Bitmap? NavLeftImage { get; set; }

    // Right button
    [Category("NavigationButtons")]
    [DefaultValue(typeof(Bitmap), null)]
    public Bitmap? NavRightImage { get; set; }


    /// <summary>
    /// Occurs when the left navigation button clicked.
    /// </summary>
    [Category("NavigationButtons")]
    public event NavLeftClickedEventHandler? OnNavLeftClicked = null;
    public delegate void NavLeftClickedEventHandler(MouseEventArgs e);


    /// <summary>
    /// Occurs when the right navigation button clicked.
    /// </summary>
    [Category("NavigationButtons")]
    public event NavRightClickedEventHandler? OnNavRightClicked = null;
    public delegate void NavRightClickedEventHandler(MouseEventArgs e);

    #endregion


    // Events
    #region Events

    /// <summary>
    /// Occurs when the host is being panned
    /// </summary>
    public event PanningEventHandler? OnPanning;
    public delegate void PanningEventHandler(PanningEventArgs e);


    /// <summary>
    /// Occurs when the image is changed
    /// </summary>
    public event ImageChangedEventHandler? OnImageChanged;
    public delegate void ImageChangedEventHandler(EventArgs e);


    /// <summary>
    /// Occurs when the mouse pointer is moved over the control
    /// </summary>
    public event ImageMouseMoveEventHandler? OnImageMouseMove;
    public delegate void ImageMouseMoveEventHandler(ImageMouseMoveEventArgs e);


    #endregion


    #endregion


    public ViewBox()
    {
        // request for high resolution gif animation
        if (!TimerApi.HasRequestedRateAtLeastAsFastAs(10) && TimerApi.TimeBeginPeriod(10))
        {
            HighResolutionGifAnimator.SetTickTimeInMilliseconds(10);
        }

        _animator = new HighResolutionGifAnimator();
    }

    protected override void OnLoaded()
    {
        base.OnLoaded();

        // back up value
        _useHardwareAccelerationBackup = UseHardwareAcceleration;

        // draw the control
        Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _d2dBitmap?.Dispose();
        _gdiBitmap?.Dispose();

        NavLeftImage?.Dispose();
        NavRightImage?.Dispose();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsReady) return;

        var requestRerender = false;

        // Navigation clickable check
        #region Navigation clickable check
        if (e.Button == MouseButtons.Left)
        {
            if (NavDisplay == NavButtonDisplay.Left
                || NavDisplay == NavButtonDisplay.Both)
            {
                // left clickable region
                var leftClickable = new RectangleF(
                _navLeftPos.X - NavButtonRadius,
                _navLeftPos.Y - NavButtonRadius,
                NavButtonRadius * 2,
                NavButtonRadius * 2);

                // calculate whether the point inside the rect
                _isNavLeftPressed = leftClickable.Contains(e.Location);
            }


            if (NavDisplay == NavButtonDisplay.Right
                || NavDisplay == NavButtonDisplay.Both)
            {
                // right clickable region
                var rightClickable = new RectangleF(
                _navRightPos.X - NavButtonRadius,
                _navRightPos.Y - NavButtonRadius,
                NavButtonRadius * 2,
                NavButtonRadius * 2);

                // calculate whether the point inside the rect
                _isNavRightPressed = rightClickable.Contains(e.Location);
            }

            requestRerender = _isNavLeftPressed || _isNavRightPressed;
        }
        #endregion


        // Image panning check
        #region Image panning check
        if (_d2dBitmap is not null)
        {
            _panHostPoint.X = e.Location.X;
            _panHostPoint.Y = e.Location.Y;
            _panSpeed.X = 0;
            _panSpeed.Y = 0;
            _panHostStartPoint.X = e.Location.X;
            _panHostStartPoint.Y = e.Location.Y;
        }
        #endregion


        _isMouseDown = true;
        if (requestRerender)
        {
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsReady) return;


        // Navigation clickable check
        #region Navigation clickable check
        if (e.Button == MouseButtons.Left)
        {
            if (_isNavLeftPressed)
            {
                // left clickable region
                var leftClickable = new RectangleF(
                    _navLeftPos.X - NavButtonRadius,
                    _navLeftPos.Y - NavButtonRadius,
                    NavButtonRadius * 2,
                    NavButtonRadius * 2);

                // emit nav button event if the point inside the rect
                if (leftClickable.Contains(e.Location))
                {
                    OnNavLeftClicked?.Invoke(e);
                }
            }
            else if (_isNavRightPressed)
            {
                // right clickable region
                var rightClickable = new RectangleF(
                    _navRightPos.X - NavButtonRadius,
                    _navRightPos.Y - NavButtonRadius,
                    NavButtonRadius * 2,
                    NavButtonRadius * 2);

                // emit nav button event if the point inside the rect
                if (rightClickable.Contains(e.Location))
                {
                    OnNavRightClicked?.Invoke(e);
                }
            }
        }

        _isNavLeftPressed = false;
        _isNavRightPressed = false;
        #endregion


        _isMouseDown = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsReady) return;

        var requestRerender = false;


        // Navigation hoverable check
        #region Navigation hoverable check
        // no button pressed
        if (e.Button == MouseButtons.None)
        {
            // left hoverable region
            if (NavDisplay == NavButtonDisplay.Left
                || NavDisplay == NavButtonDisplay.Both)
            {
                var leftHoverable = new RectangleF(
                _navLeftPos.X - NavButtonRadius - NAV_PADDING,
                _navLeftPos.Y - NavButtonRadius * 3,
                NavButtonRadius * 2 + NAV_PADDING,
                NavButtonRadius * 6);

                // calculate whether the point inside the rect
                _isNavLeftHovered = leftHoverable.Contains(e.Location);
            }

            // right hoverable region
            if (NavDisplay == NavButtonDisplay.Right
                || NavDisplay == NavButtonDisplay.Both)
            {
                var rightHoverable = new RectangleF(
                _navRightPos.X - NavButtonRadius,
                _navRightPos.Y - NavButtonRadius * 3,
                NavButtonRadius * 2 + NAV_PADDING,
                NavButtonRadius * 6);

                // calculate whether the point inside the rect
                _isNavRightHovered = rightHoverable.Contains(e.Location);
            }

            if (!_isNavLeftHovered && !_isNavRightHovered && _isNavVisible)
            {
                requestRerender = true;
                _isNavVisible = false;
            }
            else
            {
                requestRerender = _isNavVisible = _isNavLeftHovered || _isNavRightHovered;
            }
        }
        #endregion


        // Image panning check
        if (_isMouseDown)
        {
            requestRerender = PanTo(
                _panHostPoint.X - e.Location.X,
                _panHostPoint.Y - e.Location.Y,
                false);
        }


        // emit event OnImageMouseMove
        var imgX = (e.X - _destRect.X) / _zoomFactor + _srcRect.X;
        var imgY = (e.Y - _destRect.Y) / _zoomFactor + _srcRect.Y;
        OnImageMouseMove?.Invoke(new(imgX, imgY, e.Button));


        // request re-render control
        if (requestRerender)
        {
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        _isNavLeftHovered = false;
        _isNavRightHovered = false;


        if (_isNavVisible)
        {
            _isNavVisible = false;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!IsReady || _d2dBitmap is null || e.Delta == 0) return;

        _ = ZoomToPoint(e.Delta, e.Location);
    }

    protected override void OnResize(EventArgs e)
    {
        _shouldRecalculateDrawingRegion = true;

        // redraw the control on resizing if it's not manual zoom
        if (IsReady && _d2dBitmap is not null && !_isManualZoom)
        {
            Refresh();
        }

        base.OnResize(e);
    }

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        
        if (e.KeyCode == Keys.Right)
        {
            _animationSource = AnimationSource.PanRight;
        }
        else if (e.KeyCode == Keys.Left)
        {
            _animationSource = AnimationSource.PanLeft;
        }
        else if (e.KeyCode == Keys.Up)
        {
            _animationSource = AnimationSource.PanUp;
        }
        else if (e.KeyCode == Keys.Down)
        {
            _animationSource = AnimationSource.PanDown;
        }
        else if (e.KeyCode == Keys.Oemplus)
        {
            _animationSource = AnimationSource.ZoomIn;
        }
        else if (e.KeyCode == Keys.OemMinus)
        {
            _animationSource = AnimationSource.ZoomOut;
        }

        EnableAnimation = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        EnableAnimation = false;
    }


    protected override void OnFrame()
    {
        base.OnFrame();


        // Panning
        if (_animationSource.HasFlag(AnimationSource.PanLeft))
        {
            _ = PanTo(-20, 0, requestRerender: false);
        }
        else if (_animationSource.HasFlag(AnimationSource.PanRight))
        {
            _ = PanTo(20, 0, requestRerender: false);
        }

        if (_animationSource.HasFlag(AnimationSource.PanUp))
        {
            _ = PanTo(0, -20, requestRerender: false);
        }
        else if (_animationSource.HasFlag(AnimationSource.PanDown))
        {
            _ = PanTo(0, 20, requestRerender: false);
        }

        // Zooming
        if (_animationSource.HasFlag(AnimationSource.ZoomIn))
        {
            _ = ZoomToPoint(20, requestRerender: false);
        }
        else if (_animationSource.HasFlag(AnimationSource.ZoomOut))
        {
            _ = ZoomToPoint(-20, requestRerender: false);
        }

    }


    protected override void OnRender(IHybridGraphics g)
    {
        base.OnRender(g);


        // update drawing regions
        CalculateDrawingRegion();

        // checkerboard background
        DrawCheckerboardLayer(g);


        if (CanAnimate)
        {
            DrawGifFrame(g);
        }
        else
        {
            // image layer
            DrawImageLayer(g);
        }

        // text message
        DrawTextLayer(g);

        // navigation layer
        DrawNavigationLayer(g);
    }


    protected virtual void DrawGifFrame(IHybridGraphics hg)
    {
        if (_gdiBitmap is null) return;

        // use GDI+ to handle GIF animation
        var g = hg as GDIGraphics;
        if (g is null) return;

        g.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
        g.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        try
        {
            if (IsAnimating && !DesignMode)
            {
                _animator.UpdateFrames(_gdiBitmap);
            }

            g.DrawImage(_gdiBitmap, _destRect, _srcRect, 1, (int)_interpolationMode);
        }
        catch (ArgumentException)
        {
            // ignore errors that occur due to the image being disposed
        }
        catch (OutOfMemoryException)
        {
            // also ignore errors that occur due to running out of memory
        }
        catch (ExternalException)
        {
            // stop the animation and reset to the first frame.
            IsAnimating = false;
            _animator.StopAnimate(_gdiBitmap, OnFrameChangedHandler);
        }
        catch (InvalidOperationException)
        {
            // issue #373: a race condition caused this exception: deleting the image from underneath us could
            // cause a collision in HighResolutionGif_animator. I've not been able to repro; hopefully this is
            // the correct response.

            // stop the animation and reset to the first frame.
            IsAnimating = false;
            _animator.StopAnimate(_gdiBitmap, OnFrameChangedHandler);
        }

        g.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
    }


    protected virtual void CalculateDrawingRegion()
    {
        if (_d2dBitmap is null || _shouldRecalculateDrawingRegion is false) return;

        var zoomX = _drawPoint.X;
        var zoomY = _drawPoint.Y;

        _xOut = false;
        _yOut = false;

        var clientW = Width;
        var clientH = Height;

        if (clientW > _d2dBitmap.Width * _zoomFactor)
        {
            _srcRect.X = 0;
            _srcRect.Width = _d2dBitmap.Width;
            _destRect.X = (clientW - _d2dBitmap.Width * _zoomFactor) / 2.0f;
            _destRect.Width = _d2dBitmap.Width * _zoomFactor;
        }
        else
        {
            _srcRect.X += (clientW / _oldZoomFactor - clientW / _zoomFactor) / ((clientW + 0.001f) / zoomX);
            _srcRect.Width = clientW / _zoomFactor;
            _destRect.X = 0;
            _destRect.Width = clientW;
        }


        if (clientH > _d2dBitmap.Height * _zoomFactor)
        {
            _srcRect.Y = 0;
            _srcRect.Height = _d2dBitmap.Height;
            _destRect.Y = (clientH - _d2dBitmap.Height * _zoomFactor) / 2f;
            _destRect.Height = _d2dBitmap.Height * _zoomFactor;
        }
        else
        {
            _srcRect.Y += (clientH / _oldZoomFactor - clientH / _zoomFactor) / ((clientH + 0.001f) / zoomY);
            _srcRect.Height = clientH / _zoomFactor;
            _destRect.Y = 0;
            _destRect.Height = clientH;
        }

        _oldZoomFactor = _zoomFactor;
        //------------------------

        if (_srcRect.X + _srcRect.Width > _d2dBitmap.Width)
        {
            _xOut = true;
            _srcRect.X = _d2dBitmap.Width - _srcRect.Width;
        }

        if (_srcRect.X < 0)
        {
            _xOut = true;
            _srcRect.X = 0;
        }

        if (_srcRect.Y + _srcRect.Height > _d2dBitmap.Height)
        {
            _yOut = true;
            _srcRect.Y = _d2dBitmap.Height - _srcRect.Height;
        }

        if (_srcRect.Y < 0)
        {
            _yOut = true;
            _srcRect.Y = 0;
        }

        _shouldRecalculateDrawingRegion = false;
    }


    protected virtual void DrawImageLayer(IHybridGraphics g)
    {
        if (_d2dBitmap is not null && UseHardwareAcceleration)
        {
            var d2dg = g as Direct2DGraphics;
            d2dg?.DrawImage(_d2dBitmap, _destRect, _srcRect, 1f, (int)_interpolationMode);
        }
        else if (_gdiBitmap is not null)
        {
            g.DrawImage(_gdiBitmap, _destRect, _srcRect, 1f, (int)_interpolationMode);
        }
    }


    protected virtual void DrawCheckerboardLayer(IHybridGraphics g)
    {
        if (CheckerboardMode == CheckerboardMode.None) return;

        // region to draw
        Rectangle region;

        if (CheckerboardMode == CheckerboardMode.Image)
        {
            // no need to draw checkerboard if image does not has alpha pixels
            if (!HasAlphaPixels) return;

            region = (Rectangle)_destRect;
        }
        else
        {
            region = ClientRectangle;
        }

        
        if (UseHardwareAcceleration)
        {
            // grid size
            int rows = (int)Math.Ceiling(region.Width / CheckerboardCellSize);
            int cols = (int)Math.Ceiling(region.Height / CheckerboardCellSize);

            // draw grid
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    Color color;
                    if ((row + col) % 2 == 0)
                    {
                        color = CheckerboardColor1;
                    }
                    else
                    {
                        color = CheckerboardColor2;
                    }

                    var drawnW = row * CheckerboardCellSize;
                    var drawnH = col * CheckerboardCellSize;

                    var x = drawnW + region.X;
                    var y = drawnH + region.Y;

                    var w = Math.Min(region.Width - drawnW, CheckerboardCellSize);
                    var h = Math.Min(region.Height - drawnH, CheckerboardCellSize);

                    g.FillRectangle(new(x, y, w, h), color);
                }
            }
        }
        else
        {
            // use GDI+ Texture
            var gdiG = g as GDIGraphics;

            using var checkerTile = CreateCheckerBoxTile(CheckerboardCellSize, CheckerboardColor1, CheckerboardColor2);
            using var texture = new TextureBrush(checkerTile);

            gdiG?.Graphics.FillRectangle(texture, region);
        }
    }


    protected virtual void DrawTextLayer(IHybridGraphics g)
    {
        if (Text.Trim().Length == 0) return;

        var textMargin = 20;
        var textPaddingX = textMargin * 2;
        var textPaddingY = textMargin;

        var drawableArea = new Rectangle(
            textMargin,
            textMargin,
            Width - textPaddingX,
            Height - textPaddingY);

        // calculate text region
        var fontSize = DpiApi.Transform<float>(Font.Size * (float)DpiApi.DpiScale);
        var textSize = g.MeasureText(Text, Font, fontSize, drawableArea.Size);
        var region = new RectangleF(
            drawableArea.Width / 2 - textSize.Width / 2,
            drawableArea.Height / 2 - textSize.Height / 2,
            textSize.Width + textPaddingX,
            textSize.Height + textPaddingY);

        // draw text background
        var color = Color.FromArgb(170, BackColor);
        g.DrawRoundedRectangle(region, color, color, new(5, 5));


        // draw text
        g.DrawText(Text, Font, fontSize, ForeColor, region);
    }


    protected virtual void DrawNavigationLayer(IHybridGraphics g)
    {
        if (NavDisplay == NavButtonDisplay.None) return;


        // left navigation
        if (NavDisplay == NavButtonDisplay.Left || NavDisplay == NavButtonDisplay.Both)
        {
            var iconOpacity = 1f;
            var iconY = 0;
            var leftColor = Color.Transparent;

            if (_isNavLeftPressed)
            {
                leftColor = NavPressedColor;
                iconOpacity = 0.7f;
                iconY = 1;
            }
            else if (_isNavLeftHovered)
            {
                leftColor = NavHoveredColor;
            }

            // draw button
            if (leftColor != Color.Transparent)
            {
                var leftCircle = new RectangleF(
                    _navLeftPos.X - NavButtonRadius,
                    _navLeftPos.Y - NavButtonRadius,
                    NavButtonRadius * 2,
                    NavButtonRadius * 2);

                g.FillEllipse(leftCircle, leftColor);
                g.DrawEllipse(leftCircle, leftColor, 1.25f);
            }

            // draw icon
            if (NavLeftImage is not null && (_isNavLeftHovered || _isNavLeftPressed))
            {
                g.DrawImage(NavLeftImage,
                    new RectangleF(
                        _navLeftPos.X - NavButtonRadius / 2,
                        _navLeftPos.Y - NavButtonRadius / 2 + iconY,
                        NavButtonRadius,
                        NavButtonRadius),
                    new RectangleF(0, 0, NavLeftImage.Width, NavLeftImage.Height),
                    iconOpacity);
            }
        }


        // right navigation
        if (NavDisplay == NavButtonDisplay.Right || NavDisplay == NavButtonDisplay.Both)
        {
            var iconOpacity = 1f;
            var iconY = 0;
            var rightColor = Color.Transparent;

            if (_isNavRightPressed)
            {
                rightColor = NavPressedColor;
                iconOpacity = 0.7f;
                iconY = 1;
            }
            else if (_isNavRightHovered)
            {
                rightColor = NavHoveredColor;
            }

            // draw button
            if (rightColor != Color.Transparent)
            {
                var rightCircle = new RectangleF(
                    _navRightPos.X - NavButtonRadius,
                    _navRightPos.Y - NavButtonRadius,
                    NavButtonRadius * 2,
                    NavButtonRadius * 2);

                g.FillEllipse(rightCircle, rightColor);
                g.DrawEllipse(rightCircle, rightColor, 1.25f);
            }

            // draw icon
            if (NavRightImage is not null && (_isNavRightHovered || _isNavRightPressed))
            {
                g.DrawImage(NavRightImage,
                    new RectangleF(
                        _navRightPos.X - NavButtonRadius / 2,
                        _navRightPos.Y - NavButtonRadius / 2 + iconY,
                        NavButtonRadius,
                        NavButtonRadius),
                    new RectangleF(0, 0, NavRightImage.Width, NavRightImage.Height),
                    iconOpacity);
            }
        }
    }


    protected virtual void UpdateZoomMode(ZoomMode? mode = null)
    {
        if (!IsReady || _d2dBitmap is null) return;

        var viewportW = Width;
        var viewportH = Height;
        var imgFullW = _d2dBitmap.Width;
        var imgFullH = _d2dBitmap.Height;

        var horizontalPadding = Padding.Left + Padding.Right;
        var verticalPadding = Padding.Top + Padding.Bottom;
        var widthScale = (viewportW - horizontalPadding) / imgFullW;
        var heightScale = (viewportH - verticalPadding) / imgFullH;

        float zoomFactor;
        var zoomMode = mode ?? _zoomMode;

        if (zoomMode == ZoomMode.ScaleToWidth)
        {
            zoomFactor = widthScale;
        }
        else if (zoomMode == ZoomMode.ScaleToHeight)
        {
            zoomFactor = heightScale;
        }
        else if (zoomMode == ZoomMode.ScaleToFit)
        {
            zoomFactor = Math.Min(widthScale, heightScale);
        }
        else if (zoomMode == ZoomMode.ScaleToFill)
        {
            zoomFactor = Math.Max(widthScale, heightScale);
        }
        else if (zoomMode == ZoomMode.LockZoom)
        {
            zoomFactor = ZoomFactor;
        }
        // AutoZoom
        else
        {
            // viewbox size >= image size
            if (widthScale >= 1 && heightScale >= 1)
            {
                zoomFactor = 1; // show original size
            }
            else
            {
                zoomFactor = Math.Min(widthScale, heightScale);
            }
        }

        _zoomFactor = zoomFactor;
        _isManualZoom = false;
        _shouldRecalculateDrawingRegion = true;
    }


    private static Bitmap CreateCheckerBoxTile(float cellSize, Color cellColor1, Color cellColor2)
    {
        // draw the tile
        var width = cellSize * 2;
        var height = cellSize * 2;
        var result = new Bitmap((int)width, (int)height);

        using var g = Graphics.FromImage(result);
        using (Brush brush = new SolidBrush(cellColor1))
        {
            g.FillRectangle(brush, new RectangleF(cellSize, 0, cellSize, cellSize));
            g.FillRectangle(brush, new RectangleF(0, cellSize, cellSize, cellSize));
        }

        using (Brush brush = new SolidBrush(cellColor2))
        {
            g.FillRectangle(brush, new RectangleF(0, 0, cellSize, cellSize));
            g.FillRectangle(brush, new RectangleF(cellSize, cellSize, cellSize, cellSize));
        }

        return result;
    }


    /// <summary>
    /// Force the control to update zoom mode and invalidate itself.
    /// </summary>
    public new void Refresh()
    {
        UpdateZoomMode();
        Invalidate();
    }


    /// <summary>
    /// Zooms into the image.
    /// </summary>
    /// <param name="point">
    /// Client's cursor location to zoom into.
    /// <c><see cref="ImageViewportCenterPoint"/></c> is the default value.
    /// </param>
    /// <returns>
    ///   <list type="table">
    ///     <item><c>true</c> if the viewport is changed.</item>
    ///     <item><c>false</c> if the viewport is unchanged.</item>
    ///   </list>
    /// </returns>
    public bool ZoomIn(PointF? point = null, bool requestRerender = true)
    {
        return ZoomToPoint(SystemInformation.MouseWheelScrollDelta, point, requestRerender);
    }


    /// <summary>
    /// Zooms out of the image.
    /// </summary>
    /// <param name="point">
    /// Client's cursor location to zoom out.
    /// <c><see cref="ImageViewportCenterPoint"/></c> is the default value.
    /// </param>
    /// <returns>
    ///   <list type="table">
    ///     <item><c>true</c> if the viewport is changed.</item>
    ///     <item><c>false</c> if the viewport is unchanged.</item>
    ///   </list>
    /// </returns>
    public bool ZoomOut(PointF? point = null, bool requestRerender = true)
    {
        return ZoomToPoint(-SystemInformation.MouseWheelScrollDelta, point, requestRerender);
    }


    /// <summary>
    /// Scales the image using delta value.
    /// </summary>
    /// <param name="delta">Delta value.
    ///   <list type="table">
    ///     <item><c>delta<![CDATA[>]]>0</c>: Zoom in.</item>
    ///     <item><c>delta<![CDATA[<]]>0</c>: Zoom out.</item>
    ///   </list>
    /// </param>
    /// <param name="point">
    /// Client's cursor location to zoom out.
    /// <c><see cref="ImageViewportCenterPoint"/></c> is the default value.
    /// </param>
    /// <returns>
    ///   <list type="table">
    ///     <item><c>true</c> if the viewport is changed.</item>
    ///     <item><c>false</c> if the viewport is unchanged.</item>
    ///   </list>
    /// </returns>
    public bool ZoomToPoint(float delta, PointF? point = null, bool requestRerender = true)
    {
        var speed = delta / 500f;
        var location = new PointF()
        {
            X = point?.X ?? ImageViewportCenterPoint.X,
            Y = point?.Y ?? ImageViewportCenterPoint.Y,
        };

        // zoom in
        if (delta > 0)
        {
            if (_zoomFactor > MaxZoom)
                return false;

            _oldZoomFactor = _zoomFactor;
            _zoomFactor *= 1f + speed;
            _shouldRecalculateDrawingRegion = true;
        }
        // zoom out
        else if (delta < 0)
        {
            if (_zoomFactor < MinZoom)
                return false;

            _oldZoomFactor = _zoomFactor;
            _zoomFactor /= 1f - speed;
            _shouldRecalculateDrawingRegion = true;
        }

        _isManualZoom = true;
        _drawPoint = location.ToVector2();

        if (requestRerender)
        {
            Invalidate();
        }

        // emit OnZoomChanged event
        OnZoomChanged?.Invoke(new(_zoomFactor));

        return true;
    }


    /// <summary>
    /// Pan the current viewport to a distance
    /// </summary>
    /// <param name="hDistance">Horizontal distance</param>
    /// <param name="vDistance">Vertical distance</param>
    /// <param name="requestRerender"><c>true</c> to request the control invalidates.</param>
    /// <returns>
    /// <list type="table">
    /// <item><c>true</c> if the viewport is changed.</item>
    /// <item><c>false</c> if the viewport is unchanged.</item>
    /// </list>
    /// </returns>
    public bool PanTo(float hDistance, float vDistance, bool requestRerender = true)
    {
        if (_d2dBitmap is null) return false;

        var loc = PointToClient(Cursor.Position);


        // horizontal
        if (hDistance != 0)
        {
            _srcRect.X += (hDistance / _zoomFactor) + _panSpeed.X;
        }

        // vertical 
        if (vDistance != 0)
        {
            _srcRect.Y += (vDistance / _zoomFactor) + _panSpeed.Y;
        }

        _drawPoint = new();
        _shouldRecalculateDrawingRegion = true;


        if (_xOut == false)
        {
            _panHostPoint.X = loc.X;
        }

        if (_yOut == false)
        {
            _panHostPoint.Y = loc.Y;
        }

        // emit event
        OnPanning?.Invoke(new(loc, new(_panHostPoint)));

        if (requestRerender)
        {
            Invalidate();
        }

        return true;
    }


    /// <summary>
    /// Shows text message.
    /// </summary>
    /// <param name="text">Message to show</param>
    /// <param name="durationMs">Display duration in millisecond.
    /// Set it <b>greater than 0</b> to disable auto clear.</param>
    /// <param name="delayMs">Duration to delay before displaying the message.</param>
    public async void ShowMessage(string text, int durationMs = 0, int delayMs = 0)
    {
        _msgTokenSrc?.Cancel();
        _msgTokenSrc = new();

        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, _msgTokenSrc.Token);
            }

            Text = text;

            if (durationMs > 0)
            {
                await Task.Delay(durationMs, _msgTokenSrc.Token);
            }
        }
        catch { }

        if (durationMs > 0)
        {
            Text = string.Empty;
        }
    }


    /// <summary>
    /// Immediately clears text message.
    /// </summary>
    public void ClearMessage()
    {
        _msgTokenSrc?.Cancel();
        Text = string.Empty;
    }


    /// <summary>
    /// Load image from file path
    /// </summary>
    /// <param name="filename">Full path of file</param>
    public void LoadImage(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return;

        // disable animations
        StopAnimating();

        _gdiBitmap?.Dispose();
        try
        {
            _gdiBitmap = new Bitmap(filename);
        }
        catch { }

        _d2dBitmap?.Dispose();
        _d2dBitmap = Device.LoadBitmap(filename);

        PrepareImage();
    }


    /// <summary>
    /// Load image
    /// </summary>
    /// <param name="bmp"></param>
    public void LoadImage(Bitmap? bmp)
    {
        // disable animations
        StopAnimating();

        _d2dBitmap?.Dispose();
        _gdiBitmap?.Dispose();

        _gdiBitmap = bmp;
        if (_gdiBitmap is null) return;

        _d2dBitmap = Device.CreateBitmapFromGDIBitmap(_gdiBitmap);
        PrepareImage();
    }

    private void PrepareImage()
    {
        // emit OnImageChanged event
        OnImageChanged?.Invoke(EventArgs.Empty);

        if (CanAnimate && _gdiBitmap is not null)
        {
            UpdateZoomMode();
            StartAnimating();
        }
        else
        {
            Refresh();
        }
    }


    private void OnFrameChangedHandler(object? sender, EventArgs eventArgs)
    {
        Invalidate();
    }



    /// <summary>
    /// Start animating the image if it can animate, using GDI+.
    /// </summary>
    public void StartAnimating()
    {
        if (IsAnimating || !CanAnimate || _gdiBitmap is null)
            return;

        try
        {
            // backup current UseHardwardAcceleration value
            _useHardwareAccelerationBackup = UseHardwareAcceleration;

            // force using GDI+ to animate GIF
            UseHardwareAcceleration = false;

            _animator.Animate(_gdiBitmap, OnFrameChangedHandler);
            IsAnimating = true;
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Stop animating the image
    /// </summary>
    public void StopAnimating()
    {
        if (_gdiBitmap is not null)
        {
            _animator.StopAnimate(_gdiBitmap, OnFrameChangedHandler);
        }

        IsAnimating = false;

        // restore the UseHardwardAcceleration value
        UseHardwareAcceleration = _useHardwareAccelerationBackup;
    }


}
