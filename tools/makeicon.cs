// ============================================================================
//  makeicon.cs - generates app.ico for G-Wolves-Driver-Tray
//
//  One-off build helper. Compile with the .NET Framework 4.x csc.exe and run:
//      csc /nologo /target:exe /out:makeicon.exe makeicon.cs
//      makeicon.exe app.ico
//
//  Writes a multi-resolution .ico (16/24/32/48/64/128/256) whose entries are
//  PNG-compressed (supported since Vista). Drawn with GDI+, no assets needed.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = radius * 2f;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 0f) d = 1f;
        p.AddArc(r.X, r.Y, d, d, 180f, 90f);
        p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        p.CloseFigure();
        return p;
    }

    private static Bitmap Draw(int s)
    {
        Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // ---- rounded dark background plate ------------------------------
            float pad = s * 0.03f;
            RectangleF bg = new RectangleF(pad, pad, s - pad * 2f, s - pad * 2f);
            using (GraphicsPath p = RoundRect(bg, s * 0.23f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 0x22, 0x29, 0x33)))
                g.FillPath(b, p);

            if (s >= 24)
            {
                using (GraphicsPath p = RoundRect(bg, s * 0.23f))
                using (Pen pen = new Pen(Color.FromArgb(255, 0x3C, 0x49, 0x59), Math.Max(1f, s * 0.022f)))
                    g.DrawPath(pen, p);
            }

            // ---- battery (horizontal) --------------------------------------
            float bodyW = s * 0.58f;
            float bodyH = s * 0.32f;
            float nubW = s * 0.055f;
            float nubH = bodyH * 0.42f;

            float totalW = bodyW + nubW;
            float x0 = (s - totalW) * 0.5f;
            float cy = s * 0.5f;

            float stroke = Math.Max(1.2f, s * 0.052f);
            Color shell = Color.FromArgb(255, 0xE9, 0xEF, 0xF7);
            Color juice = Color.FromArgb(255, 0x36, 0xD2, 0x82);

            RectangleF body = new RectangleF(x0, cy - bodyH * 0.5f, bodyW, bodyH);

            // nub first, so the body outline overlaps it cleanly
            if (s >= 24)
            {
                RectangleF nub = new RectangleF(x0 + bodyW - stroke * 0.5f,
                                                cy - nubH * 0.5f, nubW + stroke * 0.5f, nubH);
                using (GraphicsPath p = RoundRect(nub, nubH * 0.30f))
                using (SolidBrush b = new SolidBrush(shell))
                    g.FillPath(b, p);
            }

            // charge fill (about 75%)
            float inset = stroke * 0.5f + Math.Max(0.8f, s * 0.030f);
            RectangleF inner = new RectangleF(body.X + inset, body.Y + inset,
                                              body.Width - inset * 2f, body.Height - inset * 2f);
            float fillW = inner.Width * 0.75f;
            RectangleF juiceRect = new RectangleF(inner.X, inner.Y, fillW, inner.Height);
            using (GraphicsPath p = RoundRect(juiceRect, s * 0.030f))
            using (SolidBrush b = new SolidBrush(juice))
                g.FillPath(b, p);

            // body outline
            using (GraphicsPath p = RoundRect(body, s * 0.075f))
            using (Pen pen = new Pen(shell, stroke))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawPath(pen, p);
            }
        }
        return bmp;
    }

    private static byte[] Png(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static void WriteIco(string path, int[] sizes)
    {
        List<byte[]> blobs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap bmp = Draw(s))
                blobs.Add(Png(bmp));
        }

        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);                 // reserved
            w.Write((ushort)1);                 // type = icon
            w.Write((ushort)sizes.Length);      // image count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));   // width  (0 means 256)
                w.Write((byte)(s >= 256 ? 0 : s));   // height
                w.Write((byte)0);                    // palette count
                w.Write((byte)0);                    // reserved
                w.Write((ushort)1);                  // colour planes
                w.Write((ushort)32);                 // bits per pixel
                w.Write((uint)blobs[i].Length);      // size of image data
                w.Write((uint)offset);               // offset of image data
                offset += blobs[i].Length;
            }
            foreach (byte[] blob in blobs)
                w.Write(blob);
        }
    }

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        try
        {
            WriteIco(outPath, new int[] { 16, 24, 32, 48, 64, 128, 256 });
            FileInfo fi = new FileInfo(outPath);
            Console.WriteLine("wrote " + fi.FullName + "  (" + fi.Length + " bytes)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }
}
