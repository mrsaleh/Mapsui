using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Mapsui.Logging;
using Mapsui.Widgets;
using SkiaSharp.Views.Android;
using Math = System.Math;

namespace Mapsui.UI.Android
{
    public partial class MapControl : ViewGroup, IMapControl
    {
        private SKCanvasView _canvas;
        private double _innerRotation;
        private GestureDetector _gestureDetector;
        private double _previousAngle;
        private double _previousRadius = 1f;
        private TouchMode _mode = TouchMode.None;
        /// <summary>
        /// The number of pixels per device independent unit
        /// </summary>
        private float _density;

        public event EventHandler ViewportInitialized;

        public MapControl(Context context, IAttributeSet attrs) :
            base(context, attrs)
        {
            Initialize();
        }

        public MapControl(Context context, IAttributeSet attrs, int defStyle) :
            base(context, attrs, defStyle)
        {
            Initialize();
        }

        public void Initialize()
        {
            SetBackgroundColor(Color.Transparent);
            _density = PixelsPerDeviceIndependentUnit;
            _canvas = new SKCanvasView(Context) { IgnorePixelScaling = true };
            _canvas.PaintSurface += CanvasOnPaintSurface;
            AddView(_canvas);

            Map = new Map();
            TryInitializeViewport(ToDeviceIndependentUnits(Width), ToDeviceIndependentUnits(Height));
            Touch += MapView_Touch;

            _gestureDetector = new GestureDetector(Context, new GestureDetector.SimpleOnGestureListener());
            _gestureDetector.SingleTapConfirmed += OnSingleTapped;
            _gestureDetector.DoubleTap += OnDoubleTapped;
        }

        public float PixelsPerDeviceIndependentUnit => Resources.DisplayMetrics.Density;

        private void OnDoubleTapped(object sender, GestureDetector.DoubleTapEventArgs e)
        {
            var position = GetScreenPosition(e.Event, this);
            Map.InvokeInfo(position, position, Renderer.SymbolCache, WidgetTouched, 2);
        }

        private void OnSingleTapped(object sender, GestureDetector.SingleTapConfirmedEventArgs e)
        {
            var position = GetScreenPosition(e.Event, this);
            Map.InvokeInfo(position, position, Renderer.SymbolCache, WidgetTouched, 1);
        }

        protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
        {
            base.OnSizeChanged(width, height, oldWidth, oldHeight);

            if (Map == null) return;

            Map.Viewport.Width = ToDeviceIndependentUnits(width);
            Map.Viewport.Height = ToDeviceIndependentUnits(height);
        }

        private void RunOnUIThread(Action action)
        {
            new Handler(Looper.MainLooper).Post(action);
        }

        private void CanvasOnPaintSurface(object sender, SKPaintSurfaceEventArgs args)
        {
            TryInitializeViewport(ToDeviceIndependentUnits(Width), ToDeviceIndependentUnits(Height));
            if (!_map.Viewport.Initialized) return;

            Renderer.Render(args.Surface.Canvas, _map.Viewport, _map.Layers, _map.Widgets, _map.BackColor);
        }

        public void MapView_Touch(object sender, TouchEventArgs args)
        {
            if (_gestureDetector.OnTouchEvent(args.Event))
                return;

            var touchPoints = GetScreenPositions(args.Event, this);

            switch (args.Event.Action)
            {
                case MotionEventActions.Up:
                    RefreshGraphics();
                    _mode = TouchMode.None;
                    _map.RefreshData(true);
                    break;
                case MotionEventActions.Down:
                case MotionEventActions.Pointer1Down:
                case MotionEventActions.Pointer2Down:
                case MotionEventActions.Pointer3Down:
                    if (touchPoints.Count >= 2)
                    {
                        (_previousCenter, _previousRadius, _previousAngle) = GetPinchValues(touchPoints);
                        _mode = TouchMode.Zooming;
                        _innerRotation = _map.Viewport.Rotation;
                    }
                    else
                    {
                        _mode = TouchMode.Dragging;
                        _previousCenter = touchPoints.First();
                    }
                    break;
                case MotionEventActions.Pointer1Up:
                case MotionEventActions.Pointer2Up:
                case MotionEventActions.Pointer3Up:
                    // Remove the touchPoint that was released from the locations to reset the
                    // starting points of the move and rotation
                    touchPoints.RemoveAt(args.Event.ActionIndex);

                    if (touchPoints.Count >= 2)
                    {
                        (_previousCenter, _previousRadius, _previousAngle) = GetPinchValues(touchPoints);
                        _mode = TouchMode.Zooming;
                        _innerRotation = _map.Viewport.Rotation;
                    }
                    else
                    {
                        _mode = TouchMode.Dragging;
                        _previousCenter = touchPoints.First();
                    }
                    break;
                case MotionEventActions.Move:
                    switch (_mode)
                    {
                        case TouchMode.Dragging:
                            {
                                if (touchPoints.Count != 1)
                                    return;

                                var touchPosition = touchPoints.First();
                                if (_previousCenter != null && !_previousCenter.IsEmpty())
                                {
                                    _map.Viewport.Transform(touchPosition.X, touchPosition.Y, _previousCenter.X, _previousCenter.Y);

                                    ViewportLimiter.LimitExtent(_map.Viewport, _map.PanMode, _map.PanLimits, _map.Envelope);

                                    RefreshGraphics();
                                }
                                _previousCenter = touchPosition;
                            }
                            break;
                        case TouchMode.Zooming:
                            {
                                if (touchPoints.Count < 2)
                                    return;

                                var (prevCenter, prevRadius, prevAngle) = (_previousCenter, _previousRadius, _previousAngle);
                                var (center, radius, angle) = GetPinchValues(touchPoints);

                                double rotationDelta = 0;

                                if (!RotationLock)
                                {
                                    _innerRotation += angle - prevAngle;
                                    _innerRotation %= 360;

                                    if (_innerRotation > 180)
                                        _innerRotation -= 360;
                                    else if (_innerRotation < -180)
                                        _innerRotation += 360;

                                    if (_map.Viewport.Rotation == 0 && Math.Abs(_innerRotation) >= Math.Abs(UnSnapRotationDegrees))
                                        rotationDelta = _innerRotation;
                                    else if (_map.Viewport.Rotation != 0)
                                    {
                                        if (Math.Abs(_innerRotation) <= Math.Abs(ReSnapRotationDegrees))
                                            rotationDelta = -_map.Viewport.Rotation;
                                        else
                                            rotationDelta = _innerRotation - _map.Viewport.Rotation;
                                    }
                                }

                                _map.Viewport.Transform(center.X, center.Y, prevCenter.X, prevCenter.Y, radius / prevRadius, rotationDelta);

                                (_previousCenter, _previousRadius, _previousAngle) = (center, radius, angle);

                                ViewportLimiter.Limit(_map.Viewport,
                                    _map.ZoomMode, _map.ZoomLimits, _map.Resolutions,
                                    _map.PanMode, _map.PanLimits, _map.Envelope);

                                RefreshGraphics();
                            }
                            break;
                    }
                    break;
            }
        }

        private List<Geometries.Point> GetScreenPositions(MotionEvent me, View view)
        {
            var result = new List<Geometries.Point>();
            for (var i = 0; i < me.PointerCount; i++)
            {
                result.Add(new Geometries.Point(me.GetX(i) - view.Left, me.GetY(i) - view.Top)
                    .ToDeviceIndependentUnits(PixelsPerDeviceIndependentUnit));
            }
            return result;
        }

        /// <summary>
        /// Gets the screen position in device independent units relative to the MapControl.
        /// </summary>
        /// <param name="motionEvent"></param>
        /// <param name="view"></param>
        /// <returns></returns>
        private Geometries.Point GetScreenPosition(MotionEvent motionEvent, View view)
        {
            return GetScreenPositionInPixels(motionEvent, view)
                .ToDeviceIndependentUnits(PixelsPerDeviceIndependentUnit);
        }

        /// <summary>
        /// Gets the screen position in pixels relative to the MapControl.
        /// </summary>
        /// <param name="motionEvent"></param>
        /// <param name="view"></param>
        /// <returns></returns>
        private static Geometries.Point GetScreenPositionInPixels(MotionEvent motionEvent, View view)
        {
            return new PointF(
                motionEvent.GetX(0) - view.Left,
                motionEvent.GetY(0) - view.Top).ToMapsui();
        }

        public void RefreshGraphics()
        {
            RunOnUIThread(RefreshGraphicsWithTryCatch);
        }

        private void RefreshGraphicsWithTryCatch()
        {
            try
            {
                Invalidate();
                // Calling Invalite on the MapControl itself is not enough in some case (observed in XF).
                _canvas?.Invalidate();
            }
            catch (ObjectDisposedException e)
            {
                // See issue: https://github.com/Mapsui/Mapsui/issues/433
                // What seems to be happening. The Activity is Disposed. Appently it's children get Disposed
                // explicitly by some in Xamarin. During this Dispose the MessageCenter, which is itself not
                // disposed get another notification to call RefreshGraphics.
                Logger.Log(LogLevel.Warning, "This can happen when the parent Activity is disposing.", e);
            }
        }

        public void RefreshData()
        {
            _map.RefreshData(true);
        }

        protected override void OnLayout(bool changed, int l, int t, int r, int b)
        {
            Position(_canvas, l, t, r, b);
        }

        private void Position(View view, int l, int t, int r, int b)
        {
            view.Top = t;
            view.Bottom = b;
            view.Left = l;
            view.Right = r;
        }

        private void WidgetTouched(IWidget widget, Geometries.Point screenPosition)
        {
            if (widget is Hyperlink hyperlink)
            {
                global::Android.Net.Uri uri = global::Android.Net.Uri.Parse(hyperlink.Url);
                Intent intent = new Intent(Intent.ActionView);
                intent.SetData(uri);

                Intent chooser = Intent.CreateChooser(intent, "Open with");

                Context.StartActivity(chooser);
            }

            widget.HandleWidgetTouched(screenPosition);
        }

        /// <summary>
        /// In native Android touch positions are in device pixels whereas the canvas needs
        /// to be drawn in logical pixels (otherwise labels on raster tiles will be unreadable
        /// and symbols will be too small). This method converts device pixels to logical pixels.
        /// </summary>
        /// <returns>The device pixels given as input translated to device pixels.</returns>
        private float ToDeviceIndependentUnits(float pixelCoordinate)
        {
            return pixelCoordinate / _density;
        }

        public new void Dispose()
        {
            Unsubscribe();
            base.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            Unsubscribe();
            base.Dispose(disposing);
        }
    }
}