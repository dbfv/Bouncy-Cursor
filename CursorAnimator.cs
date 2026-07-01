using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BounceCursor
{
    public enum CursorKind { None, Arrow, Hand }

    public static class CursorAnimator
    {
        private const uint OCR_NORMAL = 32512;
        private const uint OCR_HAND = 32649;
        private const uint IDC_ARROW = 32512;
        private const uint IDC_HAND = 32649;
        private const uint SPI_SETCURSORS = 0x0057;

        private static readonly IntPtr _arrowHandle = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);
        private static readonly IntPtr _handHandle = LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND);

        // Keep pristine copies of the original cursors to prevent infinite scaling
        private static readonly IntPtr _origArrowHandle = CopyIcon(_arrowHandle);
        private static readonly IntPtr _origHandHandle = CopyIcon(_handHandle);

        public static CursorKind GetActiveCursorKind()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.hCursor == IntPtr.Zero) return CursorKind.None;
            if (ci.hCursor == _arrowHandle) return CursorKind.Arrow;
            if (ci.hCursor == _handHandle) return CursorKind.Hand;
            return CursorKind.None;
        }

        public static void ApplyScale(CursorKind kind, double scale)
        {
            if (kind == CursorKind.None) return;
            uint ocrId = kind == CursorKind.Arrow ? OCR_NORMAL : OCR_HAND;
            
            // Use the pristine copy as the base, not the system cursor which gets overwritten
            IntPtr baseCursor = kind == CursorKind.Arrow ? _origArrowHandle : _origHandHandle;

            IntPtr scaled = BuildScaledCursor(baseCursor, scale);
            if (scaled != IntPtr.Zero)
                SetSystemCursor(scaled, ocrId); // OS automatically destroys this handle after setting
        }

        // Reset ALL system cursors to default — call when animation ends or when app exits/crashes.
        public static void RestoreAll() =>
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

        private static IntPtr BuildScaledCursor(IntPtr hCursor, double scale)
        {
            if (!GetIconInfo(hCursor, out ICONINFO info)) return IntPtr.Zero;
            IntPtr result = IntPtr.Zero;

            try
            {
                IntPtr srcColor = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
                using var colorBmp = ExtractColorBitmap(srcColor);
                if (colorBmp == null) return IntPtr.Zero;

                int w = colorBmp.Width, h = colorBmp.Height;

                using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    float newW = (float)(w * scale);
                    float newH = (float)(h * scale);
                    float offsetX = info.xHotspot - info.xHotspot * (float)scale;
                    float offsetY = info.yHotspot - info.yHotspot * (float)scale;
                    g.DrawImage(colorBmp, offsetX, offsetY, newW, newH);
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

        // Read HBITMAP to Bitmap preserving real alpha (replaces Image.FromHbitmap which loses alpha)
        private static Bitmap? ExtractColorBitmap(IntPtr hbm)
        {
            var bmpInfo = new BITMAP();
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bmpInfo) == 0) return null;
            int width = bmpInfo.bmWidth;
            int height = bmpInfo.bmHeight;
            if (width <= 0 || height <= 0) return null;

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // top-down
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

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            bmp.UnlockBits(data);
            return bmp;
        }
        
        // Create 32bpp HBITMAP keeping correct alpha channel (premultiplied) so transparent cursor displays correctly without black borders.
        private static IntPtr CreatePremultipliedHBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr ppvBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero) return IntPtr.Zero;

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int byteCount = data.Stride * h;
                byte[] buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);
                for (int i = 0; i < byteCount; i += 4)
                {
                    byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2], a = buffer[i + 3];
                    buffer[i] = (byte)(b * a / 255);
                    buffer[i + 1] = (byte)(g * a / 255);
                    buffer[i + 2] = (byte)(r * a / 255);
                }
                Marshal.Copy(buffer, 0, ppvBits, byteCount);
            }
            finally { bmp.UnlockBits(data); }

            return hBitmap;
        }

        // All 0 mask, exact size of scaled color bitmap — must match size, otherwise CreateIconIndirect renders broken/distorted shape.
        private static IntPtr CreateMatchingMask(int width, int height)
        {
            int stride = ((width + 15) / 16) * 2; // mono bitmap requires 16-bit alignment
            byte[] zeroBits = new byte[stride * height]; // default all 0
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