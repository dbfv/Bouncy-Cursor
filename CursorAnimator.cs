using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BounceCursor
{
    public enum CursorKind { None, Arrow, Hand, IBeam }

    public static class CursorAnimator
    {
        private const uint OCR_NORMAL = 32512;
        private const uint OCR_HAND = 32649;
        private const uint OCR_IBEAM = 32513;

        private const uint IDC_ARROW = 32512;
        private const uint IDC_HAND = 32649;
        private const uint IDC_IBEAM = 32513;

        private const uint SPI_SETCURSORS = 0x0057;

        // The "pristine" base handles used as a source for scaling. We dynamically reload
        // these if they aren't loaded correctly on startup (e.g. cold boot to external monitor).
        private static IntPtr _origArrowHandle = IntPtr.Zero;
        private static IntPtr _origHandHandle = IntPtr.Zero;
        private static IntPtr _origIBeamHandle = IntPtr.Zero;

        private static volatile bool _baseCursorsReady;
        private static readonly object _initLock = new();
        private static bool _reWarmSubscribed;

        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cursor-debug.log");

        public static CursorKind GetActiveCursorKind()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.hCursor == IntPtr.Zero) return CursorKind.None;

            // Load cursor handles on every call (very cheap for standard system cursors).
            // Comparing with fixed startup handles is unreliable because Per-Monitor DPI
            // aware apps might get different underlying system handles dynamically.
            IntPtr arrow = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);
            IntPtr hand = LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND);
            IntPtr ibeam = LoadCursor(IntPtr.Zero, (IntPtr)IDC_IBEAM);

            if (ci.hCursor == arrow) return CursorKind.Arrow;
            if (ci.hCursor == hand) return CursorKind.Hand;
            if (ci.hCursor == ibeam) return CursorKind.IBeam;
            return CursorKind.None;
        }

        public static void ApplyScale(CursorKind kind, double scale)
        {
            if (kind == CursorKind.None) return;

            if (!_baseCursorsReady)
            {
                // Not ready (warming up or failed) -> skip this frame rather than setting an invalid cursor.
                return;
            }

            uint ocrId = OCR_NORMAL;
            IntPtr baseCursor = _origArrowHandle;

            if (kind == CursorKind.Arrow) { ocrId = OCR_NORMAL; baseCursor = _origArrowHandle; }
            else if (kind == CursorKind.Hand) { ocrId = OCR_HAND; baseCursor = _origHandHandle; }
            else if (kind == CursorKind.IBeam) { ocrId = OCR_IBEAM; baseCursor = _origIBeamHandle; }

            if (baseCursor == IntPtr.Zero) return;

            IntPtr scaled = BuildScaledCursor(baseCursor, scale);
            if (scaled != IntPtr.Zero)
            {
                SetSystemCursor(scaled, ocrId);
            }
            else
            {
                Log($"ApplyScale: BuildScaledCursor failed (kind={kind}, scale={scale:0.00}). Marking as not ready and re-arming.");
                _baseCursorsReady = false;
                _ = Task.Run(EnsureBaseCursorsLoaded);
            }
        }

        public static void RestoreAll() =>
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

        /// <summary>
        /// Called once at startup (on background thread) to "warm up" the base cursor cache 
        /// before the user can click. Retries automatically if the Windows cursor theme 
        /// isn't fully ready yet (e.g. during immediate external monitor boot sequence).
        /// </summary>
        public static void WarmUp()
        {
            EnsureBaseCursorsLoaded();

            // Re-warm when display settings / DPI change to ensure we get the best pristine cursor.
            if (!_reWarmSubscribed)
            {
                _reWarmSubscribed = true;
                try
                {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                    {
                        Log("DisplaySettingsChanged: resetting _baseCursorsReady and reloading base cursors.");
                        _baseCursorsReady = false;
                        _ = Task.Run(EnsureBaseCursorsLoaded);
                    };
                }
                catch (Exception ex)
                {
                    Log($"WarmUp: Failed to subscribe to DisplaySettingsChanged ({ex.Message}).");
                }
            }
        }

        private static void EnsureBaseCursorsLoaded()
        {
            if (_baseCursorsReady) return;

            lock (_initLock)
            {
                if (_baseCursorsReady) return;

                const int maxAttempts = 8;
                const int delayMs = 300;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    IntPtr arrow = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW));
                    IntPtr hand = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                    IntPtr ibeam = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_IBEAM));

                    bool arrowOk = IsCursorSane(arrow, out string arrowInfo);
                    bool handOk = IsCursorSane(hand, out string handInfo);
                    bool ibeamOk = IsCursorSane(ibeam, out string ibeamInfo);

                    Log($"EnsureBaseCursorsLoaded attempt {attempt}/{maxAttempts}: System DPI={GetDpiForSystem()} | arrow: {arrowInfo} | hand: {handInfo} | ibeam: {ibeamInfo}");

                    if (arrowOk && handOk && ibeamOk)
                    {
                        IntPtr arrowVal = ValidateAndKeepCursor(arrow, out string arrowValInfo);
                        IntPtr handVal = ValidateAndKeepCursor(hand, out string handValInfo);
                        IntPtr ibeamVal = ValidateAndKeepCursor(ibeam, out string ibeamValInfo);

                        DestroyIcon(arrow);
                        DestroyIcon(hand);
                        DestroyIcon(ibeam);

                        Log($"EnsureBaseCursorsLoaded attempt {attempt}: validation -> arrow: {arrowValInfo} | hand: {handValInfo} | ibeam: {ibeamValInfo}");

                        if (arrowVal != IntPtr.Zero && handVal != IntPtr.Zero && ibeamVal != IntPtr.Zero)
                        {
                            if (_origArrowHandle != IntPtr.Zero) DestroyIcon(_origArrowHandle);
                            if (_origHandHandle != IntPtr.Zero) DestroyIcon(_origHandHandle);
                            if (_origIBeamHandle != IntPtr.Zero) DestroyIcon(_origIBeamHandle);

                            _origArrowHandle = arrowVal;
                            _origHandHandle = handVal;
                            _origIBeamHandle = ibeamVal;
                            _baseCursorsReady = true;
                            Log("EnsureBaseCursorsLoaded: OK, base cursors are ready.");
                            return;
                        }

                        if (arrowVal != IntPtr.Zero) DestroyIcon(arrowVal);
                        if (handVal != IntPtr.Zero) DestroyIcon(handVal);
                        if (ibeamVal != IntPtr.Zero) DestroyIcon(ibeamVal);

                        Log($"EnsureBaseCursorsLoaded attempt {attempt}: Structurally sane but validation/content check failed -> retrying.");
                    }
                    else
                    {
                        if (arrow != IntPtr.Zero) DestroyIcon(arrow);
                        if (hand != IntPtr.Zero) DestroyIcon(hand);
                        if (ibeam != IntPtr.Zero) DestroyIcon(ibeam);
                    }

                    if (attempt < maxAttempts) Thread.Sleep(delayMs);
                }

                IntPtr fallbackArrow = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW));
                IntPtr fallbackHand = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                IntPtr fallbackIBeam = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_IBEAM));

                IntPtr fallbackArrowVal = ValidateAndKeepCursor(fallbackArrow, out _);
                IntPtr fallbackHandVal = ValidateAndKeepCursor(fallbackHand, out _);
                IntPtr fallbackIBeamVal = ValidateAndKeepCursor(fallbackIBeam, out _);

                if (fallbackArrowVal != IntPtr.Zero) { _origArrowHandle = fallbackArrowVal; DestroyIcon(fallbackArrow); }
                else { _origArrowHandle = fallbackArrow; }

                if (fallbackHandVal != IntPtr.Zero) { _origHandHandle = fallbackHandVal; DestroyIcon(fallbackHand); }
                else { _origHandHandle = fallbackHand; }

                if (fallbackIBeamVal != IntPtr.Zero) { _origIBeamHandle = fallbackIBeamVal; DestroyIcon(fallbackIBeam); }
                else { _origIBeamHandle = fallbackIBeam; }

                _baseCursorsReady = true;
                Log("EnsureBaseCursorsLoaded: Max attempts reached, using fallback handles.");
            }
        }

        private static bool IsCursorSane(IntPtr hCursor, out string info)
        {
            info = "handle=NULL";
            if (hCursor == IntPtr.Zero) return false;

            if (!GetIconInfo(hCursor, out ICONINFO iconInfo))
            {
                info = "GetIconInfo failed";
                return false;
            }

            try
            {
                bool isColor = iconInfo.hbmColor != IntPtr.Zero;
                IntPtr srcBmp = isColor ? iconInfo.hbmColor : iconInfo.hbmMask;
                if (srcBmp == IntPtr.Zero)
                {
                    info = "no bitmaps (both color and mask are null)";
                    return false;
                }

                var bmpInfo = new BITMAP();
                if (GetObject(srcBmp, Marshal.SizeOf<BITMAP>(), ref bmpInfo) == 0)
                {
                    info = "GetObject failed";
                    return false;
                }

                int width = bmpInfo.bmWidth;
                int height = bmpInfo.bmHeight;
                if (!isColor)
                {
                    if (height % 2 != 0)
                    {
                        info = $"monochrome but bmHeight is odd ({height}) -> abnormal";
                        return false;
                    }
                    height /= 2;
                }

                info = $"{(isColor ? "color" : "mono")} {width}x{height}";
                return width is > 0 and <= 256 && height is > 0 and <= 256;
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }

        private static IntPtr ValidateAndKeepCursor(IntPtr rawCursor, out string info)
        {
            info = "handle=NULL";
            if (rawCursor == IntPtr.Zero) return IntPtr.Zero;

            if (!GetIconInfo(rawCursor, out ICONINFO iconInfo))
            {
                info = "GetIconInfo failed";
                return IntPtr.Zero;
            }

            try
            {
                using var srcBmp = ExtractColorBitmap(iconInfo);
                if (srcBmp == null)
                {
                    info = "failed to extract bitmap";
                    return IntPtr.Zero;
                }

                if (!HasVisibleContent(srcBmp))
                {
                    info = $"bitmap {srcBmp.Width}x{srcBmp.Height} has NO CONTENT (transparent) -> possible driver glitch";
                    return IntPtr.Zero;
                }

                info = $"Valid {srcBmp.Width}x{srcBmp.Height}";
                return CopyIcon(rawCursor);
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }

        private static bool HasVisibleContent(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int byteCount = data.Stride * bmp.Height;
                byte[] buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);

                for (int i = 3; i < byteCount; i += 4) // Alpha channel
                {
                    if (buffer[i] > 20) return true;
                }
                return false;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static IntPtr BuildScaledCursor(IntPtr hCursor, double scale)
        {
            if (!GetIconInfo(hCursor, out ICONINFO info)) return IntPtr.Zero;
            IntPtr result = IntPtr.Zero;

            try
            {
                using var colorBmp = ExtractColorBitmap(info);
                if (colorBmp == null) return IntPtr.Zero;

                int destW = (int)Math.Round(colorBmp.Width * scale);
                int destH = (int)Math.Round(colorBmp.Height * scale);
                int offsetX = (int)Math.Round(info.xHotspot * (1.0 - scale));
                int offsetY = (int)Math.Round(info.yHotspot * (1.0 - scale));

                // Crucial: Format32bppPArgb tells GDI+ that colors are already pre-multiplied.
                // This eliminates the dark halo / jagged edges ("vỡ nét") during downscaling.
                using var canvas = new Bitmap(colorBmp.Width, colorBmp.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);

                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                    using var attributes = new System.Drawing.Imaging.ImageAttributes();
                    attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);

                    var destRect = new Rectangle(offsetX, offsetY, destW, destH);
                    g.DrawImage(colorBmp, destRect, 0, 0, colorBmp.Width, colorBmp.Height, GraphicsUnit.Pixel, attributes);
                }

                IntPtr hColorArgb = CreatePremultipliedHBitmap(canvas);
                IntPtr hMaskNew = CreateMatchingMask(canvas.Width, canvas.Height);

                var newInfo = new ICONINFO
                {
                    fIcon = false,
                    xHotspot = info.xHotspot,
                    yHotspot = info.yHotspot,
                    hbmColor = hColorArgb,
                    hbmMask = hMaskNew
                };
                result = CreateIconIndirect(ref newInfo);
                DeleteObject(hColorArgb);
                DeleteObject(hMaskNew);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
            return result;
        }

        private static Bitmap? ExtractColorBitmap(ICONINFO info)
        {
            bool isColor = info.hbmColor != IntPtr.Zero;
            IntPtr srcBmp = isColor ? info.hbmColor : info.hbmMask;
            if (srcBmp == IntPtr.Zero) return null;

            var bmpInfo = new BITMAP();
            if (GetObject(srcBmp, Marshal.SizeOf<BITMAP>(), ref bmpInfo) == 0) return null;

            int width = bmpInfo.bmWidth;
            int height = bmpInfo.bmHeight;

            if (!isColor)
            {
                if (height % 2 != 0)
                {
                    Log($"ExtractColorBitmap: mono cursor but odd bmHeight ({height}) -> skipping.");
                    return null;
                }
                height /= 2;
            }

            if (width <= 0 || height <= 0 || width > 256 || height > 256)
            {
                Log($"ExtractColorBitmap: abnormal dimensions width={width} height={height} isColor={isColor} -> skipping.");
                return null;
            }

            return isColor ? ExtractColorArgb(srcBmp, width, height) : ExtractMonochromeArgb(srcBmp, width, height);
        }

        private static Bitmap? ExtractColorArgb(IntPtr hbm, int width, int height)
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            int stride = width * 4;
            byte[] buffer = new byte[stride * height];
            IntPtr hdc = GetDC(IntPtr.Zero);
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int scanLines = GetDIBits(hdc, hbm, 0, (uint)height, handle.AddrOfPinnedObject(), ref bmi, 0);
                if (scanLines == 0) return null;
            }
            finally
            {
                handle.Free();
                ReleaseDC(IntPtr.Zero, hdc);
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        private static Bitmap? ExtractMonochromeArgb(IntPtr hbmMask, int width, int height)
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -(height * 2); 
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 1;
            bmi.bmiHeader.biCompression = 0;

            int maskStride = ((width + 31) / 32) * 4;
            byte[] maskBuffer = new byte[maskStride * height * 2];

            IntPtr hdc = GetDC(IntPtr.Zero);
            GCHandle handle = GCHandle.Alloc(maskBuffer, GCHandleType.Pinned);
            try
            {
                int scanLines = GetDIBits(hdc, hbmMask, 0, (uint)(height * 2), handle.AddrOfPinnedObject(), ref bmi, 0);
                if (scanLines == 0) return null;
            }
            finally
            {
                handle.Free();
                ReleaseDC(IntPtr.Zero, hdc);
            }

            int argbStride = width * 4;
            byte[] argb = new byte[argbStride * height];

            for (int y = 0; y < height; y++)
            {
                int andRowOffset = y * maskStride;
                int xorRowOffset = (height + y) * maskStride;

                for (int x = 0; x < width; x++)
                {
                    bool andBit = GetMaskBit(maskBuffer, andRowOffset, x);
                    bool xorBit = GetMaskBit(maskBuffer, xorRowOffset, x);

                    byte a, c;
                    if (!andBit && !xorBit) { a = 255; c = 0; }       // Black
                    else if (!andBit && xorBit) { a = 255; c = 255; } // White
                    else if (andBit && !xorBit) { a = 0; c = 0; }     // Transparent
                    else { a = 255; c = 128; }                        // Inverted -> Gray

                    int i = y * argbStride + x * 4;
                    argb[i + 0] = c; // B
                    argb[i + 1] = c; // G
                    argb[i + 2] = c; // R
                    argb[i + 3] = a; // A
                }
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(argb, 0, data.Scan0, argb.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        private static bool GetMaskBit(byte[] buffer, int rowByteOffset, int x)
        {
            int byteIndex = rowByteOffset + (x / 8);
            int bitIndex = 7 - (x % 8);
            return (buffer[byteIndex] & (1 << bitIndex)) != 0;
        }

        private static IntPtr CreatePremultipliedHBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr ppvBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero) return IntPtr.Zero;

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int byteCount = data.Stride * h;
                // Since the canvas is natively PArgb, the memory layout perfectly matches 
                // the pre-multiplied DIB requirements. We just do a direct byte copy!
                byte[] buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);
                Marshal.Copy(buffer, 0, ppvBits, byteCount);
            }
            finally { bmp.UnlockBits(data); }

            return hBitmap;
        }

        private static IntPtr CreateMatchingMask(int width, int height)
        {
            int stride = ((width + 15) / 16) * 2;
            byte[] zeroBits = new byte[stride * height];
            GCHandle handle = GCHandle.Alloc(zeroBits, GCHandleType.Pinned);
            try
            {
                return CreateBitmap(width, height, 1, 1, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch
            {
                // Silent catch for logger
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter;
            public int biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        [DllImport("user32.dll")] private static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetSystemCursor(IntPtr hcur, uint id);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public short bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpvBits);
    }
}