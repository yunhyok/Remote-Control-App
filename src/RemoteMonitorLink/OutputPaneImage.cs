using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteMonitorLink
{
    internal static class OutputPaneImage
    {
        // ponytail: temporary PowerSI flat neutral-background heuristic, not semantic pane identification.
        // Textured/colored panes or panes covering >=90% of the frame need verified UIA geometry instead.
        internal static Rectangle FindBody(PowerSiFrame frame, Rectangle suggested, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateFrame(frame);
            int width = frame.PixelSize.Width, height = frame.PixelSize.Height;
            if (!Inside(suggested, frame.PixelSize) || suggested.Size == frame.PixelSize)
                throw new LocalVisionException("REGION_BOUNDARY_UNCONFIRMED");
            var colors = NeutralColors(frame, cancellation);
            var queue = new int[colors.Length];
            var center = new Point(suggested.X + suggested.Width / 2, suggested.Y + suggested.Height / 2);
            Rectangle? found = null;
            for (int start = 0; start < colors.Length; start++)
            {
                if ((start & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                byte color = colors[start];
                if (color == 0) continue;
                int head = 0, tail = 1, left = start % width, right = left, top = start / width, bottom = top;
                queue[0] = start; colors[start] = 0;
                while (head < tail)
                {
                    if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int pixel = queue[head++], x = pixel % width, y = pixel / width;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (x > 0) Visit(pixel - 1, color, colors, queue, ref tail);
                    if (x + 1 < width) Visit(pixel + 1, color, colors, queue, ref tail);
                    if (y > 0) Visit(pixel - width, color, colors, queue, ref tail);
                    if (y + 1 < height) Visit(pixel + width, color, colors, queue, ref tail);
                }
                var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
                long area = (long)bounds.Width * bounds.Height;
                if (bounds.Width < 120 || bounds.Height < 80 || tail < 10000 || tail * 100L < area * 55 ||
                    area * 10 >= (long)width * height * 9 || !bounds.Contains(center)) continue;
                var overlap = Rectangle.Intersect(bounds, suggested);
                if ((long)overlap.Width * overlap.Height * 2 < Math.Min(area, (long)suggested.Width * suggested.Height)) continue;
                if (found.HasValue) throw new LocalVisionException("REGION_BOUNDARY_UNCONFIRMED");
                found = bounds;
            }
            cancellation.ThrowIfCancellationRequested();
            if (!found.HasValue) throw new LocalVisionException("REGION_BOUNDARY_UNCONFIRMED");
            return found.Value;
        }

        private static void Visit(int pixel, byte color, byte[] colors, int[] queue, ref int tail)
        {
            if (colors[pixel] != color) return;
            colors[pixel] = 0;
            queue[tail++] = pixel;
        }

        private static byte[] NeutralColors(PowerSiFrame frame, CancellationToken cancellation)
        {
            using (var stream = new MemoryStream(frame.Png, false))
            using (var bitmap = new Bitmap(stream))
            {
                if (bitmap.Size != frame.PixelSize) throw new LocalVisionException("IMAGE_INVALID");
                int width = bitmap.Width, height = bitmap.Height;
                var colors = new byte[width * height];
                var row = new byte[width * 3];
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                        for (int x = 0; x < width; x++)
                        {
                            int blue = row[x * 3], green = row[x * 3 + 1], red = row[x * 3 + 2];
                            if (Math.Abs(red - green) <= 3 && Math.Abs(green - blue) <= 3)
                                colors[y * width + x] = (byte)(1 + red / 8);
                        }
                    }
                }
                finally { bitmap.UnlockBits(bits); }
                return colors;
            }
        }

        internal static byte[] OcrInput(PowerSiFrame frame, Rectangle body)
        {
            ValidateFrame(frame);
            // Reuse the source-pixel crop validation before selecting the bottom visible rows.
            byte[] cropped = PowerSiScreenCapture.Crop(frame, body);
            using (var stream = new MemoryStream(cropped, false))
            using (var source = new Bitmap(stream))
            {
                int height = Math.Min(256, source.Height), scale = source.Width <= 2048 ? 2 : 1;
                using (var output = new Bitmap(source.Width * scale, height * scale, PixelFormat.Format24bppRgb))
                using (var graphics = Graphics.FromImage(output))
                using (var png = new MemoryStream())
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height),
                        new Rectangle(0, source.Height - height, source.Width, height), GraphicsUnit.Pixel);
                    output.Save(png, ImageFormat.Png);
                    return png.ToArray();
                }
            }
        }

        private static bool Inside(Rectangle bounds, Size size)
        {
            return bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
                bounds.Width <= size.Width && bounds.Height <= size.Height &&
                bounds.X <= size.Width - bounds.Width && bounds.Y <= size.Height - bounds.Height;
        }

        private static void ValidateFrame(PowerSiFrame frame)
        {
            if (frame == null || frame.Png == null || frame.Png.Length < 33 || frame.Png.Length > 8 * 1024 * 1024)
                throw new LocalVisionException("IMAGE_INVALID");
            byte[] header = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
            for (int i = 0; i < header.Length; i++)
                if (frame.Png[i] != header[i]) throw new LocalVisionException("IMAGE_INVALID");
            long width = 0, height = 0;
            for (int i = 16; i < 20; i++) { width = (width << 8) + frame.Png[i]; height = (height << 8) + frame.Png[i + 4]; }
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || width * height > 16 * 1024 * 1024 ||
                width != frame.PixelSize.Width || height != frame.PixelSize.Height)
                throw new LocalVisionException("IMAGE_INVALID");
        }

        internal static void SelfTest()
        {
            void Check(bool passed, string name)
            { if (!passed) throw new InvalidOperationException("Output pane image self-test: " + name); }
            void Reject(Action action, string code = "REGION_BOUNDARY_UNCONFIRMED")
            {
                try { action(); }
                catch (LocalVisionException ex) when (ex.Code == code) { return; }
                throw new InvalidOperationException("Output pane image accepted invalid input: " + code);
            }
            PowerSiFrame Frame(Bitmap bitmap)
            {
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, ImageFormat.Png);
                    return new PowerSiFrame { Png = png.ToArray(), PixelSize = bitmap.Size, CapturedUtc = DateTime.UtcNow };
                }
            }
            var body = new Rectangle(20, 40, 360, 320);
            var suggested = new Rectangle(100, 150, 360, 160);
            foreach (int grey in new[] { 40, 232 })
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var background = new SolidBrush(Color.FromArgb(grey, grey, grey)))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(background, body);
                    graphics.FillRectangle(background, new Rectangle(410, 40, 170, 320));
                    for (int y = 55; y < 350; y += 16) graphics.FillRectangle(Brushes.Black, 30, y, 300, 2);
                }
                var frame = Frame(bitmap);
                Check(FindBody(frame, suggested, CancellationToken.None) == body, "partial proposal expands to full body without adjacent pane");
                Reject(() => FindBody(frame, new Rectangle(1, 1, 10, 10), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(-1, 40, 360, 320), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(int.MaxValue, 40, 360, 320), CancellationToken.None));
                Reject(() => FindBody(frame, new Rectangle(0, 0, 600, 420), CancellationToken.None));
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try { FindBody(frame, suggested, canceled.Token); throw new InvalidOperationException("Canceled image scan ran."); }
                    catch (OperationCanceledException) { }
                }
                frame.PixelSize = new Size(4097, 420);
                Reject(() => FindBody(frame, suggested, CancellationToken.None), "IMAGE_INVALID");
            }
            using (var bitmap = new Bitmap(600, 420, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.DarkBlue);
                    graphics.FillRectangle(Brushes.White, 30, 30, 400, 350);
                    graphics.FillRectangle(Brushes.Gray, 130, 125, 160, 140);
                }
                Reject(() => FindBody(Frame(bitmap), new Rectangle(140, 140, 120, 100), CancellationToken.None));
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                Reject(() => FindBody(Frame(bitmap), suggested, CancellationToken.None));
            }
            using (var bitmap = new Bitmap(24, 320, PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, Color.FromArgb(x * 9, y % 256, (x + y) % 256));
                var region = new Rectangle(3, 8, 12, 300);
                using (var png = new MemoryStream(OcrInput(Frame(bitmap), region), false))
                using (var output = new Bitmap(png))
                {
                    Check(output.Size == new Size(24, 512), "bottom 256 source rows enlarged exactly twice");
                    for (int y = 0; y < output.Height; y++)
                        for (int x = 0; x < output.Width; x++)
                            Check(output.GetPixel(x, y) == bitmap.GetPixel(region.X + x / 2, region.Bottom - 256 + y / 2),
                                "nearest-neighbor duplicates source pixels including newest bottom row");
                }
            }
            foreach (int width in new[] { 120, 2048, 2049, 4096 })
            using (var bitmap = new Bitmap(width, 80, PixelFormat.Format24bppRgb))
            using (var png = new MemoryStream(OcrInput(Frame(bitmap), new Rectangle(0, 10, width, 60)), false))
            using (var output = new Bitmap(png))
            {
                int scale = width <= 2048 ? 2 : 1;
                Check(output.Size == new Size(width * scale, 60 * scale), "short body and maximum OCR dimensions");
            }
        }
    }
}
