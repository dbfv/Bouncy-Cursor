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

        // Cac handle "goc" (pristine) dung lam nguon de scale.
        // KHONG con la static readonly nap 1 lan luc khoi dong nua,
        // vi luc app vua mo (dac biet la chay cung luc Windows dang nhap
        // va xuat thang ra man hinh ngoai) theme con tro cua Windows co the
        // CHUA load xong -> LoadCursor tra ve con tro mac dinh/degenerate,
        // va neu cache y nguyen cai do mai mai thi moi lan scale sau deu bi vo.
        private static IntPtr _origArrowHandle = IntPtr.Zero;
        private static IntPtr _origHandHandle = IntPtr.Zero;
        private static IntPtr _origIBeamHandle = IntPtr.Zero;

        private static volatile bool _baseCursorsReady;
        private static readonly object _initLock = new();

        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cursor-debug.log");

        public static CursorKind GetActiveCursorKind()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.hCursor == IntPtr.Zero) return CursorKind.None;

            // Nap lai handle tham chieu MOI LAN goi (rat re voi con tro he thong chuan)
            // thay vi so sanh voi handle cache tinh 1 lan luc khoi dong. Windows co the
            // tra ve cac handle KHAC NHAU cho "cung mot" con tro tuy theo DPI cua
            // man hinh hien tai khi app Per-Monitor-DPI-aware, nen so voi cache cu de
            // bi None (khong nhan dien duoc) khi doi man hinh / DPI.
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
                // Chua san sang (dang warm-up o nen, hoac warm-up chua kip chay) ->
                // bo qua khung hinh nay thay vi build tu 1 cursor goc chua hop le.
                // Tha mat vai khung hinh animation con hon set 1 con tro vo ra man hinh.
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
                Log($"ApplyScale: BuildScaledCursor that bai (kind={kind}, scale={scale:0.00}). " +
                    "Danh dau lai la 'chua san sang' va thu nap lai cursor goc o nen.");
                _baseCursorsReady = false;
                _ = Task.Run(EnsureBaseCursorsLoaded);
            }
        }

        public static void RestoreAll() =>
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

        /// <summary>
        /// Goi 1 lan luc khoi dong (tren background thread) de "lam nong" cache
        /// cursor goc truoc khi nguoi dung kip click. Tu thu lai vai lan neu
        /// theme con tro cua Windows chua san sang (VD: vua khoi dong may,
        /// dang xuat thang ra man hinh ngoai, driver/monitor chua init xong).
        /// </summary>
        public static void WarmUp() => EnsureBaseCursorsLoaded();

        private static void EnsureBaseCursorsLoaded()
        {
            if (_baseCursorsReady) return;

            lock (_initLock)
            {
                if (_baseCursorsReady) return;

                const int maxAttempts = 6;
                const int delayMs = 250;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    IntPtr arrow = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW));
                    IntPtr hand = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                    IntPtr ibeam = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_IBEAM));

                    bool arrowOk = IsCursorSane(arrow, out string arrowInfo);
                    bool handOk = IsCursorSane(hand, out string handInfo);
                    bool ibeamOk = IsCursorSane(ibeam, out string ibeamInfo);

                    Log($"EnsureBaseCursorsLoaded lan {attempt}/{maxAttempts}: " +
                        $"DPI he thong={GetDpiForSystem()} | arrow: {arrowInfo} | hand: {handInfo} | ibeam: {ibeamInfo}");

                    if (arrowOk && handOk && ibeamOk)
                    {
                        _origArrowHandle = arrow;
                        _origHandHandle = hand;
                        _origIBeamHandle = ibeam;
                        _baseCursorsReady = true;
                        Log("EnsureBaseCursorsLoaded: OK, cursor goc da san sang.");
                        return;
                    }

                    if (arrow != IntPtr.Zero) DestroyIcon(arrow);
                    if (hand != IntPtr.Zero) DestroyIcon(hand);
                    if (ibeam != IntPtr.Zero) DestroyIcon(ibeam);

                    if (attempt < maxAttempts) Thread.Sleep(delayMs);
                }

                // Het so lan thu cho phep: van nap 1 bo handle cuoi cung de app
                // khong bi "cam" hoan toan, nhung da log lai ro rang de biet la
                // truong hop bat thuong (neu van gap thi day chinh la bang chung
                // can gui lai de dieu tra tiep).
                _origArrowHandle = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW));
                _origHandHandle = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                _origIBeamHandle = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)IDC_IBEAM));
                _baseCursorsReady = true;
                Log("EnsureBaseCursorsLoaded: het luot thu, dung tam bo handle cuoi cung (co the chua ly tuong).");
            }
        }

        private static bool IsCursorSane(IntPtr hCursor, out string info)
        {
            info = "handle=NULL";
            if (hCursor == IntPtr.Zero) return false;

            if (!GetIconInfo(hCursor, out ICONINFO iconInfo))
            {
                info = "GetIconInfo that bai";
                return false;
            }

            try
            {
                bool isColor = iconInfo.hbmColor != IntPtr.Zero;
                IntPtr srcBmp = isColor ? iconInfo.hbmColor : iconInfo.hbmMask;
                if (srcBmp == IntPtr.Zero)
                {
                    info = "khong co bitmap nao (ca color lan mask deu null)";
                    return false;
                }

                var bmpInfo = new BITMAP();
                if (GetObject(srcBmp, Marshal.SizeOf<BITMAP>(), ref bmpInfo) == 0)
                {
                    info = "GetObject that bai";
                    return false;
                }

                int width = bmpInfo.bmWidth;
                int height = bmpInfo.bmHeight;
                if (!isColor)
                {
                    // Cursor don sac: hbmMask gop ca mat na AND (nua tren) va XOR (nua duoi)
                    if (height % 2 != 0)
                    {
                        info = $"mono nhung bmHeight le ({height}) -> bat thuong";
                        return false;
                    }
                    height /= 2;
                }

                info = $"{(isColor ? "mau" : "don sac")} {width}x{height}";
                return width is > 0 and <= 256 && height is > 0 and <= 256;
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
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

                // Calculate destination size and offsets using Math.Round to prevent sub-pixel rendering blur
                int destW = (int)Math.Round(colorBmp.Width * scale);
                int destH = (int)Math.Round(colorBmp.Height * scale);
                int offsetX = (int)Math.Round(info.xHotspot * (1.0 - scale));
                int offsetY = (int)Math.Round(info.yHotspot * (1.0 - scale));

                using var canvas = new Bitmap(colorBmp.Width, colorBmp.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);

                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                    // Use ImageAttributes to prevent edge bleeding (ringing artifacts)
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
                // Cursor don sac (VD: khi Windows chua load xong theme cursor mau luc
                // boot thang ra man hinh ngoai): hbmMask la 1 bitmap 1-bit CAO GAP DOI,
                // nua tren la AND mask, nua duoi la XOR mask. Neu doc thang cai nay nhu
                // 1 bitmap mau 32-bit binh thuong (nhu code cu tung lam) se ra hinh vo/nhieu.
                if (height % 2 != 0)
                {
                    Log($"ExtractColorBitmap: cursor don sac nhung bmHeight le ({height}) -> bo qua.");
                    return null;
                }
                height /= 2;
            }

            if (width <= 0 || height <= 0 || width > 256 || height > 256)
            {
                Log($"ExtractColorBitmap: kich thuoc bat thuong width={width} height={height} isColor={isColor} -> bo qua.");
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

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        /// <summary>
        /// Giai ma cursor don sac (AND/XOR mask) thanh ARGB that su thay vi
        /// doc nham mask 1-bit (cao gap doi) nhu the no la bitmap mau 32-bit.
        /// </summary>
        private static Bitmap? ExtractMonochromeArgb(IntPtr hbmMask, int width, int height)
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -(height * 2); // top-down, doc ca AND + XOR
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 1;
            bmi.bmiHeader.biCompression = 0;

            int maskStride = ((width + 31) / 32) * 4; // DIB 1bpp luon can theo 4 byte (DWORD)
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
                    if (!andBit && !xorBit) { a = 255; c = 0; }       // den, duc
                    else if (!andBit && xorBit) { a = 255; c = 255; } // trang, duc
                    else if (andBit && !xorBit) { a = 0; c = 0; }     // trong suot
                    else { a = 255; c = 128; }                       // hiem gap (invert) -> xam

                    int i = y * argbStride + x * 4;
                    argb[i + 0] = c; // B
                    argb[i + 1] = c; // G
                    argb[i + 2] = c; // R
                    argb[i + 3] = a; // A
                }
            }

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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
                // Ghi log loi thi bo qua, khong duoc lam crash chuong trinh chinh.
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